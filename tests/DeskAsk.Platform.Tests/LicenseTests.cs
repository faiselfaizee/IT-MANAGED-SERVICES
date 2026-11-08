using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeskAsk.Platform;
using DeskAsk.Platform.Licensing;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

public class LicenseCodecTests
{
    static readonly ECDsa Vendor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    static LicensePayload P(string tenant = "acme", int expiresInDays = 30, int grace = 14, int startsInDays = 0) =>
        new(1, tenant, "business", Now.ToUnixTimeSeconds(), Now.AddDays(startsInDays).ToUnixTimeSeconds(), Now.AddDays(expiresInDays).ToUnixTimeSeconds(), grace,
            new() { ["servicedesk"] = new(new() { ["agents"] = 10 }), ["assets"] = new() });
    static string Issue(LicensePayload p) => LicenseCodec.Issue(Vendor, p);

    [Fact] public void ValidLicenceGrantsItsModulesAndLimits()
    {
        var l = LicenseCodec.Verify(Issue(P()), Vendor, "acme", Now);
        Assert.Equal(LicenseState.Valid, l.State);
        Assert.True(l.Has("servicedesk")); Assert.True(l.Has("assets")); Assert.False(l.Has("vault"));
        Assert.Equal(10, l.Limit("servicedesk", "agents")); Assert.Null(l.Limit("assets", "agents"));
    }
    [Fact] public void ChangedContentIsInvalid()
    {
        var parts = Issue(P()).Split('.');
        var evil = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(P(expiresInDays: 9999), new JsonSerializerOptions(JsonSerializerDefaults.Web))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(LicenseState.Invalid, LicenseCodec.Verify(parts[0] + "." + evil + "." + parts[2], Vendor, "acme", Now).State);
    }
    [Fact] public void LicenceFromAnotherVendorKeyIsInvalid()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal(LicenseState.Invalid, LicenseCodec.Verify(LicenseCodec.Issue(other, P()), Vendor, "acme", Now).State);
    }
    [Fact] public void LicenceOfAnotherCustomerIsInvalid() =>
        Assert.Equal(LicenseState.Invalid, LicenseCodec.Verify(Issue(P("other")), Vendor, "acme", Now).State);
    [Fact] public void WildcardLicenceWorksForAnyCustomer() =>
        Assert.Equal(LicenseState.Valid, LicenseCodec.Verify(Issue(P("*")), Vendor, "anyone", Now).State);
    [Fact] public void ExpiryGraceAndAfter()
    {
        var t = Issue(P(expiresInDays: 30, grace: 14));
        Assert.Equal(LicenseState.Valid, LicenseCodec.Verify(t, Vendor, "acme", Now.AddDays(30)).State);
        var g = LicenseCodec.Verify(t, Vendor, "acme", Now.AddDays(35));
        Assert.Equal(LicenseState.Grace, g.State); Assert.True(g.Has("assets"));
        var e = LicenseCodec.Verify(t, Vendor, "acme", Now.AddDays(45));
        Assert.Equal(LicenseState.Expired, e.State); Assert.False(e.Has("assets"));
    }
    [Fact] public void NotYetValid() =>
        Assert.Equal(LicenseState.NotYetValid, LicenseCodec.Verify(Issue(P(startsInDays: 5)), Vendor, "acme", Now).State);
    [Theory] [InlineData("")] [InlineData("a.b")] [InlineData("a.b.c")] [InlineData("not a licence at all")]
    public void GarbageIsInvalidNotAnException(string token) =>
        Assert.Equal(LicenseState.Invalid, LicenseCodec.Verify(token, Vendor, "acme", Now).State);
    [Fact] public void TokenWithAlgNoneIsRefused()
    {
        string B(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var body = Issue(P()).Split('.')[1];
        Assert.Equal(LicenseState.Invalid, LicenseCodec.Verify(B("{\"alg\":\"none\",\"typ\":\"dk-license\"}") + "." + body + ".", Vendor, "acme", Now).State);
    }
}

