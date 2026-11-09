using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DeskAsk.Platform.Tenancy;
using Npgsql;

namespace DeskAsk.Platform;

/// <summary>
/// Creating and looking after customers from the command line (docs/provisioning.md):
///   tenant init                                   make the catalog tables
///   tenant create --id acme --host acme.deskask.in --admin-email a@acme.com [--name "Acme Ltd"] [--db deskask_acme] [--admin-password-env VAR]
///   tenant list | enable --id X | disable --id X
///   tenant migrate                                bring every active customer's database up to this version
/// Needs Platform:CatalogConnection. "create" also needs a database login that may create databases (Platform:ProvisionConnection, default: the catalog login).
/// </summary>
public static class TenantCli
{
    public static async Task<int> RunAsync(string[] args, WebApplicationBuilder builder, TextWriter? output = null, CancellationToken ct = default)
    {
        var o = output ?? Console.Out;
        if (args.Length == 0 || args[0] is "help" or "--help") { Help(o); return args.Length == 0 ? 1 : 0; }
        var cmd = args[0];
        var opt = Parse(args[1..]);
        var catalogConn = builder.Configuration["Platform:CatalogConnection"];
        if (string.IsNullOrWhiteSpace(catalogConn)) { await o.WriteLineAsync("Platform:CatalogConnection is not set. It is the connection to the platform catalog database."); return 2; }
        try
        {
            return cmd switch
            {
                "init" => await Init(catalogConn, o, ct),
                "list" => await List(catalogConn, o, ct),
                "enable" => await SetActive(catalogConn, opt, true, o, ct),
                "disable" => await SetActive(catalogConn, opt, false, o, ct),
                "migrate" => await Migrate(builder, catalogConn, o, ct),
                "create" => await Create(builder, catalogConn, opt, o, ct),
                _ => Unknown(cmd, o),
            };
        }
        catch (ArgumentException e) { await o.WriteLineAsync("Error: " + e.Message); return 2; }
        catch (Exception e) { await o.WriteLineAsync("Failed: " + e.Message); return 1; }
    }

    static int Unknown(string cmd, TextWriter o) { o.WriteLine("Unknown command '" + cmd + "'."); Help(o); return 2; }

