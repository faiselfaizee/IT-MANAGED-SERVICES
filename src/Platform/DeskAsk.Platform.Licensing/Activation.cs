using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeskAsk.Platform.Licensing;

/// <summary>What this installation keeps about its online activation (setting license.activation). The secret is sealed with data protection.</summary>
public sealed record ActivationState(string Url, string Id, string Secret, string KeyHint, DateTime ActivatedAt, DateTime? LastCheck, string LastResult);

/// <summary>
/// Online activation of an on-prem installation (docs/licensing.md). An administrator types the licence key once; the installation sends it with
/// its installation id to the vendor (Licensing:ActivationUrl, default https://platform.deskask.in) and gets back a licence bound to this
/// installation, plus an activation id and secret. Once a day the installation asks for a newer licence with that id and secret, so renewals,
/// upgrades and cancellations arrive by themselves. Nothing else is sent: no usage and no customer data.
/// </summary>
public static class Activation
{
    public const string SettingKey = "license.activation";
    const string Purpose = "licensing.activation";

    /// <summary>Tests replace the network with the vendor test server.</summary>
    public static HttpMessageHandler? TestHandler { get; set; }
    static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(30) };
    static HttpClient Client() => TestHandler != null ? new HttpClient(TestHandler, false) { Timeout = TimeSpan.FromSeconds(30) } : Shared;

    static string VendorUrl(IConfiguration cfg) => (cfg["Licensing:ActivationUrl"] is { Length: > 0 } u ? u : "https://platform.deskask.in").TrimEnd('/');

    public static async Task<ActivationState?> LoadAsync(ISettings s, IDataProtectionProvider dp, CancellationToken ct)
    {
        var v = await s.GetAsync(SettingKey, ct);
        if (v == null) return null;
        try
        {
            var a = JsonSerializer.Deserialize<ActivationState>(v, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (a == null || a.Id == "") return null;
            return a with { Secret = a.Secret == "" ? "" : dp.CreateProtector(Purpose).Unprotect(a.Secret) };
        }
        catch (Exception e) when (e is JsonException or System.Security.Cryptography.CryptographicException) { return null; }
    }

    static Task SaveAsync(ISettings s, IDataProtectionProvider dp, ActivationState a, CancellationToken ct) =>
        s.SetAsync(SettingKey, JsonSerializer.Serialize(a with { Secret = dp.CreateProtector(Purpose).Protect(a.Secret) }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), ct);

    static object View(ActivationState? a) => a == null ? new { activated = false } : new
    {
        activated = true, keyHint = a.KeyHint, activatedAt = a.ActivatedAt, lastCheck = a.LastCheck, lastResult = a.LastResult, url = a.Url,
    };

    static async Task<(int Status, JsonElement Body)> PostAsync(string url, object body, CancellationToken ct)
    {
        try
        {
            using var r = await Client().PostAsJsonAsync(url, body, ct);
            var text = await r.Content.ReadAsStringAsync(ct);
            JsonElement j;
            try { j = JsonDocument.Parse(text == "" ? "{}" : text).RootElement.Clone(); } catch (JsonException) { j = JsonDocument.Parse("{}").RootElement.Clone(); }
            return ((int)r.StatusCode, j);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return (0, JsonSerializer.SerializeToElement(new { error = "unreachable", detail = "The licence server could not be reached: " + e.Message }));
        }
    }

    static string Detail(JsonElement j, string fallback) =>
        j.ValueKind == JsonValueKind.Object && j.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : fallback;

    /// <summary>Asks for the newest licence of this activation and installs it when it is newer. Used by "Check now" and the daily job.</summary>
    public static async Task<(bool Ok, string Result)> RefreshAsync(IServiceProvider sp, CancellationToken ct)
    {
        var settings = sp.GetRequiredService<ISettings>(); var dp = sp.GetRequiredService<IDataProtectionProvider>();
        var a = await LoadAsync(settings, dp, ct);
        if (a == null) return (false, "This installation is not activated with a licence key.");
        var (status, body) = await PostAsync(a.Url + "/api/store/licence", new { activation = a.Id, secret = a.Secret }, ct);
        string result; var ok = false;
        var installedNow = await settings.GetAsync(LicenseResolver.SettingKey, ct);
        var token = status == 200 && body.ValueKind == JsonValueKind.Object && body.TryGetProperty("token", out var tk) && tk.ValueKind == JsonValueKind.String ? tk.GetString() ?? "" : "";
        if (token != "" && installedNow == JsonSerializer.Serialize(token.Trim())) { ok = true; result = "The licence is up to date."; }
        else if (token != "")
        {
            var refused = await sp.GetRequiredService<LicenseResolver>().InstallAsync(token, "activation", ct);
            ok = refused == null || refused.Value.Status == 409;   // 409: the licence installed here is already the newest
            result = refused == null ? "A newer licence was installed." : refused.Value.Status == 409 ? "The licence is up to date." : "The licence from the server was refused.";
        }
        else if (status is 404 or 410) result = Detail(body, "The licence server no longer knows this activation. Activate again with a licence key.");
        else result = Detail(body, "The licence server answered " + status + ".");
        await SaveAsync(settings, dp, a with { LastCheck = DateTime.UtcNow, LastResult = result }, ct);
        return (ok, result);
    }

    public static void Map(IEndpointRouteBuilder r)
    {
        r.MapGet("/api/platform/license/activation", async (ISettings s, IDataProtectionProvider dp, CancellationToken ct) =>
            Results.Ok(new { installation = await Installation.GetAsync(s, create: true, ct), activation = View(await LoadAsync(s, dp, ct)) }))
            .RequireAuthorization(p => p.RequireRole("admin"));

        // the administrator types the licence key: exchange it for a licence bound to this installation
        r.MapPost("/api/platform/license/activate", async (JsonElement b, HttpContext http, ISettings s, IDataProtectionProvider dp, IConfiguration cfg, ITenantContext t,
            LicenseResolver res, IAuditLog audit, ILoggerFactory logs, CancellationToken ct) =>
        {
            try
            {
            var key = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString()!.Trim().ToUpperInvariant() : "";
            if (key.Length is < 10 or > 64) return Results.BadRequest(new { error = "key_missing", detail = "Type the licence key, for example DKA-XXXXX-XXXXX-XXXXX-XXXXX-XXXXX." });
            var installation = (await Installation.GetAsync(s, create: true, ct))!;
            var url = VendorUrl(cfg);
            var (status, body) = await PostAsync(url + "/api/store/activate", new
            {
                key, installation, tenant = t.Require().Id, host = http.Request.Host.Host, version = typeof(Activation).Assembly.GetName().Version?.ToString() ?? "",
            }, ct);
            if (status != 200) return Results.Json(new { error = "activation_refused", status, detail = Detail(body, "The licence server refused the key.") }, statusCode: status == 0 ? 502 : 400);
            var token = body.TryGetProperty("token", out var tk) ? tk.GetString() ?? "" : "";
            var id = body.TryGetProperty("activation", out var ai) ? ai.GetString() ?? "" : "";
            var secret = body.TryGetProperty("secret", out var se) ? se.GetString() ?? "" : "";
            if (await res.InstallAsync(token, "activation", ct) is { } refused && refused.Status != 409) return Results.Json(refused.Body, statusCode: refused.Status);
            await SaveAsync(s, dp, new ActivationState(url, id, secret, key[^4..], DateTime.UtcNow, DateTime.UtcNow, "Activated."), ct);
            await audit.WriteAsync("license.activated", "key ending " + key[^4..], "installation " + installation, ct: ct);
            return Results.Ok(LicensingModule.Summary(await res.CurrentAsync(ct), t, res.Vendor));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // say what failed instead of a bare 500, and keep the full error in the server log
                logs.CreateLogger("DeskAsk.Licensing").LogError(e, "Activating a licence key failed for customer {Tenant}", t.Current?.Id);
                return Results.Json(new { error = "activation_failed", detail = "The key could not be activated: " + e.GetType().Name + ": " + e.Message + " (see the DeskAsk log for the full error)." }, statusCode: 500);
            }
        }).RequireAuthorization(p => p.RequireRole("admin"));

        r.MapPost("/api/platform/license/refresh", async (HttpContext http, IAuditLog audit, CancellationToken ct) =>
        {
            var (ok, result) = await RefreshAsync(http.RequestServices, ct);
            await audit.WriteAsync("license.checked", "", result, ct: ct);
            return ok ? Results.Ok(new { result }) : Results.BadRequest(new { error = "refresh_failed", detail = result });
        }).RequireAuthorization(p => p.RequireRole("admin"));

        // moving to another server: free the activation so the key can be used there; the licence stays until it ends
        r.MapPost("/api/platform/license/release", async (ISettings s, IDataProtectionProvider dp, IAuditLog audit, CancellationToken ct) =>
        {
            var a = await LoadAsync(s, dp, ct);
            if (a == null) return Results.BadRequest(new { error = "not_activated" });
            var (status, body) = await PostAsync(a.Url + "/api/store/release", new { activation = a.Id, secret = a.Secret }, ct);
            if (status is not (200 or 404 or 410)) return Results.Json(new { error = "release_failed", detail = Detail(body, "The licence server could not release this installation.") }, statusCode: 502);
            await s.DeleteAsync(SettingKey, ct);
            await audit.WriteAsync("license.released", "key ending " + a.KeyHint, "", ct: ct);
            return Results.Ok(new { released = true });
        }).RequireAuthorization(p => p.RequireRole("admin"));
    }
}

/// <summary>Once a day: an activated installation asks the vendor for a newer licence (renewal, upgrade, cancellation).</summary>
public sealed class LicenseRefreshJob : IJobHandler
{
    public const string JobType = "licensing.refresh";
    public string Type => JobType;
    public async Task RunAsync(JobContext job, CancellationToken ct)
    {
        if (await Activation.LoadAsync(job.Services.GetRequiredService<ISettings>(), job.Services.GetRequiredService<IDataProtectionProvider>(), ct) == null) return;
        var (ok, result) = await Activation.RefreshAsync(job.Services, ct);
        if (!ok) job.Services.GetService<ILoggerFactory>()?.CreateLogger("DeskAsk.Licensing").LogWarning("Licence check for {Tenant}: {Result}", job.Tenant.Id, result);
    }
}
