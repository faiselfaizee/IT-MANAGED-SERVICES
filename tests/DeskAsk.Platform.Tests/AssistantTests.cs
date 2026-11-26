using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

/// <summary>6.23.0: the Assistant answers plain-English questions from the person's own data.</summary>
public class AssistantTests : IClassFixture<WebApplicationFactory<Program>>
{
    readonly WebApplicationFactory<Program> factory;
    public AssistantTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });
    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    [PgFact]
    public async Task QuestionsAreAnsweredFromTheInventoryAndTheLinksBetweenAssets()
    {
        var admin = await TestSupport.AdminClient(factory);
        await admin.GetAsync("/api/assets/types");
        async Task<long> Make(string name) => (await J(await admin.PostAsync("/api/assets", Json(new { name })))).GetProperty("id").GetInt64();
        async Task<JsonElement> Ask(string q) { var r = await admin.PostAsync("/api/assets/assistant/ask", Json(new { q })); Assert.Equal(HttpStatusCode.OK, r.StatusCode); return await J(r); }
        string[] Names(JsonElement a) => a.GetProperty("tables").EnumerateArray().SelectMany(t => t.GetProperty("rows").EnumerateArray().SelectMany(r => r.EnumerateArray().Select(c => c.GetString() ?? ""))).ToArray();

        var core = await Make("asst-core-switch"); var app = await Make("asst-billing-app"); await Make("asst-lonely-box");
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/assets/{app}/relations", Json(new { toId = core, kind = "depends_on" }))).StatusCode);

        // what fails with it, found by part of the name
        var impact = await Ask("What is affected if the asst-core-switch fails?");
        Assert.Contains("asst-billing-app", Names(impact)); Assert.DoesNotContain("asst-lonely-box", Names(impact));
        Assert.Contains("asst-core-switch", impact.GetProperty("text").GetString());
        var none = await Ask("what depends on asst-lonely-box");
        Assert.Empty(none.GetProperty("tables").EnumerateArray()); Assert.Contains("Nothing is recorded", none.GetProperty("text").GetString());
        Assert.Contains("could not find", (await Ask("what is affected if zz-nothing-here fails")).GetProperty("text").GetString());

        // a plain search, a count and the help answer
        Assert.Contains("asst-lonely-box", Names(await Ask("find asst-lonely-box")));
        var count = await Ask("how many assets do we have");
        Assert.True(count.GetProperty("count").GetInt32() >= 3); Assert.Contains("You have", count.GetProperty("text").GetString());
        Assert.NotEmpty((await Ask("help")).GetProperty("suggestions").EnumerateArray());

        // questions that touch other modules answer, and never fail, even when nothing matches
        foreach (var q in new[] { "show overdue tickets", "tickets with no engineer", "open critical tickets", "show open critical alerts", "which devices are down", "warranty ending in 30 days", "assets out of warranty",
                                  "computers low on disk space", "which computers are not protected by antivirus", "computers not reporting for 7 days", "which computers have chrome installed", "what is assigned to ravi" })
            Assert.False(string.IsNullOrEmpty((await Ask(q)).GetProperty("text").GetString()), q);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/assets/assistant/ask", Json(new { q = "  " }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/assets/assistant/ask", Json(new { q = new string('x', 301) }))).StatusCode);
    }
}
