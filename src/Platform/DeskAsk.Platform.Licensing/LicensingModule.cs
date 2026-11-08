using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.Licensing;

/// <summary>The vendor's public key (Licensing:VendorPublicKeyPem). Without it no licence can be trusted.</summary>
public sealed class VendorKey
{
    public ECDsa? Public { get; }
    public VendorKey(IConfiguration cfg)
    {
        var pem = cfg["Licensing:VendorPublicKeyPem"];
        if (string.IsNullOrWhiteSpace(pem)) return;
        var k = ECDsa.Create(); k.ImportFromPem(pem); Public = k;
    }
}

/// <summary>
/// Finds the customer's licence. Sources: the catalog (SaaS, table platform.licenses), a file (Licensing:File) and an uploaded
/// token (setting license.token). Every token is verified; the valid one issued last wins, so an old licence can never replace a newer one.
/// Licensing:AllowUnlicensed=true gives every module (development only).
/// </summary>
public sealed class LicenseResolver : IEntitlements
{
    public const string SettingKey = "license.token";
    readonly IConfiguration cfg; readonly ITenantContext tenant; readonly ISettings settings; readonly IMemoryCache cache; readonly VendorKey vendor;
    public LicenseResolver(IConfiguration cfg, ITenantContext tenant, ISettings settings, IMemoryCache cache, VendorKey vendor)
    { this.cfg = cfg; this.tenant = tenant; this.settings = settings; this.cache = cache; this.vendor = vendor; }

    string CacheKey => "license|" + tenant.Require().Id;
    public void Invalidate() => cache.Remove(CacheKey);

    public async Task<License> CurrentAsync(CancellationToken ct = default)
    {
        var ttl = cfg.GetValue("Licensing:CacheSeconds", 60);
        if (ttl > 0 && cache.TryGetValue(CacheKey, out License? hit) && hit != null) return hit;
        var l = await ComputeAsync(ct);
        if (ttl > 0) cache.Set(CacheKey, l, TimeSpan.FromSeconds(ttl));
        return l;
    }

    public async Task<License> ComputeAsync(CancellationToken ct = default)
    {
        var t = tenant.Require();
        if (cfg.GetValue<bool>("Licensing:AllowUnlicensed"))
            return new License(LicenseState.Valid, new LicensePayload(1, t.Id, "development", 0, 0, long.MaxValue / 2, 0, Modules.All.ToDictionary(m => m, _ => new ModuleGrant())), "");
        var tokens = new List<string>();
        var up = await settings.GetAsync(SettingKey, ct);
        if (up != null) try { tokens.Add(JsonSerializer.Deserialize<string>(up) ?? ""); } catch (JsonException) { }
        var file = cfg["Licensing:File"];
        if (!string.IsNullOrWhiteSpace(file) && File.Exists(file)) tokens.Add(await File.ReadAllTextAsync(file, ct));
        var catalog = cfg["Platform:CatalogConnection"];
        if (!string.IsNullOrWhiteSpace(catalog))
        {
            try
            {
                await using var db = new NpgsqlConnection(catalog); await db.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand("select token from platform.licenses where tenant_id = @t", db);
                cmd.Parameters.AddWithValue("t", t.Id);
                if (await cmd.ExecuteScalarAsync(ct) is string s) tokens.Add(s);
            }
            catch (PostgresException) { }   // no licences table yet: the other sources still count
        }
        tokens = tokens.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (tokens.Count == 0) return new License(LicenseState.Missing, null, "No licence is installed.");
        if (vendor.Public == null) return new License(LicenseState.Invalid, null, "This installation has no vendor key, so no licence can be checked.");
        var all = tokens.Select(x => LicenseCodec.Verify(x, vendor.Public, t.Id, DateTimeOffset.UtcNow)).ToList();
        return all.Where(x => x.InForce).OrderByDescending(x => x.Payload!.Issued).FirstOrDefault()
            ?? all.OrderByDescending(x => x.Payload?.Issued ?? 0).First();
    }
}

public sealed class LicensingModule : IPlatformModule
{
    public string Key => "licensing";
    public string Schema => "core";   // licences are kept in core.settings (license.token) or the catalog

