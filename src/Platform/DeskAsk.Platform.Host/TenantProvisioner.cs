using System.Text.RegularExpressions;
using DeskAsk.Platform.Tenancy;
using Npgsql;

namespace DeskAsk.Platform;

/// <summary>
/// Creates customers from inside the running platform (the store's trials and purchases), the same way "tenant create" does: database,
/// catalog row (off), every module's schema, then on. Needs the platform catalog (Platform:CatalogConnection; tests use Licensing:CatalogConnection)
/// and a login that may create databases (Platform:ProvisionConnection, default the catalog login).
/// </summary>
public sealed class TenantProvisioner : ITenantProvisioner
{
    readonly IConfiguration cfg; readonly IServiceScopeFactory scopes; readonly TenantSchemas schemas;
    public TenantProvisioner(IConfiguration cfg, IServiceScopeFactory scopes, TenantSchemas schemas) { this.cfg = cfg; this.scopes = scopes; this.schemas = schemas; }

    string Catalog => cfg["Platform:CatalogConnection"] is { Length: > 0 } c ? c : cfg["Licensing:CatalogConnection"] ?? throw new InvalidOperationException("This installation has no platform catalog.");
    static string DbName(string id) => "deskask_" + id.Replace('-', '_');

    public async Task<bool> ExistsAsync(string id, string host, CancellationToken ct = default)
    {
        await SchemaMigrator.RunAsync(Catalog, "catalog", CatalogSchema.Migrations, ct);
        await using var db = new NpgsqlConnection(Catalog); await db.OpenAsync(ct);
        await using var q = new NpgsqlCommand("select 1 from platform.tenants where id = @i or lower(host) = @h", db);
        q.Parameters.AddWithValue("i", id); q.Parameters.AddWithValue("h", HostName.Normalize(host));
        return await q.ExecuteScalarAsync(ct) != null;
    }

    public async Task<TenantInfo> CreateAsync(string id, string name, string host, CancellationToken ct = default)
    {
        id = id.ToLowerInvariant(); host = HostName.Normalize(host);
        if (!Regex.IsMatch(id, "^[a-z][a-z0-9-]{1,30}$")) throw new ArgumentException("The customer id has 2 to 31 lower case letters, digits and '-', starting with a letter.");
        var dbName = DbName(id);
        if (await ExistsAsync(id, host, ct)) throw new ArgumentException("The name '" + id + "' is already taken.");
        var template = cfg["Platform:TenantConnectionTemplate"];
        var tenantConn = !string.IsNullOrWhiteSpace(template) ? template.Replace("{db}", dbName) : new NpgsqlConnectionStringBuilder(Catalog) { Database = dbName }.ConnectionString;

        await using (var admin = new NpgsqlConnection(cfg["Platform:ProvisionConnection"] is { Length: > 0 } p ? p : Catalog))
        {
            await admin.OpenAsync(ct);
            await using var exists = new NpgsqlCommand("select 1 from pg_database where datname = @n", admin);
            exists.Parameters.AddWithValue("n", dbName);
            if (await exists.ExecuteScalarAsync(ct) != null) throw new ArgumentException("A database for '" + id + "' already exists.");
            await using var create = new NpgsqlCommand("create database \"" + dbName + "\"", admin);   // checked above: letters, digits, '_'
            await create.ExecuteNonQueryAsync(ct);
        }
        await using (var db = new NpgsqlConnection(Catalog))
        {
            await db.OpenAsync(ct);
            await using var ins = new NpgsqlCommand("insert into platform.tenants (id, name, host, connection_string, active) values (@i, @n, @h, @c, false)", db);
            ins.Parameters.AddWithValue("i", id); ins.Parameters.AddWithValue("n", name); ins.Parameters.AddWithValue("h", host); ins.Parameters.AddWithValue("c", tenantConn);
            await ins.ExecuteNonQueryAsync(ct);
        }
        var t = new TenantInfo(id, name, host, tenantConn);
        await using (var scope = scopes.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Current = t;
            ProvisioningScope.NoBootstrapAdmin.Value = true;
            try { await schemas.EnsureAsync(scope.ServiceProvider, t, ct); }
            finally { ProvisioningScope.NoBootstrapAdmin.Value = false; }
        }
        await using (var db = new NpgsqlConnection(Catalog))
        {
            await db.OpenAsync(ct);
            await using var on = new NpgsqlCommand("update platform.tenants set active = true where id = @i", db);
            on.Parameters.AddWithValue("i", id); await on.ExecuteNonQueryAsync(ct);
        }
        return t;
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        if (!Regex.IsMatch(id, "^[a-z][a-z0-9-]{1,30}$")) throw new ArgumentException("Not a customer id.");
        string? conn = null;
        await using (var db = new NpgsqlConnection(Catalog))
        {
            await db.OpenAsync(ct);
            await using (var q = new NpgsqlCommand("select connection_string from platform.tenants where id = @i", db))
            { q.Parameters.AddWithValue("i", id); conn = await q.ExecuteScalarAsync(ct) as string; }
            await using var del = new NpgsqlCommand("delete from platform.licenses where tenant_id = @i; delete from platform.tenants where id = @i", db);
            del.Parameters.AddWithValue("i", id); await del.ExecuteNonQueryAsync(ct);
        }
        var dbName = conn is { Length: > 0 } ? new NpgsqlConnectionStringBuilder(conn).Database : DbName(id);
        if (dbName == null || !Regex.IsMatch(dbName, "^[a-z][a-z0-9_]{1,62}$")) return;
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(cfg["Platform:ProvisionConnection"] is { Length: > 0 } p ? p : Catalog);
        await admin.OpenAsync(ct);
        await using var drop = new NpgsqlCommand("drop database if exists \"" + dbName + "\" with (force)", admin);
        await drop.ExecuteNonQueryAsync(ct);
    }
}
