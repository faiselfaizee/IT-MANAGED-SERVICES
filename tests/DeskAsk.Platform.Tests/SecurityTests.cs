using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>Protections that must not quietly disappear: browser headers, limits on anonymous addresses, no open API route, no way to reach private networks.</summary>
public class SecurityTests : IClassFixture<WebApplicationFactory<Program>>
{
    readonly WebApplicationFactory<Program> factory;
    public SecurityTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    [PgFact]
    public async Task EveryResponseCarriesTheBrowserProtections()
    {
        var r = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", r.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", r.Headers.GetValues("Referrer-Policy").Single());
        var csp = r.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self'", csp); Assert.Contains("frame-ancestors 'none'", csp); Assert.DoesNotContain("unsafe-eval", csp);
        var api = await factory.CreateClient().GetAsync("/api/assets/types");
        Assert.Equal("no-store", api.Headers.CacheControl?.ToString());
    }

    [PgFact]
    public async Task HealthGivesNothingAwayToAStranger()
    {
        var body = await (await factory.CreateClient().GetAsync("/health")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("version", body); Assert.DoesNotContain("roles", body);
    }

    [PgFact]
    public async Task OneAddressCannotHammerTheSignInAddresses()
    {
        await using var f = factory.WithWebHostBuilder(b => { b.UseSetting("Security:RateLimits", "on"); b.UseSetting("Security:AuthPerMinute", "3"); });
        var c = f.CreateClient(); var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) codes.Add((await c.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "password" }))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[^1]);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, codes.Take(3));
        // other addresses of the platform are not limited
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health")).StatusCode);
    }

    [PgFact]
    public async Task NoApiRouteIsOpenByAccident()
    {
        await factory.CreateClient().GetAsync("/health");
        var open = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => (e.RoutePattern.RawText ?? "").StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() == null && e.Metadata.GetMetadata<IAllowAnonymous>() == null)
            .Select(e => e.RoutePattern.RawText).Distinct().ToList();
        Assert.True(open.Count == 0, "These /api routes have no authorization rule: " + string.Join(", ", open));
    }

    [Theory]
    [InlineData("127.0.0.1", false)] [InlineData("10.1.2.3", false)] [InlineData("172.16.0.5", false)] [InlineData("172.32.0.5", true)]
    [InlineData("192.168.0.1", false)] [InlineData("169.254.169.254", false)] [InlineData("100.64.0.1", false)] [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)] [InlineData("fe80::1", false)] [InlineData("fd00::1", false)] [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("8.8.8.8", true)] [InlineData("52.96.0.1", true)] [InlineData("2606:4700::1111", true)]
    public void OnlyPublicAddressesAreReachableByWebHooks(string ip, bool pub) => Assert.Equal(pub, DeskAsk.Platform.OutboundGuard.IsPublic(IPAddress.Parse(ip)));
}
