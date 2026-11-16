using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeskAsk.Platform;
using DeskAsk.Platform.Licensing;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

/// <summary>
/// Size limits in the licence: monitored devices, staff accounts, AD domains and devices with file backup are refused over the limit, and
/// the licence page gets what is used. The licence comes from a file of this test's own (other tests run unlicensed-allowed).
/// </summary>
[Collection("backup")]
public class LicenseLimitsTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly ECDsa Vendor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    static readonly string File = Path.Combine(Path.GetTempPath(), "dk-limits-" + Guid.NewGuid().ToString("N") + ".lic");
    const string Pw = "Correct-Horse-9";
    readonly WebApplicationFactory<Program> factory;
    public LicenseLimitsTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b =>
    {
        TestSupport.Configure(b);
        b.UseSetting("Licensing:VendorPublicKeyPem", Vendor.ExportSubjectPublicKeyInfoPem());
        b.UseSetting("Licensing:CacheSeconds", "0");
        b.UseSetting("Licensing:File", File);
        b.UseSetting("Platform:Roles", "all");
    });

    static void Licence(Dictionary<string, Dictionary<string, int>?> modules)
    {
        var now = DateTimeOffset.UtcNow;
        System.IO.File.WriteAllText(File, LicenseCodec.Issue(Vendor, new LicensePayload(1, "*", "limits", now.ToUnixTimeSeconds(), now.AddDays(-1).ToUnixTimeSeconds(), now.AddDays(30).ToUnixTimeSeconds(), 0,
            modules.ToDictionary(m => m.Key, m => new ModuleGrant(m.Value)))));
    }
    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    [PgFact]
    public async Task LimitsAreKeptAndShown()
    {
        var all = new Dictionary<string, Dictionary<string, int>?> { ["servicedesk"] = null, ["assets"] = null, ["monitoring"] = null, ["adsuite"] = null, ["vault"] = null, ["backup"] = null };
        Licence(all);
        try
        {
            var admin = await TestSupport.AdminClient(factory);
            var used = await J(await admin.GetAsync("/api/platform/license/usage"));
            long U(string k) => used.TryGetProperty(k, out var v) ? v.GetInt64() : -1;
            foreach (var k in new[] { "assets:max_assets", "monitoring:max_devices", "servicedesk:agents", "adsuite:max_domains", "vault:max_users", "backup:max_devices" }) Assert.True(U(k) >= 0, k);
            Assert.True(U("servicedesk:agents") >= 1);   // at least the administrator

            // two new monitored assets over a limit of (monitored now + 1): one goes unwatched, and switching on another is refused
            var a1 = (await J(await admin.PostAsync("/api/assets", Json(new { name = "LIM-1 " + Guid.NewGuid().ToString("N")[..5] })))).GetProperty("id").GetInt64();
            var mon0 = U("monitoring:max_devices") + 1;   // includes a1
            Licence(new(all) { ["monitoring"] = new() { ["max_devices"] = (int)mon0 } });
            var a2 = (await J(await admin.PostAsync("/api/assets", Json(new { name = "LIM-2 " + Guid.NewGuid().ToString("N")[..5] })))).GetProperty("id").GetInt64();
            var a3 = (await J(await admin.PostAsync("/api/assets", Json(new { name = "LIM-3 " + Guid.NewGuid().ToString("N")[..5], monitored = false })))).GetProperty("id").GetInt64();
            var dash = await J(await admin.GetAsync("/api/monitoring/dashboard"));
            Assert.True(dash.GetProperty("licence").GetProperty("over").GetInt64() >= 1);
            Assert.Equal(mon0, dash.GetProperty("licence").GetProperty("allowed").GetInt64());
            Assert.True(dash.GetProperty("all").GetProperty("devices").GetInt64() <= mon0);
            var refused = await admin.PostAsync("/api/assets/bulk-monitor", Json(new { ids = new[] { a3 }, monitored = true }));
            Assert.Equal(HttpStatusCode.PaymentRequired, refused.StatusCode);
            Assert.Equal("limit_reached", (await J(refused)).GetProperty("error").GetString());
            Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.PutAsync("/api/assets/" + a3, Json(new { monitored = true }))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/assets/bulk-monitor", Json(new { ids = new[] { a2 }, monitored = false }))).StatusCode);   // switching off is always allowed

            // staff accounts: at the limit an engineer is refused, a viewer is not
            var staff = (int)(await J(await admin.GetAsync("/api/platform/license/usage"))).GetProperty("servicedesk:agents").GetInt64();
            Licence(new(all) { ["servicedesk"] = new() { ["agents"] = staff } });
            var eng = await admin.PostAsync("/api/platform/users", Json(new { email = $"eng-{Guid.NewGuid():N}@lim.test", name = "Eng", role = "engineer", password = Pw }));
            Assert.Equal(HttpStatusCode.PaymentRequired, eng.StatusCode);
            Assert.Contains("staff accounts", (await J(eng)).GetProperty("detail").GetString());
            var view = await J(await admin.PostAsync("/api/platform/users", Json(new { email = $"view-{Guid.NewGuid():N}@lim.test", name = "Viewer", role = "viewer", password = Pw })));
            var promote = await admin.PatchAsync("/api/platform/users/" + view.GetProperty("id").GetString(), Json(new { role = "engineer" }));
            Assert.Equal(HttpStatusCode.PaymentRequired, promote.StatusCode);

            // AD domains
            Licence(new(all) { ["adsuite"] = new() { ["max_domains"] = 0 } });
            Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.PostAsync("/api/adsuite/domains", Json(new { name = "acme.local" }))).StatusCode);

            // devices with file backup: a new device over the limit is refused, a second plan for a counted device is not
            var bk = (int)(await J(await admin.GetAsync("/api/platform/license/usage"))).GetProperty("backup:max_devices").GetInt64();
            Licence(new(all) { ["backup"] = new() { ["max_devices"] = bk + 1 } });
            object Plan(long asset, string name) => new { name, assetId = asset, paths = new[] { "C:\\Data" }, storage = new { kind = "local", path = "E:\\Backups" } };
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/backup/plans", Json(Plan(a1, "First")))).StatusCode);
            var over = await admin.PostAsync("/api/backup/plans", Json(Plan(a2, "Other device")));
            Assert.Equal(HttpStatusCode.PaymentRequired, over.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/backup/plans", Json(Plan(a1, "Second")))).StatusCode);
            Assert.Equal(bk + 1, (await J(await admin.GetAsync("/api/platform/license/usage"))).GetProperty("backup:max_devices").GetInt64());
        }
        finally { try { System.IO.File.Delete(File); } catch { } }
    }
}
