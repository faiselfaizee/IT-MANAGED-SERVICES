using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

public class ConnectTests : IClassFixture<WebApplicationFactory<Program>>
{
    readonly WebApplicationFactory<Program> factory;
    public ConnectTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(TestSupport.Configure);

    static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");
    HttpRequestMessage Beat(string token, string body = "{\"agentId\":\"pc-1\",\"version\":\"2.0.0-alpha.1\",\"plugins\":[\"assets\"]}")
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/connect/heartbeat") { Content = Json(body) };
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return r;
    }

    [PgFact]
    public async Task OnlyAnAdministratorManagesAgents()
    {
        var anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/connect/agents", Json("{\"name\":\"x\"}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/connect/agents")).StatusCode);
    }

    [PgFact]
    public async Task AgentLifecycle_Create_Heartbeat_List_Revoke()
    {
        var admin = await TestSupport.AdminClient(factory);
        var created = JsonDocument.Parse(await (await admin.PostAsync("/api/connect/agents", Json("{\"name\":\"Head office server\"}"))).Content.ReadAsStringAsync()).RootElement;
        var id = created.GetProperty("id").GetString()!; var token = created.GetProperty("token").GetString()!;
        Assert.StartsWith("dka_", token);

        await using (var db = new NpgsqlConnection(Environment.GetEnvironmentVariable("TEST_PG_CONNECTION")))
        {   // the token itself is never stored, only its hash
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand("select token_hash from connect.agents where id = @i", db);
            cmd.Parameters.AddWithValue("i", id);
            var stored = (string)(await cmd.ExecuteScalarAsync())!;
            Assert.NotEqual(token, stored); Assert.DoesNotContain(token, stored);
        }

        var anon = factory.CreateClient();
        var hb = await anon.SendAsync(Beat(token));
        Assert.Equal(HttpStatusCode.OK, hb.StatusCode);
        var answer = JsonDocument.Parse(await hb.Content.ReadAsStringAsync()).RootElement;
        Assert.True(answer.GetProperty("ok").GetBoolean());
        Assert.Equal(30, answer.GetProperty("intervalSeconds").GetInt32());

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(Beat("dka_notatoken"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(Beat("something-else"))).StatusCode);

        var list = JsonDocument.Parse(await admin.GetStringAsync("/api/connect/agents")).RootElement;
        var me = list.EnumerateArray().First(a => a.GetProperty("id").GetString() == id);
        Assert.True(me.GetProperty("online").GetBoolean());
        Assert.Equal("2.0.0-alpha.1", me.GetProperty("version").GetString());
        Assert.Equal("assets", me.GetProperty("plugins")[0].GetString());
        Assert.DoesNotContain("token", me.ToString().ToLowerInvariant().Replace("token_hash", ""));

        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync("/api/connect/agents/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(Beat(token))).StatusCode);   // revoked: refused at once
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/connect/agents/" + id)).StatusCode);

        var audit = JsonDocument.Parse(await admin.GetStringAsync("/api/platform/audit?action=connect.")).RootElement;
        Assert.Contains(audit.EnumerateArray(), r => r.GetProperty("action").GetString() == "connect.agent_created" && r.GetProperty("target").GetString() == id);
        Assert.Contains(audit.EnumerateArray(), r => r.GetProperty("action").GetString() == "connect.agent_revoked");
    }

    [PgFact]
    public async Task IntervalComesFromSettings()
    {
        var admin = await TestSupport.AdminClient(factory);
        var created = JsonDocument.Parse(await (await admin.PostAsync("/api/connect/agents", Json("{\"name\":\"a\"}"))).Content.ReadAsStringAsync()).RootElement;
        await admin.PutAsync("/api/platform/settings/connect.interval", Json("{\"value\":90}"));
        var hb = await factory.CreateClient().SendAsync(Beat(created.GetProperty("token").GetString()!));
        Assert.Equal(90, JsonDocument.Parse(await hb.Content.ReadAsStringAsync()).RootElement.GetProperty("intervalSeconds").GetInt32());
        await admin.DeleteAsync("/api/platform/settings/connect.interval");
    }

    [PgFact]
    public async Task BadAgentNameIsRefused()
    {
        var admin = await TestSupport.AdminClient(factory);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/connect/agents", Json("{\"name\":\"\"}"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/connect/agents", Json("not json"))).StatusCode);
    }
}
