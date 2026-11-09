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

public sealed class InMemoryTenantCatalog : ITenantCatalog
{
    readonly Dictionary<string, TenantInfo> byHost;
    public InMemoryTenantCatalog(IEnumerable<TenantInfo> tenants) =>
        byHost = tenants.ToDictionary(t => HostName.Normalize(t.Host), t => t);
    public Task<TenantInfo?> FindByHostAsync(string host, CancellationToken ct = default) =>
        Task.FromResult(byHost.TryGetValue(HostName.Normalize(host), out var t) && t.Active ? t : null);
    public Task<IReadOnlyList<TenantInfo>> ListActiveAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TenantInfo>>(byHost.Values.Where(t => t.Active).ToList());
}

/// <summary>Reads the platform catalog database: table platform.tenants(id, name, host, connection_string, active).</summary>
public sealed class PgTenantCatalog : ITenantCatalog
{
    readonly string conn;
    public PgTenantCatalog(string connectionString) => conn = connectionString;
    public async Task<TenantInfo?> FindByHostAsync(string host, CancellationToken ct = default)
    {
        await using var db = new NpgsqlConnection(conn);
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select id, name, host, connection_string from platform.tenants where lower(host) = @h and active", db);
        cmd.Parameters.AddWithValue("h", HostName.Normalize(host));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new TenantInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)) : null;
    }

    public async Task<IReadOnlyList<TenantInfo>> ListActiveAsync(CancellationToken ct = default)
    {
        await using var db = new NpgsqlConnection(conn);
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("select id, name, host, connection_string from platform.tenants where active order by id", db);
        var list = new List<TenantInfo>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new TenantInfo(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        return list;
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
    };
}
