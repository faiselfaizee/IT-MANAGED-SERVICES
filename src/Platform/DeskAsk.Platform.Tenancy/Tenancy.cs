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

public static class TenancyExtensions
{
    public static IServiceCollection AddTenancy(this IServiceCollection s, ITenantCatalog catalog)
    {
        s.AddSingleton(catalog);
        s.AddScoped<TenantContext>();
        s.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        return s;
    }
    public static IApplicationBuilder UseTenancy(this IApplicationBuilder app) => app.UseMiddleware<TenantResolutionMiddleware>();
}
