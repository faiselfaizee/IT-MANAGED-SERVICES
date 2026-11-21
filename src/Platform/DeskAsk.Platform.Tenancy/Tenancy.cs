using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.Tenancy;

public sealed class TenantContext : ITenantContext
{
    public TenantInfo? Current { get; set; }
    public TenantInfo Require() => Current ?? throw new InvalidOperationException("No customer is set for this request.");
}

public static class HostName
{
    /// <summary>"Acme.DeskAsk.in:443" becomes "acme.deskask.in".</summary>
    public static string Normalize(string? host)
    {
        var h = (host ?? "").Trim().ToLowerInvariant();
        var c = h.LastIndexOf(':');
        if (c > 0 && !h.EndsWith(']')) h = h[..c];
        return h.TrimEnd('.');
    }
}

public sealed class InMemoryTenantCatalog : ITenantCatalog, ITenantDomains
{
    readonly Dictionary<string, TenantInfo> byHost;
    readonly List<TenantInfo> all;
    sealed record Dom(string Tenant, string Host, string Token, DateTime? Verified, bool Primary);
    readonly List<Dom> domains = new();
    readonly object gate = new();
    public InMemoryTenantCatalog(IEnumerable<TenantInfo> tenants)
    {
        all = tenants.ToList();
        byHost = all.ToDictionary(t => HostName.Normalize(t.Host), t => t);
    }
    TenantInfo Shown(TenantInfo t) { lock (gate) { var p = domains.FirstOrDefault(d => d.Tenant == t.Id && d.Primary && d.Verified != null); return p == null ? t : t with { Host = p.Host }; } }
    public Task<TenantInfo?> FindByHostAsync(string host, CancellationToken ct = default)
    {
        var h = HostName.Normalize(host);
        TenantInfo? t = null;
        if (byHost.TryGetValue(h, out var own)) t = own;
        else lock (gate) { var d = domains.FirstOrDefault(x => x.Host == h && x.Verified != null); if (d != null) t = all.FirstOrDefault(x => x.Id == d.Tenant); }
        return Task.FromResult(t is { Active: true } ? Shown(t) : null);
    }
    public Task<IReadOnlyList<TenantInfo>> ListActiveAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TenantInfo>>(all.Where(t => t.Active).Select(Shown).ToList());

    public Task<IReadOnlyList<DomainRef>> ListAsync(string tenantId, CancellationToken ct = default)
    { lock (gate) return Task.FromResult<IReadOnlyList<DomainRef>>(domains.Where(d => d.Tenant == tenantId).Select(d => new DomainRef(d.Host, d.Token, d.Verified != null, d.Primary, d.Verified)).ToList()); }
    public Task<string?> AddAsync(string tenantId, string host, string token, int max, CancellationToken ct = default)
    {
        host = HostName.Normalize(host);
        lock (gate)
        {
            if (byHost.ContainsKey(host) || domains.Any(d => d.Host == host)) return Task.FromResult<string?>("That address is already in use.");
            if (domains.Count(d => d.Tenant == tenantId) >= max) return Task.FromResult<string?>("You can add up to " + max + " domains. Remove one first.");
            domains.Add(new Dom(tenantId, host, token, null, false));
        }
        return Task.FromResult<string?>(null);
    }
    public Task<bool> MarkVerifiedAsync(string tenantId, string host, CancellationToken ct = default)
    {
        host = HostName.Normalize(host);
        lock (gate) { var i = domains.FindIndex(d => d.Tenant == tenantId && d.Host == host); if (i < 0) return Task.FromResult(false); var d = domains[i]; domains[i] = d with { Verified = d.Verified ?? DateTime.UtcNow }; return Task.FromResult(true); }
    }
    public Task<bool> RemoveAsync(string tenantId, string host, CancellationToken ct = default)
    { host = HostName.Normalize(host); lock (gate) return Task.FromResult(domains.RemoveAll(d => d.Tenant == tenantId && d.Host == host) > 0); }
    public Task<bool> SetPrimaryAsync(string tenantId, string host, bool primary, CancellationToken ct = default)
    {
        host = HostName.Normalize(host);
        lock (gate)
        {
            if (!domains.Any(d => d.Tenant == tenantId && d.Host == host && d.Verified != null)) return Task.FromResult(false);
            for (var i = 0; i < domains.Count; i++) if (domains[i].Tenant == tenantId) domains[i] = domains[i] with { Primary = primary && domains[i].Host == host };
            return Task.FromResult(true);
        }
    }
    public Task<string> BuiltInHostAsync(string tenantId, CancellationToken ct = default) => Task.FromResult(all.FirstOrDefault(t => t.Id == tenantId)?.Host ?? "");
    public Task<IReadOnlyList<string>> VerifiedHostsAsync(CancellationToken ct = default)
    { lock (gate) return Task.FromResult<IReadOnlyList<string>>(domains.Where(d => d.Verified != null).Select(d => d.Host).OrderBy(h => h).ToList()); }
}

