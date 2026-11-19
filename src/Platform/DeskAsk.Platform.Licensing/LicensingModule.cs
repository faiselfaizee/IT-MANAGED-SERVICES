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

/// <summary>
/// The vendor's public key: every licence is checked against it. Built into the release (VendorPublicKey.pem, embedded); Licensing:VendorPublicKeyPem
/// (or Licensing:VendorPublicKeyFile) replaces it. A key in the configuration that cannot be read never stops the server: it is ignored,
/// the built-in key is used, and <see cref="Problem"/> says what is wrong (shown on Administration › Licence and in the vendor console).
/// </summary>
public sealed class VendorKey
{
    public ECDsa? Public { get; }
    public string? Problem { get; }
    public string Source { get; } = "none";

    public VendorKey(IConfiguration cfg)
    {
        var text = cfg["Licensing:VendorPublicKeyPem"];
        var file = cfg["Licensing:VendorPublicKeyFile"];
        if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(file))
        {
            try { text = File.ReadAllText(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Problem = "Licensing:VendorPublicKeyFile could not be read (" + e.Message + "). The built-in key is used."; }
        }
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (Read(text) is { } k) { Public = k; Source = "configuration"; return; }
            Problem = "Licensing:VendorPublicKeyPem in the configuration is not a valid public key, so it is ignored and the built-in key is used. Remove the line, or paste the whole key with \\n between its lines.";
        }
        if (Read(BuiltIn()) is { } b) { Public = b; Source = "built-in"; }
    }

    /// <summary>Reads a PEM public key; also accepts it with literal \n, with real line breaks, or only the base64 body. Null when it is not a P-256 public key.</summary>
    public static ECDsa? Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Replace("\\r", "").Replace("\\n", "\n").Trim();
        if (!t.Contains("-----BEGIN")) t = "-----BEGIN PUBLIC KEY-----\n" + t + "\n-----END PUBLIC KEY-----";
        var k = ECDsa.Create();
        try { k.ImportFromPem(t); if (k.KeySize == 256 && !HasPrivate(k)) return k; }
        catch (Exception e) when (e is ArgumentException or CryptographicException) { }
        k.Dispose();
        return null;
    }

    static bool HasPrivate(ECDsa k) { try { k.ExportParameters(true); return true; } catch (CryptographicException) { return false; } }

    static string BuiltIn()
    {
        using var st = typeof(VendorKey).Assembly.GetManifestResourceStream("DeskAsk.Platform.Licensing.VendorPublicKey.pem");
        if (st == null) return "";
        using var r = new StreamReader(st);
        return r.ReadToEnd();
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

    public VendorKey Vendor => vendor;
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
        var catalog = cfg["Platform:CatalogConnection"] ?? cfg["Licensing:CatalogConnection"];
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
        var installation = await Installation.GetAsync(settings, create: false, ct);
        var all = tokens.Select(x => Bound(LicenseCodec.Verify(x, vendor.Public, t.Id, DateTimeOffset.UtcNow), installation)).ToList();
        return all.Where(x => x.InForce).OrderByDescending(x => x.Payload!.Issued).FirstOrDefault()
            ?? all.OrderByDescending(x => x.Payload?.Issued ?? 0).First();
    }

    /// <summary>A licence made by online activation names the installation it was issued to; a copy on another installation is refused.</summary>
    public static License Bound(License l, string? installation) =>
        l.Payload?.Installation is { Length: > 0 } want && want != installation
            ? new License(LicenseState.Invalid, l.Payload, "This licence was activated on another installation. Activate this one with its own licence key.")
            : l;

    /// <summary>Checks and stores a licence token (pasted by an administrator or fetched by activation). Returns null when installed, else the refusal.</summary>
    public async Task<(int Status, object Body)?> InstallAsync(string token, string source, CancellationToken ct)
    {
        if (vendor.Public == null) return (400, new { error = "no_vendor_key", reason = "This installation has no vendor key configured." });
        var installation = await Installation.GetAsync(settings, create: false, ct);
        var l = Bound(LicenseCodec.Verify(token, vendor.Public, tenant.Require().Id, DateTimeOffset.UtcNow), installation);
        if (!l.InForce) return (400, new { error = "license_rejected", state = l.State.ToString().ToLowerInvariant(), reason = l.Reason });
        var current = await ComputeAsync(ct);
        if (current.InForce && current.Payload!.Issued > l.Payload!.Issued)
            return (409, new { error = "older_license", reason = "A newer licence is already installed." });
        await settings.SetAsync(SettingKey, JsonSerializer.Serialize(token.Trim()), ct);
        Invalidate();
        return null;
    }
}

