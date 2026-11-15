using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

public class IncidentPriorityTests
{
    [Theory]
    [InlineData("high", "high", "critical")]
    [InlineData("high", "medium", "high")]
    [InlineData("medium", "high", "high")]
    [InlineData("medium", "medium", "medium")]
    [InlineData("high", "low", "medium")]
    [InlineData("low", "high", "medium")]
    [InlineData("medium", "low", "low")]
    [InlineData("low", "low", "low")]
    public void ImpactAndUrgencyGivePriority(string impact, string urgency, string priority) =>
        Assert.Equal(priority, DeskAsk.Platform.ServiceDesk.SlaConfig.PriorityFor(impact, urgency));
}

public class IncidentTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public IncidentTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }
    static Task<HttpResponseMessage> Patch(HttpClient c, string url, object body) => c.SendAsync(new HttpRequestMessage(HttpMethod.Patch, url) { Content = Json(body) });

    [PgFact]
    public async Task IncidentsAndRequestsImpactUrgencyAndMajorIncidents()
    {
        var admin = await TestSupport.AdminClient(factory);
        await admin.GetAsync("/api/servicedesk/me");
        await Sql("truncate servicedesk.tickets, servicedesk.people, servicedesk.events restart identity cascade");
        async Task<long> Raise(object body) { var r = await admin.PostAsync("/api/servicedesk/tickets", Json(body)); Assert.Equal(HttpStatusCode.Created, r.StatusCode); return (await J(r)).GetProperty("id").GetInt64(); }
        async Task<JsonElement> Get(long id) => await J(await admin.GetAsync("/api/servicedesk/tickets/" + id));
        async Task<int> Count(string qs) => (await J(await admin.GetAsync("/api/servicedesk/tickets?status=all&" + qs))).GetProperty("total").GetInt32();

        // a ticket is an incident unless it is raised as a request; impact and urgency choose the priority when none is given
        var inc = await Raise(new { subject = "Mail is down", impact = "high", urgency = "high" });
        var t = (await Get(inc)).GetProperty("ticket");
        Assert.Equal("incident", t.GetProperty("kind").GetString()); Assert.Equal("critical", t.GetProperty("priority").GetString());
        Assert.Equal("high", t.GetProperty("impact").GetString()); Assert.False(t.GetProperty("major").GetBoolean());
        var req = await Raise(new { subject = "New laptop for Ravi", kind = "request" });
        Assert.Equal("request", (await Get(req)).GetProperty("ticket").GetProperty("kind").GetString());
        var plain = await Raise(new { subject = "Printer jams", priority = "low", impact = "high", urgency = "high" });  // a chosen priority wins
        Assert.Equal("low", (await Get(plain)).GetProperty("ticket").GetProperty("priority").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/servicedesk/tickets", Json(new { subject = "x", kind = "wish" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/servicedesk/tickets", Json(new { subject = "x", impact = "huge" }))).StatusCode);

        // filters
        Assert.Equal(2, await Count("kind=incident")); Assert.Equal(1, await Count("kind=request")); Assert.Equal(0, await Count("major=1"));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/servicedesk/tickets?kind=other")).StatusCode);

        // changing impact or urgency sets the priority again; the type can be changed; a major incident is flagged
        Assert.Equal(HttpStatusCode.OK, (await Patch(admin, "/api/servicedesk/tickets/" + inc, new { urgency = "low" })).StatusCode);
        Assert.Equal("medium", (await Get(inc)).GetProperty("ticket").GetProperty("priority").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Patch(admin, "/api/servicedesk/tickets/" + inc, new { major = true })).StatusCode);
        var m = (await Get(inc)).GetProperty("ticket"); Assert.True(m.GetProperty("major").GetBoolean()); Assert.NotEqual(JsonValueKind.Null, m.GetProperty("majorAt").ValueKind);
        Assert.Equal(1, await Count("major=1"));
        Assert.Equal(HttpStatusCode.OK, (await Patch(admin, "/api/servicedesk/tickets/" + req, new { kind = "incident" })).StatusCode);
        Assert.Equal(3, await Count("kind=incident"));
        Assert.Equal(HttpStatusCode.OK, (await Patch(admin, "/api/servicedesk/tickets/" + inc, new { major = false })).StatusCode);
        Assert.Equal(0, await Count("major=1"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Patch(admin, "/api/servicedesk/tickets/" + inc, new { kind = "wish" })).StatusCode);
    }
}
