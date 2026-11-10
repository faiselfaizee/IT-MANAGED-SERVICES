using System.Net;
using DeskAsk.Platform;
using DeskAsk.Platform.Tenancy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

public class HostNameTests
{
    [Theory]
    [InlineData("Acme.DeskAsk.in:443", "acme.deskask.in")]
    [InlineData("acme.deskask.in.", "acme.deskask.in")]
    [InlineData("  localhost ", "localhost")]
    [InlineData(null, "")]
    public void Normalizes(string? input, string expected) => Assert.Equal(expected, HostName.Normalize(input));
}

public class CatalogTests
{
    [Fact]
    public async Task FindsActiveTenantByHost_IgnoresCaseAndPort()
    {
        var c = new InMemoryTenantCatalog(new[] { new TenantInfo("acme", "Acme", "acme.deskask.in", "x") });
        Assert.Equal("acme", (await c.FindByHostAsync("ACME.deskask.in:8443"))?.Id);
    }

    [Fact]
    public async Task InactiveOrUnknownTenantIsNotFound()
    {
        var c = new InMemoryTenantCatalog(new[] { new TenantInfo("old", "Old", "old.deskask.in", "x", false) });
        Assert.Null(await c.FindByHostAsync("old.deskask.in"));
        Assert.Null(await c.FindByHostAsync("nobody.deskask.in"));
    }
}

public class HostTests : IClassFixture<WebApplicationFactory<Program>>
{
    readonly WebApplicationFactory<Program> f;
    public HostTests(WebApplicationFactory<Program> f) => this.f = f;

    [Fact]
    public async Task HealthAnswersWithoutATenant()
    {
        var r = await f.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task WhoAmIKnowsTheDevelopmentTenant()
    {
        var r = await f.CreateClient().GetAsync("/api/platform/whoami");   // test client host is "localhost"
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("\"tenant\":\"dev\"", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnknownAddressIs404()
    {
        var c = f.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/platform/whoami");
        req.Headers.Host = "stranger.example";
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(req)).StatusCode);
    }
}

public class WebAppFilesTests : IClassFixture<WebApplicationFactory<Program>>
{
    readonly WebApplicationFactory<Program> f;
    public WebAppFilesTests(WebApplicationFactory<Program> f) => this.f = f;

    [Fact]
    public async Task ScriptsAreServedAsFiles_PagesFallBackToIndex_MissingFilesAre404()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wr" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "assets"));
        File.WriteAllText(Path.Combine(dir, "index.html"), "<html>INDEX-PAGE</html>");
        File.WriteAllText(Path.Combine(dir, "assets", "app.js"), "console.log(1)");
        try
        {
            var c = f.WithWebHostBuilder(b => b.UseWebRoot(dir)).CreateClient();
            var js = await c.GetAsync("/assets/app.js");
            Assert.Equal(HttpStatusCode.OK, js.StatusCode);
            Assert.Contains("javascript", js.Content.Headers.ContentType!.MediaType);
            Assert.Contains("console.log", await js.Content.ReadAsStringAsync());

            var page = await c.GetAsync("/auth/callback");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("INDEX-PAGE", await page.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/assets/missing.js")).StatusCode);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