/// <summary>Reads the platform catalog database: tables platform.tenants(id, name, host, connection_string, active) and platform.tenant_domains.</summary>
public sealed class PgTenantCatalog : ITenantCatalog, ITenantDomains
{
    readonly string conn;
    Task? ready;
    readonly object gate = new();
    public PgTenantCatalog(string connectionString) => conn = connectionString;

    /// <summary>The domains table is made the first time it is needed, so an upgrade does not depend on someone running "tenant migrate" first.</summary>
    Task Ready(CancellationToken ct)
    {
        lock (gate) return ready ??= EnsureAsync();
        async Task EnsureAsync() { try { await SchemaMigrator.RunAsync(conn, "catalog", CatalogSchema.Migrations, CancellationToken.None); } catch { lock (gate) ready = null; throw; } }
    }
    async Task<NpgsqlConnection> Open(CancellationToken ct) { await Ready(ct); var db = new NpgsqlConnection(conn); await db.OpenAsync(ct); return db; }

    const string Shown = "select t.id, t.name, coalesce((select d.host from platform.tenant_domains d where d.tenant_id = t.id and d.is_primary and d.verified_at is not null), t.host), t.connection_string from platform.tenants t";

    public async Task<TenantInfo?> FindByHostAsync(string host, CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var cmd = new NpgsqlCommand(Shown + @" where t.active and (lower(t.host) = @h or exists (select 1 from platform.tenant_domains x where x.tenant_id = t.id and x.host = @h and x.verified_at is not null)) limit 1", db);
        cmd.Parameters.AddWithValue("h", HostName.Normalize(host));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new TenantInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)) : null;
    }

    public async Task<IReadOnlyList<TenantInfo>> ListActiveAsync(CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var cmd = new NpgsqlCommand(Shown + " where t.active order by t.id", db);
        var list = new List<TenantInfo>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new TenantInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        return list;
    }

    public async Task<IReadOnlyList<DomainRef>> ListAsync(string tenantId, CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var c = new NpgsqlCommand("select host, token, verified_at, is_primary from platform.tenant_domains where tenant_id = @t order by created_at, host", db);
        c.Parameters.AddWithValue("t", tenantId);
        var l = new List<DomainRef>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) { var v = r.IsDBNull(2) ? (DateTime?)null : r.GetDateTime(2); l.Add(new DomainRef(r.GetString(0), r.GetString(1), v != null, r.GetBoolean(3), v)); }
        return l;
    }

    public async Task<string?> AddAsync(string tenantId, string host, string token, int max, CancellationToken ct = default)
    {
        host = HostName.Normalize(host);
        await using var db = await Open(ct);
        await using (var n = new NpgsqlCommand("select count(*) from platform.tenant_domains where tenant_id = @t", db))
        { n.Parameters.AddWithValue("t", tenantId); if ((long)(await n.ExecuteScalarAsync(ct))! >= max) return "You can add up to " + max + " domains. Remove one first."; }
        await using (var u = new NpgsqlCommand("select 1 from platform.tenants where lower(host) = @h", db))
        { u.Parameters.AddWithValue("h", host); if (await u.ExecuteScalarAsync(ct) != null) return "That address is already in use."; }
        try
        {
            await using var c = new NpgsqlCommand("insert into platform.tenant_domains (host, tenant_id, token) values (@h, @t, @k)", db);
            c.Parameters.AddWithValue("h", host); c.Parameters.AddWithValue("t", tenantId); c.Parameters.AddWithValue("k", token);
            await c.ExecuteNonQueryAsync(ct);
            return null;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) { return "That address is already in use."; }
    }

    public async Task<bool> MarkVerifiedAsync(string tenantId, string host, CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var c = new NpgsqlCommand("update platform.tenant_domains set verified_at = coalesce(verified_at, now()) where tenant_id = @t and host = @h", db);
        c.Parameters.AddWithValue("t", tenantId); c.Parameters.AddWithValue("h", HostName.Normalize(host));
        return await c.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> RemoveAsync(string tenantId, string host, CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var c = new NpgsqlCommand("delete from platform.tenant_domains where tenant_id = @t and host = @h", db);
        c.Parameters.AddWithValue("t", tenantId); c.Parameters.AddWithValue("h", HostName.Normalize(host));
        return await c.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> SetPrimaryAsync(string tenantId, string host, bool primary, CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var ok = new NpgsqlCommand("select 1 from platform.tenant_domains where tenant_id = @t and host = @h and verified_at is not null", db, tx))
        { ok.Parameters.AddWithValue("t", tenantId); ok.Parameters.AddWithValue("h", HostName.Normalize(host)); if (await ok.ExecuteScalarAsync(ct) == null) return false; }
        await using (var clear = new NpgsqlCommand("update platform.tenant_domains set is_primary = false where tenant_id = @t", db, tx)) { clear.Parameters.AddWithValue("t", tenantId); await clear.ExecuteNonQueryAsync(ct); }
        if (primary)
            await using (var set = new NpgsqlCommand("update platform.tenant_domains set is_primary = true where tenant_id = @t and host = @h", db, tx)) { set.Parameters.AddWithValue("t", tenantId); set.Parameters.AddWithValue("h", HostName.Normalize(host)); await set.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<string> BuiltInHostAsync(string tenantId, CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var c = new NpgsqlCommand("select host from platform.tenants where id = @t", db); c.Parameters.AddWithValue("t", tenantId);
        return await c.ExecuteScalarAsync(ct) as string ?? "";
    }

    public async Task<IReadOnlyList<string>> VerifiedHostsAsync(CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var c = new NpgsqlCommand("select d.host from platform.tenant_domains d join platform.tenants t on t.id = d.tenant_id where d.verified_at is not null and t.active order by d.host", db);
        var l = new List<string>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) l.Add(r.GetString(0));
        return l;
    }
}

/// <summary>Puts the customer of the request address into ITenantContext. Unknown address: 404, nothing else runs.</summary>
public sealed class TenantResolutionMiddleware
{
    readonly RequestDelegate next;
    public TenantResolutionMiddleware(RequestDelegate next) => this.next = next;
    public async Task InvokeAsync(HttpContext http, ITenantCatalog catalog, TenantContext ctx)
    {
        if (http.Request.Path.StartsWithSegments("/health")) { await next(http); return; }
        // the proxy asks before it requests a certificate for an address: 200 only for an address a customer has proved is theirs
        if (http.Request.Path.StartsWithSegments("/api/platform/domain-check"))
        {
            var d = http.RequestServices.GetService<ITenantDomains>();
            var asked = HostName.Normalize(http.Request.Query["domain"].ToString());
            var ok = asked != "" && (await catalog.FindByHostAsync(asked, http.RequestAborted)) != null && d != null;
            http.Response.StatusCode = ok ? StatusCodes.Status200OK : StatusCodes.Status404NotFound;
            return;
        }
        var t = await catalog.FindByHostAsync(http.Request.Host.Host, http.RequestAborted);
        if (t == null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            await http.Response.WriteAsJsonAsync(new { error = "unknown_tenant" });
            return;
        }
        ctx.Current = t;
        await next(http);
    }
}

/// <summary>Lets every module prepare its schema in a customer database, once per customer per process. Used by web requests and by the worker.</summary>
public sealed class TenantSchemas
{
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> done = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> locks = new();

    public async Task EnsureAsync(IServiceProvider services, TenantInfo t, CancellationToken ct)
    {
        if (t.ConnectionString == "") return;
        foreach (var sc in services.GetServices<ITenantSchema>())
        {
            var key = t.Id + "|" + sc.Name;
            if (done.ContainsKey(key)) continue;
            var gate = locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                if (done.ContainsKey(key)) continue;
                await sc.EnsureAsync(services, t, ct);
                done[key] = true;
            }
            finally { gate.Release(); }
        }
    }
}