/// <summary>A random id for this installation (per customer), made the first time it is needed. Online activation binds a licence to it.</summary>
public static class Installation
{
    public const string SettingKey = "license.installation";
    public static async Task<string?> GetAsync(ISettings s, bool create, CancellationToken ct)
    {
        var v = await s.GetAsync(SettingKey, ct);
        if (v != null) try { if (JsonSerializer.Deserialize<string>(v) is { Length: > 0 } id) return id; } catch (JsonException) { }
        if (!create) return null;
        var fresh = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        await s.SetAsync(SettingKey, JsonSerializer.Serialize(fresh), ct);
        return fresh;
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
        s.AddSingleton<IJobHandler, LicenseRefreshJob>();
        s.AddSingleton(new RecurringJob("licensing.refresh", LicenseRefreshJob.JobType, TimeSpan.FromHours(24)));
    }

    internal static object Summary(License l, ITenantContext t, VendorKey vendor) => new
    {
        state = l.State.ToString().ToLowerInvariant(), reason = l.Reason, tenant = t.Require().Id,
        plan = l.Payload?.Plan,
        expires = l.Payload == null ? (DateTime?)null : DateTimeOffset.FromUnixTimeSeconds(Math.Min(l.Payload.Expires, 253370764800L)).UtcDateTime,
        graceUntil = l.Payload == null ? (DateTime?)null : DateTimeOffset.FromUnixTimeSeconds(Math.Min(l.Payload.Expires + l.Payload.GraceDays * 86400L, 253370764800L)).UtcDateTime,
        modules = l.InForce ? l.Payload!.Modules.Select(m => new { key = m.Key, limits = m.Value.Limits ?? new Dictionary<string, int>() }).ToArray() : Array.Empty<object>().Select(_ => new { key = "", limits = new Dictionary<string, int>() }).ToArray(),
        readOnly = l.State == LicenseState.Expired && l.Payload != null ? l.Payload.Modules.Keys.ToArray() : Array.Empty<string>(),
        boundToInstallation = !string.IsNullOrEmpty(l.Payload?.Installation),
        vendorKey = new { source = vendor.Source, problem = vendor.Problem },
    };

    public void MapEndpoints(IEndpointRouteBuilder r)
    {
        r.MapGet("/api/platform/license", async (IEntitlements e, ITenantContext t, VendorKey vendor, CancellationToken ct) => Results.Ok(Summary(await e.CurrentAsync(ct), t, vendor))).RequireAuthorization();

        Activation.Map(r);

        // how much of each licence limit is used ("module:limit" -> count), from every module that counts
        r.MapGet("/api/platform/license/usage", async (HttpContext http, CancellationToken ct) =>
        {
            var o = new Dictionary<string, long>();
            foreach (var u in http.RequestServices.GetServices<ILicenseUsage>())
                foreach (var kv in await u.UsageAsync(ct)) o[kv.Key] = kv.Value;
            return Results.Ok(o);
        }).RequireAuthorization(p => p.RequireRole("admin"));

        r.MapGet("/api/platform/modules", async (IEntitlements e, CancellationToken ct) =>
        {
            var l = await e.CurrentAsync(ct);
            return Results.Ok(Modules.All.Select(m => new { key = m, licensed = l.Has(m), readOnly = l.ReadOnly(m) }));
        }).RequireAuthorization();

        r.MapGet("/api/platform/license/check/{module}", async (string module, IEntitlements e, CancellationToken ct) =>
            (await e.CurrentAsync(ct)).Has(module) ? Results.Ok(new { licensed = true }) : Results.Json(new { error = "module_not_licensed", module }, statusCode: 402)).RequireAuthorization();

        r.MapPut("/api/platform/license", async (HttpRequest req, LicenseResolver res, ITenantContext t, VendorKey vendor, IAuditLog audit, CancellationToken ct) =>
        {
            JsonElement body;
            try { body = await req.ReadFromJsonAsync<JsonElement>(ct); } catch (JsonException) { return Results.BadRequest(new { error = "bad_json" }); }
            var token = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("token", out var tk) && tk.ValueKind == JsonValueKind.String ? tk.GetString() : null;
            if (string.IsNullOrWhiteSpace(token) || token.Length > 20000) return Results.BadRequest(new { error = "token_missing" });
            if (await res.InstallAsync(token, "upload", ct) is { } refused) return Results.Json(refused.Body, statusCode: refused.Status);
            var l = await res.CurrentAsync(ct);
            await audit.WriteAsync("license.installed", l.Payload?.Plan ?? "", "expires " + (l.Payload == null ? "" : DateTimeOffset.FromUnixTimeSeconds(l.Payload.Expires).UtcDateTime.ToString("yyyy-MM-dd")) + "; modules " + string.Join(",", l.Payload?.Modules.Keys ?? Enumerable.Empty<string>()), ct: ct);
            return Results.Ok(Summary(l, t, vendor));
        }).RequireAuthorization(p => p.RequireRole("admin"));
    }
}