    static void Help(TextWriter o) => o.WriteLine(@"tenant init
tenant create --id acme --host acme.deskask.in --admin-email admin@acme.com [--name ""Acme Ltd""] [--db deskask_acme] [--admin-password-env VAR]
tenant list
tenant enable --id acme
tenant disable --id acme
tenant migrate");

    static Dictionary<string, string> Parse(string[] a)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--")) throw new ArgumentException("Unexpected '" + a[i] + "'.");
            if (i + 1 >= a.Length || a[i + 1].StartsWith("--")) throw new ArgumentException("Option " + a[i] + " needs a value.");
            d[a[i][2..]] = a[++i];
        }
        return d;
    }

    static string Need(Dictionary<string, string> o, string key) =>
        o.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : throw new ArgumentException("--" + key + " is needed.");

    static async Task<int> Init(string catalogConn, TextWriter o, CancellationToken ct)
    {
        var n = await SchemaMigrator.RunAsync(catalogConn, "catalog", CatalogSchema.Migrations, ct);
        await o.WriteLineAsync(n == 0 ? "The catalog is up to date." : "The catalog is ready (" + n + " step(s) applied).");
        return 0;
    }

    static async Task<int> List(string catalogConn, TextWriter o, CancellationToken ct)
    {
        await SchemaMigrator.RunAsync(catalogConn, "catalog", CatalogSchema.Migrations, ct);
        await using var db = new NpgsqlConnection(catalogConn); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("select id, name, host, active, created_at from platform.tenants order by id", db);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        var n = 0;
        while (await rd.ReadAsync(ct)) { n++; await o.WriteLineAsync($"{rd.GetString(0),-20} {rd.GetString(2),-34} {(rd.GetBoolean(3) ? "active" : "disabled"),-9} {rd.GetString(1)}"); }
        if (n == 0) await o.WriteLineAsync("No customers yet.");
        return 0;
    }

    static async Task<int> SetActive(string catalogConn, Dictionary<string, string> opt, bool active, TextWriter o, CancellationToken ct)
    {
        var id = Need(opt, "id");
        await SchemaMigrator.RunAsync(catalogConn, "catalog", CatalogSchema.Migrations, ct);
        await using var db = new NpgsqlConnection(catalogConn); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("update platform.tenants set active = @a where id = @i", db);
        cmd.Parameters.AddWithValue("a", active); cmd.Parameters.AddWithValue("i", id);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) { await o.WriteLineAsync("No customer '" + id + "'."); return 1; }
        await o.WriteLineAsync("Customer '" + id + "' is now " + (active ? "active." : "disabled. Its data is kept; its address stops answering."));
        return 0;
    }

    /// <summary>Runs every module's migrations (and the first-use seeding) for one customer.</summary>
    static async Task PrepareAsync(IServiceProvider root, TenantInfo t, CancellationToken ct)
    {
        await using var scope = root.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Current = t;
        await root.GetRequiredService<TenantSchemas>().EnsureAsync(scope.ServiceProvider, t, ct);
    }

    static async Task<int> Migrate(WebApplicationBuilder builder, string catalogConn, TextWriter o, CancellationToken ct)
    {
        await SchemaMigrator.RunAsync(catalogConn, "catalog", CatalogSchema.Migrations, ct);
        PlatformSetup.Add(builder);
        await using var app = builder.Build();
        var failed = 0;
        foreach (var t in await app.Services.GetRequiredService<ITenantCatalog>().ListActiveAsync(ct))
        {
            try { await PrepareAsync(app.Services, t, ct); await o.WriteLineAsync("ok      " + t.Id); }
            catch (Exception e) { failed++; await o.WriteLineAsync("FAILED  " + t.Id + ": " + e.Message); }
        }
        return failed == 0 ? 0 : 1;
    }

    static async Task<int> Create(WebApplicationBuilder builder, string catalogConn, Dictionary<string, string> opt, TextWriter o, CancellationToken ct)
    {
        var id = Need(opt, "id").ToLowerInvariant();
        var host = HostName.Normalize(Need(opt, "host"));
        var email = Need(opt, "admin-email").ToLowerInvariant();
        var name = opt.TryGetValue("name", out var nm) && nm.Trim() != "" ? nm.Trim() : id;
        var dbName = opt.TryGetValue("db", out var d) ? d.Trim() : "deskask_" + id.Replace('-', '_');
        if (!Regex.IsMatch(id, "^[a-z][a-z0-9-]{1,30}$")) throw new ArgumentException("--id: 2 to 31 characters, lower case letters, digits and '-', starting with a letter.");
        if (!Regex.IsMatch(host, "^[a-z0-9]([a-z0-9.-]*[a-z0-9])?$")) throw new ArgumentException("--host is not a valid address.");
        if (!Regex.IsMatch(dbName, "^[a-z][a-z0-9_]{1,62}$")) throw new ArgumentException("--db: lower case letters, digits and '_', starting with a letter.");
        if (!Regex.IsMatch(email, "^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$")) throw new ArgumentException("--admin-email is not an email address.");

        string? generated = null, password;
        if (opt.TryGetValue("admin-password-env", out var envName))
        {
            password = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrEmpty(password)) throw new ArgumentException("The environment variable " + envName + " is empty or missing.");
        }
        else password = generated = NewPassword();

        await SchemaMigrator.RunAsync(catalogConn, "catalog", CatalogSchema.Migrations, ct);
        var resume = false;
        await using (var db = new NpgsqlConnection(catalogConn))
        {
            await db.OpenAsync(ct);
            await using var q = new NpgsqlCommand("select id, host, active from platform.tenants where id = @i or host = @h", db);
            q.Parameters.AddWithValue("i", id); q.Parameters.AddWithValue("h", host);
            await using var rd = await q.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                if (rd.GetString(0) == id && rd.GetString(1) == host && !rd.GetBoolean(2)) resume = true;   // an earlier create stopped half way: carry on
                else { await o.WriteLineAsync("A customer with this id or address already exists (" + rd.GetString(0) + ", " + rd.GetString(1) + ")."); return 1; }
            }
        }

        var template = builder.Configuration["Platform:TenantConnectionTemplate"];
        var tenantConn = !string.IsNullOrWhiteSpace(template) ? template.Replace("{db}", dbName) : new NpgsqlConnectionStringBuilder(catalogConn) { Database = dbName }.ConnectionString;

        // 1. the database
        await using (var admin = new NpgsqlConnection(builder.Configuration["Platform:ProvisionConnection"] ?? catalogConn))
        {
            await admin.OpenAsync(ct);
            await using var exists = new NpgsqlCommand("select 1 from pg_database where datname = @n", admin);
            exists.Parameters.AddWithValue("n", dbName);
            if (await exists.ExecuteScalarAsync(ct) == null)
            {
                await using var create = new NpgsqlCommand("create database \"" + dbName + "\"", admin);   // the name was checked above: letters, digits, '_'
                await create.ExecuteNonQueryAsync(ct);
            }
            else if (!resume) { await o.WriteLineAsync("The database '" + dbName + "' already exists. Use --db for another name, or remove it first."); return 1; }
        }

        // 2. the catalog row, not active yet
        await using (var db = new NpgsqlConnection(catalogConn))
        {
            await db.OpenAsync(ct);
            await using var ins = new NpgsqlCommand("insert into platform.tenants (id, name, host, connection_string, active) values (@i, @n, @h, @c, false) on conflict (id) do nothing", db);
            ins.Parameters.AddWithValue("i", id); ins.Parameters.AddWithValue("n", name); ins.Parameters.AddWithValue("h", host); ins.Parameters.AddWithValue("c", tenantConn);
            await ins.ExecuteNonQueryAsync(ct);
        }

        // 3. every module's schema and the first administrator
        builder.Configuration["Platform:BootstrapAdmin:Email"] = email;
        builder.Configuration["Platform:BootstrapAdmin:Password"] = password;
        builder.Configuration["Platform:BootstrapAdmin:Name"] = "Administrator";
        PlatformSetup.Add(builder);
        await using (var app = builder.Build())
            await PrepareAsync(app.Services, new TenantInfo(id, name, host, tenantConn), ct);

        // 4. switch it on
        await using (var db = new NpgsqlConnection(catalogConn))
        {
            await db.OpenAsync(ct);
            await using var on = new NpgsqlCommand("update platform.tenants set active = true where id = @i", db);
            on.Parameters.AddWithValue("i", id); await on.ExecuteNonQueryAsync(ct);
        }

        await o.WriteLineAsync($"Customer '{id}' created. Address: https://{host}  Database: {dbName}");
        await o.WriteLineAsync("First administrator: " + email);
        if (generated != null) await o.WriteLineAsync("Password: " + generated + "   (shown once; change it after the first sign-in)");
        return 0;
    }

    static string NewPassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return new string(Enumerable.Range(0, 20).Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
    }
}