/// <summary>After tenancy: the customer's schemas are ready before the request is handled.</summary>
public sealed class TenantSchemaMiddleware
{
    readonly RequestDelegate next;
    public TenantSchemaMiddleware(RequestDelegate next) => this.next = next;
    public async Task InvokeAsync(HttpContext http, TenantSchemas schemas, ITenantContext ctx)
    {
        if (ctx.Current != null) await schemas.EnsureAsync(http.RequestServices, ctx.Current, http.RequestAborted);
        await next(http);
    }
}

public static class TenancyExtensions
{
    public static IServiceCollection AddTenancy(this IServiceCollection s, ITenantCatalog catalog)
    {
        s.AddSingleton(catalog);
        if (catalog is ITenantDomains domains) s.AddSingleton(domains);
        s.AddSingleton<TenantSchemas>();
        s.AddScoped<TenantContext>();
        s.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        return s;
    }
    public static IApplicationBuilder UseTenantSchemas(this IApplicationBuilder app) => app.UseMiddleware<TenantSchemaMiddleware>();
    public static IApplicationBuilder UseTenancy(this IApplicationBuilder app) => app.UseMiddleware<TenantResolutionMiddleware>();
}

/// <summary>The platform catalog database: the list of customers (and their licences). Made by "tenant init" / "tenant create".</summary>
public static class CatalogSchema
{
    public static readonly Migration[] Migrations =
    {
        new(1, "customers and licences", @"
            create schema if not exists platform;
            create table if not exists platform.tenants (
              id text primary key, name text not null, host text not null unique,
              connection_string text not null, active boolean not null default true, created_at timestamptz not null default now());
            create table if not exists platform.licenses (
              tenant_id text primary key references platform.tenants(id), token text not null, updated_at timestamptz not null default now());"),
        new(2, "customers' own domains", @"
            create table if not exists platform.tenant_domains (
              host text primary key, tenant_id text not null references platform.tenants(id) on delete cascade,
              token text not null, verified_at timestamptz, is_primary boolean not null default false, created_at timestamptz not null default now());
            create index if not exists tenant_domains_tenant on platform.tenant_domains (tenant_id);
            create unique index if not exists tenant_domains_primary on platform.tenant_domains (tenant_id) where is_primary"),
    };
}