    public void AddServices(IServiceCollection s, IConfiguration cfg)
    {
        s.AddMemoryCache();
        s.AddSingleton<VendorKey>();
        s.AddScoped<LicenseResolver>();
        s.AddScoped<IEntitlements>(sp => sp.GetRequiredService<LicenseResolver>());
    }

    static object Summary(License l, ITenantContext t) => new
    {
        state = l.State.ToString().ToLowerInvariant(), reason = l.Reason, tenant = t.Require().Id,
        plan = l.Payload?.Plan,
        expires = l.Payload == null ? (DateTime?)null : DateTimeOffset.FromUnixTimeSeconds(Math.Min(l.Payload.Expires, 253370764800L)).UtcDateTime,
        graceUntil = l.Payload == null ? (DateTime?)null : DateTimeOffset.FromUnixTimeSeconds(Math.Min(l.Payload.Expires + l.Payload.GraceDays * 86400L, 253370764800L)).UtcDateTime,
        modules = l.InForce ? l.Payload!.Modules.Select(m => new { key = m.Key, limits = m.Value.Limits ?? new Dictionary<string, int>() }).ToArray() : Array.Empty<object>().Select(_ => new { key = "", limits = new Dictionary<string, int>() }).ToArray(),
    };

    public void MapEndpoints(IEndpointRouteBuilder r)
    {
        r.MapGet("/api/platform/license", async (IEntitlements e, ITenantContext t, CancellationToken ct) => Results.Ok(Summary(await e.CurrentAsync(ct), t))).RequireAuthorization();

        r.MapGet("/api/platform/modules", async (IEntitlements e, CancellationToken ct) =>
        {
            var l = await e.CurrentAsync(ct);
            return Results.Ok(Modules.All.Select(m => new { key = m, licensed = l.Has(m) }));
        }).RequireAuthorization();

        r.MapGet("/api/platform/license/check/{module}", async (string module, IEntitlements e, CancellationToken ct) =>
            (await e.CurrentAsync(ct)).Has(module) ? Results.Ok(new { licensed = true }) : Results.Json(new { error = "module_not_licensed", module }, statusCode: 402)).RequireAuthorization();

        r.MapPut("/api/platform/license", async (HttpRequest req, LicenseResolver res, VendorKey vendor, ITenantContext t, ISettings settings, IAuditLog audit, CancellationToken ct) =>
        {
            JsonElement body;
            try { body = await req.ReadFromJsonAsync<JsonElement>(ct); } catch (JsonException) { return Results.BadRequest(new { error = "bad_json" }); }
            var token = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("token", out var tk) ? tk.GetString() : null;
            if (string.IsNullOrWhiteSpace(token) || token.Length > 20000) return Results.BadRequest(new { error = "token_missing" });
            if (vendor.Public == null) return Results.BadRequest(new { error = "no_vendor_key", reason = "This installation has no vendor key configured." });
            var l = LicenseCodec.Verify(token, vendor.Public, t.Require().Id, DateTimeOffset.UtcNow);
            if (!l.InForce) return Results.BadRequest(new { error = "license_rejected", state = l.State.ToString().ToLowerInvariant(), reason = l.Reason });
            var current = await res.ComputeAsync(ct);
            if (current.InForce && current.Payload!.Issued > l.Payload!.Issued)
                return Results.Json(new { error = "older_license", reason = "A newer licence is already installed." }, statusCode: 409);
            await settings.SetAsync(LicenseResolver.SettingKey, JsonSerializer.Serialize(token.Trim()), ct);
            res.Invalidate();
            await audit.WriteAsync("license.installed", l.Payload!.Plan, "expires " + DateTimeOffset.FromUnixTimeSeconds(l.Payload.Expires).UtcDateTime.ToString("yyyy-MM-dd") + "; modules " + string.Join(",", l.Payload.Modules.Keys), ct: ct);
            return Results.Ok(Summary(await res.CurrentAsync(ct), t));
        }).RequireAuthorization(p => p.RequireRole("admin"));
    }
}