public class LicenseEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly ECDsa Vendor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    readonly WebApplicationFactory<Program> factory;
    public LicenseEndpointTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b =>
    {
        TestSupport.Configure(b);
        b.UseSetting("Licensing:VendorPublicKeyPem", Vendor.ExportSubjectPublicKeyInfoPem());
        b.UseSetting("Licensing:CacheSeconds", "0");
    });

    static StringContent Body(string token) => new(JsonSerializer.Serialize(new { token }), Encoding.UTF8, "application/json");
    static string Issue(string tenant = "dev", int days = 30, long? issued = null, int grace = 14, params string[] modules)
    {
        var now = DateTimeOffset.UtcNow;
        return LicenseCodec.Issue(Vendor, new LicensePayload(1, tenant, "business", issued ?? now.ToUnixTimeSeconds(), now.AddDays(-1).ToUnixTimeSeconds(), now.AddDays(days).ToUnixTimeSeconds(), grace,
            (modules.Length == 0 ? new[] { "servicedesk", "assets" } : modules).ToDictionary(m => m, _ => new ModuleGrant())));
    }
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    [PgFact]
    public async Task NoLicence_ThenInstall_ThenModulesOpen()
    {
        var admin = await TestSupport.AdminClient(factory);
        await admin.DeleteAsync("/api/platform/settings/" + LicenseResolver.SettingKey);

        var before = await J(await admin.GetAsync("/api/platform/license"));
        Assert.Equal("missing", before.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.GetAsync("/api/platform/license/check/assets")).StatusCode);

        var put = await admin.PutAsync("/api/platform/license", Body(Issue()));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var after = await J(put);
        Assert.Equal("valid", after.GetProperty("state").GetString());
        Assert.Equal("business", after.GetProperty("plan").GetString());

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/platform/license/check/assets")).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.GetAsync("/api/platform/license/check/vault")).StatusCode);
        var mods = await J(await admin.GetAsync("/api/platform/modules"));
        Assert.True(mods.EnumerateArray().First(m => m.GetProperty("key").GetString() == "servicedesk").GetProperty("licensed").GetBoolean());
        Assert.False(mods.EnumerateArray().First(m => m.GetProperty("key").GetString() == "backup").GetProperty("licensed").GetBoolean());

        var audit = await J(await admin.GetAsync("/api/platform/audit?action=license."));
        Assert.Contains(audit.EnumerateArray(), r => r.GetProperty("action").GetString() == "license.installed");
        await admin.DeleteAsync("/api/platform/settings/" + LicenseResolver.SettingKey);
    }

    [PgFact]
    public async Task BadLicencesAreRefused_AndAnOlderOneDoesNotReplaceANewerOne()
    {
        var admin = await TestSupport.AdminClient(factory);
        await admin.DeleteAsync("/api/platform/settings/" + LicenseResolver.SettingKey);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/platform/license", Body("garbage"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/platform/license", Body(Issue("another-customer")))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/platform/license", Body(Issue(days: -40, grace: 14)))).StatusCode);   // expired beyond grace
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = LicenseCodec.Issue(stranger, new LicensePayload(1, "dev", "x", 1, 0, long.MaxValue / 4, 0, new() { ["vault"] = new() }));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/platform/license", Body(forged))).StatusCode);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/platform/license", Body(Issue(issued: now, modules: new[] { "vault" })))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsync("/api/platform/license", Body(Issue(issued: now - 1000)))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/platform/license/check/vault")).StatusCode);   // the newer one is still in force
        await admin.DeleteAsync("/api/platform/settings/" + LicenseResolver.SettingKey);
    }

    [PgFact]
    public async Task OnlyAnAdministratorInstallsALicence_AndAnonymousSeesNothing()
    {
        var anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/platform/license")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsync("/api/platform/license", Body(Issue()))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/platform/modules")).StatusCode);
    }
}
