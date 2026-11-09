using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using DeskAsk.Platform;
using DeskAsk.Platform.Identity;
using Npgsql;
using Xunit;

public class KeysTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public KeysTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(TestSupport.Configure);

    async Task<TenantKeys> Keys()
    {
        await factory.CreateClient().GetAsync("/health");   // not needed for health itself, but starts the host
        await factory.CreateClient().GetAsync("/api/platform/whoami");   // goes through tenancy + schemas, so identity.keys exists
        return factory.Services.GetRequiredService<TenantKeys>();
    }

    static string KidOf(string jwt)
    {
        var h = jwt.Split('.')[0].Replace('-', '+').Replace('_', '/'); h = h.PadRight(h.Length + (4 - h.Length % 4) % 4, '=');
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(h))).RootElement.GetProperty("kid").GetString()!;
    }

    static string Token(HttpClient api) => api.DefaultRequestHeaders.Authorization!.Parameter!;

    static async Task<string[]> Jwks(HttpClient c) =>
        JsonDocument.Parse(await c.GetStringAsync("/.well-known/jwks")).RootElement.GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("kid").GetString()!).ToArray();

    [PgFact]
    public async Task EachCustomerHasItsOwnKey()
    {
        var keys = await Keys();
        var a = keys.Get(new TenantInfo("alpha", "Alpha", "alpha.test", Pg));
        var b = keys.Get(new TenantInfo("beta", "Beta", "beta.test", Pg));
        Assert.NotEqual(a.Kids[0], b.Kids[0]);
        Assert.NotEqual(a.Signing[0].Key, b.Signing[0].Key);
        // the key that protects refresh tokens differs too
        Assert.NotEqual(((Microsoft.IdentityModel.Tokens.SymmetricSecurityKey)a.Encryption.Key).Key, ((Microsoft.IdentityModel.Tokens.SymmetricSecurityKey)b.Encryption.Key).Key);
    }

    [PgFact]
    public async Task TheKeyIsKeptAndTheStoredCopyIsSealed()
    {
        var keys = await Keys();
        var t = new TenantInfo("gamma", "Gamma", "gamma.test", Pg);
        var kid = keys.Get(t).Kids[0];
        var again = new TenantKeys(factory.Services.GetRequiredService<IConfiguration>()).Get(t);   // a new process: reads the stored key
        Assert.Equal(kid, again.Kids[0]);
        await using var db = new NpgsqlConnection(Pg); await db.OpenAsync();
        await using var cmd = new NpgsqlCommand("select private_sealed from identity.keys where tenant_id = 'gamma' and retired_at is null", db);
        var stored = (string)(await cmd.ExecuteScalarAsync())!;
        Assert.DoesNotContain("PRIVATE KEY", Encoding.UTF8.GetString(Convert.FromBase64String(stored)));
    }

    [PgFact]
    public async Task TokensAreSignedWithTheCustomersKeyAndItIsPublished()
    {
        var keys = await Keys();
        var api = await TestSupport.AdminClient(factory);
        var dev = (await factory.Services.GetRequiredService<ITenantCatalog>().ListActiveAsync()).First();
        Assert.Equal(keys.Get(dev).Kids[0], KidOf(Token(api)));
        Assert.Contains(KidOf(Token(api)), await Jwks(factory.CreateClient()));
        var priv = await factory.CreateClient().GetStringAsync("/.well-known/jwks");
        Assert.DoesNotContain("\"d\"", priv);   // only the public half is published
    }

    [PgFact]
    public async Task RotatingMakesANewKeyAndOldTokensStillWork()
    {
        await Keys();
        var anon = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/platform/identity/keys/rotate", null)).StatusCode);

        var api = await TestSupport.AdminClient(factory);
        var oldKid = KidOf(Token(api));
        var res = await api.PostAsync("/api/platform/identity/keys/rotate", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var newKid = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("kid").GetString()!;
        Assert.NotEqual(oldKid, newKid);

        var published = await Jwks(factory.CreateClient());
        Assert.Contains(oldKid, published);   // still there for verifying
        Assert.Contains(newKid, published);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/platform/me")).StatusCode);   // the old token is still accepted

        var fresh = await TestSupport.AdminClient(factory);
        Assert.Equal(newKid, KidOf(Token(fresh)));   // new sign-ins use the new key
    }
}
