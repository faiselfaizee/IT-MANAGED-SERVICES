using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

public class UpdatesNotSetUpTests : IClassFixture<WebApplicationFactory<Program>>
{
    readonly WebApplicationFactory<Program> factory;
    public UpdatesNotSetUpTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(TestSupport.Configure);

    [PgFact]
    public async Task AnonymousIsRefused()
    {
        var anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/platform/updates")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/platform/updates?name=x.dkb", new ByteArrayContent(new byte[300]))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.DeleteAsync("/api/platform/updates/aaaaaaaaaaaaaaaa")).StatusCode);
    }

    [PgFact]
    public async Task WithoutTheUpdaterServiceTheAnswerIsNotInstalled()
    {
        var admin = await TestSupport.AdminClient(factory);
        Assert.Contains("\"installed\":false", await admin.GetStringAsync("/api/platform/updates"));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/platform/updates?name=x.dkb", new ByteArrayContent(new byte[300]))).StatusCode);
    }
}
