using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

/// <summary>Branding: anyone can read the look (the sign-in page needs it), only administrators change it, and a logo can never carry script.</summary>
public class BrandingTests : IClassFixture<WebApplicationFactory<Program>>
{
    readonly WebApplicationFactory<Program> factory;
    public BrandingTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });
    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static ByteArrayContent Raw(byte[] b, string type) { var c = new ByteArrayContent(b); c.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type); return c; }
    static readonly byte[] Png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };

    [Fact]
    public void LogoChecks()
    {
        Assert.Null(DeskAsk.Platform.Identity.BrandingApi.CheckLogo("image/png", Png));
        Assert.NotNull(DeskAsk.Platform.Identity.BrandingApi.CheckLogo("image/png", Encoding.ASCII.GetBytes("<html>not a png</html>")));
        Assert.NotNull(DeskAsk.Platform.Identity.BrandingApi.CheckLogo("application/pdf", Png));
        Assert.NotNull(DeskAsk.Platform.Identity.BrandingApi.CheckLogo("image/png", new byte[300 * 1024]));
        Assert.Null(DeskAsk.Platform.Identity.BrandingApi.CheckLogo("image/svg+xml", Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\" fill=\"#0d8a7f\"/></svg>")));
        foreach (var bad in new[] { "<svg><script>alert(1)</script></svg>", "<svg onload=\"x()\"></svg>", "<svg><foreignObject/></svg>", "<svg><a href=\"javascript:x()\"/></svg>", "<svg><image href=\"http://evil.test/x.png\"/></svg>", "<svg><style>@import 'x';</style></svg>" })
            Assert.NotNull(DeskAsk.Platform.Identity.BrandingApi.CheckLogo("image/svg+xml", Encoding.UTF8.GetBytes(bad)));
    }

    [PgFact]
    public async Task EveryoneReadsAdministratorsChange()
    {
        var admin = await TestSupport.AdminClient(factory); var anon = factory.CreateClient();
        await admin.PostAsync("/api/branding/reset", Json(new { }));
        var def = await J(await anon.GetAsync("/api/branding"));
        Assert.Equal("DeskAsk", def.GetProperty("name").GetString()); Assert.False(def.GetProperty("hasLogo").GetBoolean());

        // not signed in, and signed in as a non-administrator: refused
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsync("/api/branding", Json(new { name = "X" }))).StatusCode);
        var pw = "Long-Enough-1";
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/platform/users", Json(new { email = "eng@brand.test", name = "Eng", role = "engineer", password = pw }))).StatusCode);
        var eng = await TestSupport.ClientFor(factory, "eng@brand.test", pw);
        Assert.Equal(HttpStatusCode.Forbidden, (await eng.PutAsync("/api/branding", Json(new { name = "X" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await eng.PutAsync("/api/branding/logo", Raw(Png, "image/png"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await eng.PostAsync("/api/branding/reset", Json(new { }))).StatusCode);

        // bad values are refused
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/branding", Json(new { name = "X", accent = "red" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/branding", Json(new { name = "X", theme = "neon" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/branding", Json(new { name = "X", accent2 = "crimson" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/branding/logo", Raw(Encoding.UTF8.GetBytes("<svg><script>1</script></svg>"), "image/svg+xml"))).StatusCode);

        // an administrator sets it; the signed-out sign-in page then shows it
        var ok = await admin.PutAsync("/api/branding", Json(new { name = "Acme <IT>", headerText = "Acme IT desk", footerText = "Call 100 for help", accent = "#0D8A7F", accent2 = "#C31528", nav = "#112233", theme = "light" }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var now = await J(await anon.GetAsync("/api/branding"));
        Assert.Equal("Acme <IT>", now.GetProperty("name").GetString()); Assert.Equal("#0d8a7f", now.GetProperty("accent").GetString()); Assert.Equal("light", now.GetProperty("theme").GetString()); Assert.Equal("#c31528", now.GetProperty("accent2").GetString());

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/branding/logo", Raw(Png, "image/png"))).StatusCode);
        Assert.True((await J(await anon.GetAsync("/api/branding"))).GetProperty("hasLogo").GetBoolean());
        var logo = await anon.GetAsync("/api/branding/logo");
        Assert.Equal("image/png", logo.Content.Headers.ContentType!.MediaType); Assert.Equal(Png, await logo.Content.ReadAsByteArrayAsync());
        Assert.Contains("sandbox", logo.Headers.GetValues("Content-Security-Policy").Single());

        var page = await anon.GetStringAsync("/account/login");
        Assert.Contains("Acme &lt;IT&gt;", page); Assert.DoesNotContain("Acme <IT>", page);
        Assert.Contains("Call 100 for help", page); Assert.Contains("/api/branding/logo?v=", page); Assert.Contains("#0d8a7f", page); Assert.Contains("#c31528", page);

        // removing the logo and resetting
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync("/api/branding/logo")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/branding/logo")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/branding/reset", Json(new { }))).StatusCode);
        Assert.Equal("DeskAsk", (await J(await anon.GetAsync("/api/branding"))).GetProperty("name").GetString());
    }
}
