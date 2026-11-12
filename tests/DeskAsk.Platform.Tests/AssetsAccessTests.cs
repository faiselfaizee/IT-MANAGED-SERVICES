using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

public class AssetsAccessTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    const string Pw = "Long-Enough-1";
    readonly WebApplicationFactory<Program> factory;
    public AssetsAccessTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });
    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }

    [PgFact]
    public async Task CustomerEmployeesHaveNoAssetsAndCustomerAdminsOnlyRead()
    {
        var admin = await TestSupport.AdminClient(factory);
        await Sql("delete from identity.users where \"Email\" like '%@acc.test'");
        foreach (var (email, role) in new[] { ("cust@acc.test", "customer"), ("cadm@acc.test", "cadmin"), ("eng@acc.test", "engineer"), ("view@acc.test", "viewer") })
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/platform/users", Json(new { email, name = role, role, password = Pw }))).StatusCode);

        var urls = new[] { "/api/assets", "/api/assets/summary", "/api/assets/companies" };
        var customer = await TestSupport.ClientFor(factory, "cust@acc.test", Pw);
        foreach (var u in urls) Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync(u)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.PostAsync("/api/assets", Json(new { name = "x" }))).StatusCode);

        foreach (var who in new[] { "cadm@acc.test", "eng@acc.test", "view@acc.test" })
        {
            var c = await TestSupport.ClientFor(factory, who, Pw);
            foreach (var u in urls) Assert.Equal(HttpStatusCode.OK, (await c.GetAsync(u)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsync("/api/assets", Json(new { name = "x" }))).StatusCode);   // nobody but an administrator changes assets yet
        }
    }
}
