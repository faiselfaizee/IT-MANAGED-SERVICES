using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeskAsk.Platform;
using DeskAsk.Platform.Licensing;
using DeskAsk.Platform.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

/// <summary>
/// Release 6.0, the vendor console and online activation (docs/store.md). The test host is both the vendor platform (its customer "dev" is the
/// vendor's own) and a customer installation that activates against it, through the test server's own handler.
/// </summary>
public class CommerceTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly ECDsa Vendor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public CommerceTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b =>
    {
        TestSupport.Configure(b);
        b.UseSetting("Licensing:VendorPublicKeyPem", Vendor.ExportSubjectPublicKeyInfoPem());
        b.UseSetting("Licensing:CacheSeconds", "0");
        b.UseSetting("Licensing:CatalogConnection", Pg);
        b.UseSetting("Licensing:ActivationUrl", "http://localhost");
        b.UseSetting("Commerce:Enabled", "true");
        b.UseSetting("Commerce:VendorTenant", "dev");
        b.UseSetting("Commerce:Connection", Pg);
    });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static string Day(int days) => DateTime.UtcNow.Date.AddDays(days).ToString("yyyy-MM-dd");

    static async Task Sql(string sql, params (string, object)[] p)
    {
        await using var db = new NpgsqlConnection(Pg); await db.OpenAsync();
        await using var c = new NpgsqlCommand(sql, db);
        foreach (var (n, v) in p) c.Parameters.AddWithValue(n, v);
        await c.ExecuteNonQueryAsync();
    }

    static async Task PrepareCatalogAsync()
    {
        await SchemaMigrator.RunAsync(Pg, "catalog", CatalogSchema.Migrations, CancellationToken.None);
        await Sql("insert into platform.tenants (id, name, host, connection_string) values ('dev', 'Development', 'localhost', '') on conflict (id) do nothing");
        await Sql("insert into platform.tenants (id, name, host, connection_string) values ('oldco', 'Old Co', 'oldco.deskask.test', '') on conflict (id) do nothing");
        var now = DateTimeOffset.UtcNow;
        var manual = LicenseCodec.Issue(Vendor, new LicensePayload(1, "oldco", "business", now.AddDays(-100).ToUnixTimeSeconds(), now.AddDays(-100).ToUnixTimeSeconds(), now.AddDays(200).ToUnixTimeSeconds(), 14,
            new() { ["servicedesk"] = new(new() { ["agents"] = 5 }), ["assets"] = new() }));
        await Sql("insert into platform.licenses (tenant_id, token) values ('oldco', @t) on conflict (tenant_id) do update set token = excluded.token", ("t", manual));
        await Sql("delete from platform.licenses where tenant_id = 'dev'");
    }

    static async Task CleanAsync(HttpClient admin)
    {
        foreach (var k in new[] { LicenseResolver.SettingKey, Activation.SettingKey, Installation.SettingKey }) await admin.DeleteAsync("/api/platform/settings/" + k);
        await Sql("delete from platform.licenses where tenant_id in ('dev', 'oldco')");
    }

    [PgFact]
    public async Task VendorConsole_SaasAndOnPremLicensing_EndToEnd()
    {
        Activation.TestHandler = factory.Server.CreateHandler();
        var admin = await TestSupport.AdminClient(factory);
        await SchemaMigrator.RunAsync(Pg, "catalog", CatalogSchema.Migrations, CancellationToken.None);
        await CleanAsync(admin);
        await PrepareCatalogAsync();
        try
        {
            // 1. the console is on, but cannot sign until the key is imported; a key that does not match the trusted public key is refused
            var st = await J(await admin.GetAsync("/api/commerce/status"));
            Assert.True(st.GetProperty("enabled").GetBoolean());
            Assert.False(st.GetProperty("signing").GetProperty("ready").GetBoolean());
            using (var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/commerce/signing-key", Json(new { pem = stranger.ExportPkcs8PrivateKeyPem() }))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/commerce/signing-key", Json(new { pem = Vendor.ExportSubjectPublicKeyInfoPem() }))).StatusCode);
            var noKey = await admin.PostAsync("/api/commerce/subscriptions", Json(new { kind = "saas", tenantId = "dev", lines = new { servicedesk = new { agents = 3 } }, endsAt = Day(365), account = new { name = "Acme", email = "a@acme.test" } }));
            Assert.True(noKey.StatusCode == HttpStatusCode.Conflict, await noKey.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/commerce/signing-key", Json(new { pem = Vendor.ExportPkcs8PrivateKeyPem() }))).StatusCode);
            st = await J(await admin.GetAsync("/api/commerce/status"));
            Assert.True(st.GetProperty("signing").GetProperty("ready").GetBoolean());
            Assert.Equal("stored", st.GetProperty("signing").GetProperty("source").GetString());

            // 2. the price list is there
            var plans = await J(await admin.GetAsync("/api/commerce/plans"));
            Assert.True(plans.GetArrayLength() >= 20);
            Assert.Contains(plans.EnumerateArray(), p => p.GetProperty("module").GetString() == "adsuite" && p.GetProperty("sizeLabel").GetString() == "1 domain");

            // 3. a customer licensed before the store: listed as not linked, then linked without a new licence
            var unlinked = await J(await admin.GetAsync("/api/commerce/unlinked"));
            var old = unlinked.EnumerateArray().Single(x => x.GetProperty("tenantId").GetString() == "oldco");
            Assert.Equal("valid", old.GetProperty("licence").GetProperty("state").GetString());
            Assert.DoesNotContain(unlinked.EnumerateArray(), x => x.GetProperty("tenantId").GetString() == "dev");   // the vendor's own customer
            var link = await admin.PostAsync("/api/commerce/link", Json(new { tenantId = "oldco", accountName = "Old Co Pvt Ltd", email = "it@oldco.test" }));
            Assert.True(link.IsSuccessStatusCode, await link.Content.ReadAsStringAsync());
            var oldSub = await J(await admin.GetAsync("/api/commerce/subscriptions/" + (await J(link)).GetProperty("id").GetString()));
            Assert.Equal(5, oldSub.GetProperty("sub").GetProperty("lines").GetProperty("servicedesk").GetProperty("agents").GetInt32());
            Assert.Equal("linked existing licence", oldSub.GetProperty("licences")[0].GetProperty("reason").GetString());
            Assert.DoesNotContain((await J(await admin.GetAsync("/api/commerce/unlinked"))).EnumerateArray(), x => x.GetProperty("tenantId").GetString() == "oldco");

            // 4. a SaaS subscription licenses the customer at once; a change reaches them at once
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/commerce/subscriptions", Json(new { kind = "saas", tenantId = "dev", lines = new { monitoring = new { max_devices = 50 } }, endsAt = Day(365), account = new { name = "Acme", email = "a@acme.test" } }))).StatusCode);   // monitoring needs assets
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/commerce/subscriptions", Json(new { kind = "saas", tenantId = "nobody", lines = new { servicedesk = new { } }, endsAt = Day(365), account = new { name = "Acme", email = "a@acme.test" } }))).StatusCode);   // no such customer
            var saas = await admin.PostAsync("/api/commerce/subscriptions", Json(new { kind = "saas", tenantId = "dev", plan = "business", lines = new { servicedesk = new { agents = 10 } }, endsAt = Day(365), account = new { name = "Acme Ltd", email = "a@acme.test", gstin = "29ABCDE1234F1Z5" } }));
            Assert.True(saas.IsSuccessStatusCode, await saas.Content.ReadAsStringAsync());
            var saasId = (await J(saas)).GetProperty("id").GetString();
            var lic = await J(await admin.GetAsync("/api/platform/license"));
            Assert.Equal("valid", lic.GetProperty("state").GetString());
            Assert.Equal("business", lic.GetProperty("plan").GetString());
            Assert.Contains(lic.GetProperty("modules").EnumerateArray(), m => m.GetProperty("key").GetString() == "servicedesk" && m.GetProperty("limits").GetProperty("agents").GetInt32() == 10);
            var change = await admin.PutAsync("/api/commerce/subscriptions/" + saasId, Json(new { lines = new { servicedesk = new { agents = 25 }, vault = new { max_users = 10 } } }));
            Assert.True(change.IsSuccessStatusCode, await change.Content.ReadAsStringAsync());
            lic = await J(await admin.GetAsync("/api/platform/license"));
            Assert.Contains(lic.GetProperty("modules").EnumerateArray(), m => m.GetProperty("key").GetString() == "vault");
            Assert.Contains(lic.GetProperty("modules").EnumerateArray(), m => m.GetProperty("key").GetString() == "servicedesk" && m.GetProperty("limits").GetProperty("agents").GetInt32() == 25);
            var list = await J(await admin.GetAsync("/api/commerce/subscriptions?q=acme"));
            Assert.Contains(list.EnumerateArray(), x => x.GetProperty("sub").GetProperty("id").GetString() == saasId);

            // 5. on-prem: a key, activated by the installation; the licence is bound to it
            var onprem = await J(await admin.PostAsync("/api/commerce/subscriptions", Json(new { kind = "onprem", plan = "onprem-test", lines = new { assets = new { max_assets = 500 }, monitoring = new { max_devices = 150 } }, endsAt = Day(30), account = new { name = "Beta Industries", email = "it@beta.test" } })));
            var onpremId = onprem.GetProperty("id").GetString();
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/commerce/subscriptions/" + saasId + "/keys", Json(new { }))).StatusCode);   // SaaS has no keys
            var k = await J(await admin.PostAsync("/api/commerce/subscriptions/" + onpremId + "/keys", Json(new { maxActivations = 1 })));
            var key = k.GetProperty("key").GetString()!;
            Assert.Matches("^DKA-([A-Z2-9]{5}-){4}[A-Z2-9]{5}$", key);

            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/platform/license/activate", Json(new { key = "DKA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA" }))).StatusCode);   // unknown key
            var act = await admin.PostAsync("/api/platform/license/activate", Json(new { key = key.ToLowerInvariant() }));
            Assert.True(act.IsSuccessStatusCode, await act.Content.ReadAsStringAsync());
            lic = await J(act);
            Assert.Equal("onprem-test", lic.GetProperty("plan").GetString());
            Assert.True(lic.GetProperty("boundToInstallation").GetBoolean());
            var activation = await J(await admin.GetAsync("/api/platform/license/activation"));
            Assert.True(activation.GetProperty("activation").GetProperty("activated").GetBoolean());
            Assert.Equal(key[^4..], activation.GetProperty("activation").GetProperty("keyHint").GetString());
            var installation = activation.GetProperty("installation").GetString()!;

            // the key is in use: another installation is refused, the same one may activate again
            var anon = factory.CreateClient();
            var other = await anon.PostAsync("/api/store/activate", Json(new { key, installation = new string('b', 32), tenant = "dev" }));
            Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
            Assert.Equal("activations_used", (await J(other)).GetProperty("error").GetString());
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/platform/license/activate", Json(new { key }))).StatusCode);
            var wrongCustomer = await anon.PostAsync("/api/store/licence", Json(new { activation = Guid.NewGuid(), secret = "x" }));
            Assert.Equal(HttpStatusCode.NotFound, wrongCustomer.StatusCode);

            var bound = await J(await admin.GetAsync("/api/commerce/subscriptions/" + onpremId));
            Assert.Equal("dev", bound.GetProperty("sub").GetProperty("tenantId").GetString());   // set at the first activation
            Assert.Equal(installation, bound.GetProperty("keys")[0].GetProperty("activations")[0].GetProperty("installation").GetString());

            // 6. the vendor extends the subscription; the installation's check picks it up
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/commerce/subscriptions/" + onpremId, Json(new { endsAt = Day(400) }))).StatusCode);
            var refresh = await admin.PostAsync("/api/platform/license/refresh", null);
            Assert.True(refresh.IsSuccessStatusCode, await refresh.Content.ReadAsStringAsync());
            lic = await J(await admin.GetAsync("/api/platform/license"));
            Assert.Equal("onprem-test", lic.GetProperty("plan").GetString());
            Assert.True(DateTime.Parse(lic.GetProperty("expires").GetString()!).ToUniversalTime() > DateTime.UtcNow.AddDays(390));
            Assert.Equal("The licence is up to date.", (await J(await admin.PostAsync("/api/platform/license/refresh", null))).GetProperty("result").GetString());

            // an offline licence file for the same installation
            var file = await J(await admin.PostAsync("/api/commerce/subscriptions/" + onpremId + "/licence", Json(new { tenantId = "dev", installation })));
            Assert.Equal("dev", LicenseCodec.Verify(file.GetProperty("token").GetString(), Vendor, "dev", DateTimeOffset.UtcNow).Payload!.Tenant);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/commerce/licences/" + file.GetProperty("id").GetInt64())).StatusCode);

            // 7. release: the activation is freed; checking again says so; a revoked key cannot be activated
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/platform/license/release", null)).StatusCode);
            Assert.False((await J(await admin.GetAsync("/api/platform/license/activation"))).GetProperty("activation").GetProperty("activated").GetBoolean());
            Assert.Equal(HttpStatusCode.OK, (await anon.PostAsync("/api/store/activate", Json(new { key, installation = new string('b', 32), tenant = "dev" }))).StatusCode);   // the place is free again
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/commerce/keys/" + k.GetProperty("id").GetString() + "/revoke", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Gone, (await anon.PostAsync("/api/store/activate", Json(new { key, installation = new string('c', 32), tenant = "dev" }))).StatusCode);

            // 8. the SaaS subscription ends: modules turn read-only (reading works, changes do not)
            await admin.DeleteAsync("/api/platform/settings/" + LicenseResolver.SettingKey);   // only the catalog licence counts from here
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/commerce/subscriptions/" + saasId + "/end", null)).StatusCode);
            lic = await J(await admin.GetAsync("/api/platform/license"));
            Assert.Equal("expired", lic.GetProperty("state").GetString());
            Assert.Contains(lic.GetProperty("readOnly").EnumerateArray(), m => m.GetString() == "servicedesk");
            var mods = await J(await admin.GetAsync("/api/platform/modules"));
            Assert.True(mods.EnumerateArray().First(m => m.GetProperty("key").GetString() == "servicedesk").GetProperty("readOnly").GetBoolean());
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/servicedesk/tickets")).StatusCode);
            var write = await admin.PostAsync("/api/servicedesk/tickets", Json(new { subject = "x" }));
            Assert.Equal(HttpStatusCode.PaymentRequired, write.StatusCode);
            Assert.Equal("license_expired", (await J(write)).GetProperty("error").GetString());

            var audit = await J(await admin.GetAsync("/api/platform/audit?action=commerce."));
            Assert.Contains(audit.EnumerateArray(), r => r.GetProperty("action").GetString() == "commerce.subscription_created");
            Assert.Contains(audit.EnumerateArray(), r => r.GetProperty("action").GetString() == "commerce.key_created");
        }
        finally
        {
            Activation.TestHandler = null;
            await CleanAsync(admin);
        }
    }

    [Fact]
    public void ALicenceBoundToAnInstallationIsRefusedElsewhere()
    {
        var now = DateTimeOffset.UtcNow;
        var token = LicenseCodec.Issue(Vendor, new LicensePayload(1, "acme", "p", now.ToUnixTimeSeconds(), now.AddDays(-1).ToUnixTimeSeconds(), now.AddDays(30).ToUnixTimeSeconds(), 0,
            new() { ["assets"] = new() }, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        var l = LicenseCodec.Verify(token, Vendor, "acme", now);
        Assert.Equal(LicenseState.Valid, LicenseResolver.Bound(l, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa").State);
        Assert.Equal(LicenseState.Invalid, LicenseResolver.Bound(l, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb").State);
        Assert.Equal(LicenseState.Invalid, LicenseResolver.Bound(l, null).State);
        Assert.False(LicenseResolver.Bound(l, null).Has("assets"));
    }

    [Fact]
    public void LicenceKeysAreReadableAndNormalized()
    {
        var k = DeskAsk.Platform.Commerce.Licences.NewKey();
        Assert.Matches("^DKA-([A-Z2-9]{5}-){4}[A-Z2-9]{5}$", k);
        Assert.Equal(25, DeskAsk.Platform.Commerce.Licences.NormalizeKey(k).Length);
        Assert.Equal(DeskAsk.Platform.Commerce.Licences.NormalizeKey(k), DeskAsk.Platform.Commerce.Licences.NormalizeKey(" " + k.ToLowerInvariant().Replace("-", " ") + " "));
        Assert.DoesNotContain('O', k); Assert.DoesNotContain('0', k); Assert.DoesNotContain('I', k); Assert.DoesNotContain('1', k);
    }

    [PgFact]
    public async Task ConsoleIsClosedToAnonymousAndToOtherInstallations()
    {
        var anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/commerce/subscriptions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/commerce/status")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.PostAsync("/api/store/activate", Json(new { key = "DKA-AAAAA-AAAAA-AAAAA-AAAAA-AAAAA", installation = new string('a', 32), tenant = "dev" }))).StatusCode);   // unknown key

        // an ordinary installation (store not turned on): the console and store addresses do not exist
        var plain = factory.WithWebHostBuilder(b => b.UseSetting("Commerce:Enabled", "false"));
        var admin = await TestSupport.AdminClient(plain);
        Assert.False((await J(await admin.GetAsync("/api/commerce/status"))).GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/commerce/subscriptions")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await plain.CreateClient().PostAsync("/api/store/activate", Json(new { key = "x" }))).StatusCode);
    }
}
