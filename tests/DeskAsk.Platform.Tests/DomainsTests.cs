using System.Net;
using System.Text;
using System.Text.Json;
using DeskAsk.Platform;
using DeskAsk.Platform.Identity;
using DeskAsk.Platform.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

/// <summary>A customer's own web address: added, proved by DNS, then used to find the customer.</summary>
public class DomainsTests : IClassFixture<WebApplicationFactory<Program>>
{
    sealed class FakeDns : IDnsLookup
    {
        public Dictionary<string, string[]> Txt = new(), Cname = new();
        public Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(Txt.GetValueOrDefault(name) ?? Array.Empty<string>());
        public Task<IReadOnlyList<string>> CnameAsync(string name, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(Cname.GetValueOrDefault(name) ?? Array.Empty<string>());
    }

    static readonly FakeDns Dns = new();
    readonly WebApplicationFactory<Program> factory;
    public DomainsTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b =>
    {
        TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true");
        b.ConfigureServices(s => { s.RemoveAll<IDnsLookup>(); s.AddSingleton<IDnsLookup>(Dns); });
    });
    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    [Fact]
    public void OnlyRealNamesOfOurOwnAreAccepted()
    {
        var cfg = new ConfigurationBuilder().Build();
        Assert.Null(DomainsApi.Problem("desk.svjlasertech.com", cfg));
        Assert.Null(DomainsApi.Problem("svjlasertech.com", cfg));
        foreach (var bad in new[] { "localhost", "10.0.0.1", "not a host", "-x.example.com", "a..b.com", "https://x.com", "acme.deskask.in", "deskask.in", "x.com/path" })
            Assert.NotNull(DomainsApi.Problem(bad, cfg));
    }

    [Fact]
    public async Task AVerifiedAddressFindsTheCustomerAndOnlyThen()
    {
        var cat = new InMemoryTenantCatalog(new[] { new TenantInfo("t1", "Svj", "svj.deskask.in", ""), new TenantInfo("t2", "Other", "other.deskask.in", "") });
        Assert.Null(await cat.AddAsync("t1", "Desk.Svjlasertech.com", "tok", 5));
        // not proved yet: nobody is found by it
        Assert.Null(await cat.FindByHostAsync("desk.svjlasertech.com"));
        // the same address cannot be taken by another customer, nor can a built-in address
        Assert.NotNull(await cat.AddAsync("t2", "desk.svjlasertech.com", "x", 5));
        Assert.NotNull(await cat.AddAsync("t2", "svj.deskask.in", "x", 5));
        Assert.True(await cat.MarkVerifiedAsync("t1", "desk.svjlasertech.com"));
        Assert.Equal("t1", (await cat.FindByHostAsync("DESK.svjlasertech.com:443"))!.Id);
        // the built-in address keeps working, and links use the main one only once one is chosen
        Assert.Equal("svj.deskask.in", (await cat.FindByHostAsync("svj.deskask.in"))!.Host);
        Assert.True(await cat.SetPrimaryAsync("t1", "desk.svjlasertech.com", true));
        Assert.Equal("desk.svjlasertech.com", (await cat.FindByHostAsync("svj.deskask.in"))!.Host);
        Assert.Equal("svj.deskask.in", await cat.BuiltInHostAsync("t1"));
        Assert.Equal(new[] { "desk.svjlasertech.com" }, await cat.VerifiedHostsAsync());
        Assert.True(await cat.SetPrimaryAsync("t1", "desk.svjlasertech.com", false));
        Assert.Equal("svj.deskask.in", (await cat.FindByHostAsync("svj.deskask.in"))!.Host);
        // a limit, and removal
        for (var i = 0; i < 4; i++) Assert.Null(await cat.AddAsync("t1", $"d{i}.svjlasertech.com", "k", 5));
        Assert.NotNull(await cat.AddAsync("t1", "d9.svjlasertech.com", "k", 5));
        Assert.True(await cat.RemoveAsync("t1", "desk.svjlasertech.com"));
        Assert.Null(await cat.FindByHostAsync("desk.svjlasertech.com"));
    }

    [PgFact]
    public async Task AdministratorsAddProveAndMakeItTheMainAddress()
    {
        var admin = await TestSupport.AdminClient(factory); var anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/platform/domains")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/platform/domains", Json(new { host = "acme.deskask.in" }))).StatusCode);

        var add = await admin.PostAsync("/api/platform/domains", Json(new { host = "desk.svjlasertech.com" }));
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var d = await J(add);
        var token = d.GetProperty("txtValue").GetString()!;
        Assert.Equal("_deskask-verify.desk.svjlasertech.com", d.GetProperty("txtName").GetString());
        Assert.False(d.GetProperty("verified").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/platform/domains", Json(new { host = "desk.svjlasertech.com" }))).StatusCode);

        // the record is not there yet
        var no = await J(await admin.PostAsync("/api/platform/domains/desk.svjlasertech.com/verify", Json(new { })));
        Assert.False(no.GetProperty("verified").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/platform/domains/desk.svjlasertech.com/primary", Json(new { primary = true }))).StatusCode);

        Dns.Txt["_deskask-verify.desk.svjlasertech.com"] = new[] { token };
        var yes = await J(await admin.PostAsync("/api/platform/domains/desk.svjlasertech.com/verify", Json(new { })));
        Assert.True(yes.GetProperty("verified").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/platform/domains/desk.svjlasertech.com/primary", Json(new { primary = true }))).StatusCode);

        // the proxy may ask for a certificate for it, and for nothing else
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/api/platform/domain-check?domain=desk.svjlasertech.com")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/platform/domain-check?domain=evil.example.com")).StatusCode);

        // the proxy's list needs the shared key, and is off without one
        var list = new HttpRequestMessage(HttpMethod.Get, "/api/platform/proxy/domains"); list.Headers.Add("X-Proxy-Key", "a-long-shared-proxy-key-1");
        Assert.Equal(HttpStatusCode.NotFound, (await anon.SendAsync(list)).StatusCode);
        var keyed = factory.WithWebHostBuilder(b => b.UseSetting("Platform:ProxyKey", "a-long-shared-proxy-key-1")).CreateClient();
        var ok = new HttpRequestMessage(HttpMethod.Get, "/api/platform/proxy/domains"); ok.Headers.Add("X-Proxy-Key", "a-long-shared-proxy-key-1");
        var got = await keyed.SendAsync(ok);   // a host of its own, so its list is its own (empty here)
        Assert.Equal(HttpStatusCode.OK, got.StatusCode);
        Assert.Equal(JsonValueKind.Array, (await J(got)).GetProperty("hosts").ValueKind);
        var bad = new HttpRequestMessage(HttpMethod.Get, "/api/platform/proxy/domains"); bad.Headers.Add("X-Proxy-Key", "wrong-key-wrong-key-1");
        Assert.Equal(HttpStatusCode.NotFound, (await keyed.SendAsync(bad)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/platform/domains/desk.svjlasertech.com")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/platform/domain-check?domain=desk.svjlasertech.com")).StatusCode);
    }
}
