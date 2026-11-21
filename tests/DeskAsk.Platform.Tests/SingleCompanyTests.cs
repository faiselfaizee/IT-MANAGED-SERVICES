using System.Net;
using System.Text;
using System.Text.Json;
using DeskAsk.Platform;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

/// <summary>Single-company mode (licence limit single_company=1): the customer's own name is the one company and a second cannot be added.</summary>
public class SingleCompanyTests : IClassFixture<WebApplicationFactory<Program>>
{
    readonly WebApplicationFactory<Program> plain, single;
    public SingleCompanyTests(WebApplicationFactory<Program> f)
    {
        plain = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });
        single = f.WithWebHostBuilder(b =>
        {
            TestSupport.Configure(b);
            b.ConfigureTestServices(s => { s.RemoveAll<IEntitlements>(); s.AddScoped<IEntitlements>(_ => new Fixed()); });
        });
    }

    sealed class Fixed : IEntitlements
    {
        public Task<License> CurrentAsync(CancellationToken ct = default) => Task.FromResult(new License(LicenseState.Valid,
            new LicensePayload(1, "t", "p", 0, 0, long.MaxValue / 2, 0, new() { ["assets"] = new ModuleGrant(new() { ["max_assets"] = 100, ["single_company"] = 1 }), ["servicedesk"] = new ModuleGrant() }), ""));
    }

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    [Fact]
    public void TheLicenceSwitchIsKnownAndOnlyOneMeansOn()
    {
        Assert.Contains("single_company", LicenseLimits.Known["assets"]); Assert.Contains("single_company", LicenseLimits.Known["monitoring"]); Assert.Contains("single_company", LicenseLimits.Known["servicedesk"]);
        License L(int? v) => new(LicenseState.Valid, new LicensePayload(1, "t", "p", 0, 0, long.MaxValue / 2, 0, new() { ["servicedesk"] = new ModuleGrant(v == null ? null : new() { ["single_company"] = v.Value }) }), "");
        Assert.True(L(1).SingleCompany); Assert.False(L(0).SingleCompany); Assert.False(L(null).SingleCompany);
        Assert.False(new License(LicenseState.Expired, L(1).Payload, "").SingleCompany);
    }

    [PgFact]
    public async Task AnOrdinaryLicenceKeepsSeveralCompanies()
    {
        var admin = await TestSupport.AdminClient(plain);
        Assert.False((await J(await admin.GetAsync("/api/platform/mode"))).GetProperty("singleCompany").GetBoolean());
        var r = await admin.PostAsync("/api/assets/companies", Json(new { name = "SC Many " + Guid.NewGuid().ToString("N")[..6] }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var r2 = await admin.PostAsync("/api/assets/companies", Json(new { name = "SC Many " + Guid.NewGuid().ToString("N")[..6] }));
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
    }

    [PgFact]
    public async Task InSingleCompanyModeTheCustomersOwnNameIsTheOneCompanyAndNoSecondCanBeAdded()
    {
        var admin = await TestSupport.AdminClient(single);
        Assert.True((await J(await admin.GetAsync("/api/platform/mode"))).GetProperty("singleCompany").GetBoolean());
        var list = await J(await admin.GetAsync("/api/assets/companies"));
        Assert.True(list.GetArrayLength() >= 1);                                          // when there is none, the customer's own name is made the first time it is needed
        var again = (await J(await admin.GetAsync("/api/assets/companies"))).GetArrayLength();
        Assert.Equal(list.GetArrayLength(), again);                                       // and listing never adds another
        var second = await admin.PostAsync("/api/assets/companies", Json(new { name = "A second company" }));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("single_company", (await J(second)).GetProperty("error").GetString());
        var id = list[0].GetProperty("id").GetInt32();                                    // a company can still be renamed
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/assets/companies/" + id, Json(new { name = list[0].GetProperty("name").GetString() }))).StatusCode);
    }

    [PgFact]
    public async Task ANewCustomerLoginIsTiedToTheOneCompanyAtOnce()
    {
        var admin = await TestSupport.AdminClient(single);
        await admin.GetAsync("/api/assets/companies");
        var pw = "Long-Enough-1"; var email = "cust" + Guid.NewGuid().ToString("N")[..6] + "@sc.test";
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/platform/users", Json(new { email, name = "Cust", role = "customer", password = pw }))).StatusCode);
        var cust = await TestSupport.ClientFor(single, email, pw);
        var mine = await J(await cust.GetAsync("/api/assets/companies"));
        Assert.Equal(1, mine.GetArrayLength());   // nobody chose a company for them: the one company is theirs
    }
}
