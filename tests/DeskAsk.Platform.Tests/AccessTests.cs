using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

public class AccessTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    const string Pw = "Long-Enough-1";
    const string Base = "/api/platform/access";
    readonly WebApplicationFactory<Program> factory;
    public AccessTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }

    sealed record Ctx(HttpClient Admin, string ViewId, string CadmId, string CustId, int CoA, int CoB, long AssetA, long AssetB);

    async Task<Ctx> Start()
    {
        var admin = await TestSupport.AdminClient(factory);
        await Sql("truncate access.groups, access.grants, access.user_companies cascade");
        await Sql("delete from identity.users where \"Email\" like '%@dg.test'");
        await Sql("delete from assets.assets where name like 'DG %'");
        async Task<int> Company(string name)
        {
            var r = await admin.PostAsync("/api/assets/companies", Json(new { name }));
            if (r.StatusCode != HttpStatusCode.OK && r.StatusCode != HttpStatusCode.Created)
                { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand("select id from assets.companies where lower(name) = lower(@n)", db); c.Parameters.AddWithValue("n", name); return (int)(await c.ExecuteScalarAsync())!; }
            return (await J(r)).GetProperty("id").GetInt32();
        }
        var a = await Company("DG Alpha"); var b = await Company("DG Beta");
        async Task<string> User(string email, string role)
        {
            var r = await admin.PostAsync("/api/platform/users", Json(new { email, name = email.Split('@')[0], role, password = Pw }));
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            return (await J(r)).GetProperty("id").GetString()!;
        }
        var view = await User("view@dg.test", "viewer"); var cadm = await User("cadm@dg.test", "cadmin"); var cust = await User("cust@dg.test", "customer");
        async Task<long> Asset(string name, int company)
        {
            var id = (await J(await admin.PostAsync("/api/assets", Json(new { name })))).GetProperty("id").GetInt64();
            await Sql($"update assets.assets set company_id = {company} where id = {id}");
            return id;
        }
        return new Ctx(admin, view, cadm, cust, a, b, await Asset("DG Laptop A", a), await Asset("DG Laptop B", b));
    }

    static Task<HttpResponseMessage> Grant(HttpClient c, string kind, string pid, string module, string action, int? company = null) =>
        c.PostAsync(Base + "/grants", Json(new { kind, principalId = pid, module, action, companyId = company }));
    static async Task<string> Group(HttpClient c, string name) { var r = await c.PostAsync(Base + "/groups", Json(new { name })); Assert.Equal(HttpStatusCode.Created, r.StatusCode); return (await J(r)).GetProperty("id").GetString()!; }
    static Task<HttpResponseMessage> Member(HttpClient c, string group, string kind, string id) => c.PostAsync(Base + "/groups/" + group + "/members", Json(new { kind, id }));

    [PgFact]
    public async Task OnlyAdministratorsManageAccess()
    {
        var x = await Start();
        var anon = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(Base + "/groups")).StatusCode);
        var viewer = await TestSupport.ClientFor(factory, "view@dg.test", Pw);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync(Base + "/groups")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Grant(viewer, "user", x.ViewId, "assets", "add")).StatusCode);   // nobody gives themselves rights
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(Base + "/mine")).StatusCode);                         // but anyone may ask what they can do
    }

    [PgFact]
    public async Task GroupsHoldUsersAndGroupsButNeverLoop()
    {
        var x = await Start();
        var a = await Group(x.Admin, "Asset editors"); var b = await Group(x.Admin, "Helpdesk");
        Assert.Equal(HttpStatusCode.Conflict, (await x.Admin.PostAsync(Base + "/groups", Json(new { name = "asset EDITORS" }))).StatusCode);   // names are unique, ignoring case
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Admin.PostAsync(Base + "/groups", Json(new { name = "" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Member(x.Admin, a, "user", x.ViewId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Member(x.Admin, a, "group", b)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Member(x.Admin, b, "group", a)).StatusCode);        // would loop
        Assert.Equal(HttpStatusCode.BadRequest, (await Member(x.Admin, a, "group", a)).StatusCode);        // itself
        Assert.Equal(HttpStatusCode.BadRequest, (await Member(x.Admin, a, "user", Guid.NewGuid().ToString())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Member(x.Admin, Guid.NewGuid().ToString(), "user", x.ViewId)).StatusCode);
        var list = await J(await x.Admin.GetAsync(Base + "/groups"));
        var editors = list.EnumerateArray().Single(g => g.GetProperty("name").GetString() == "Asset editors");
        Assert.Equal(2, editors.GetProperty("members").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await x.Admin.DeleteAsync($"{Base}/groups/{a}/members/user/{x.ViewId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await x.Admin.PutAsync($"{Base}/groups/{a}", Json(new { name = "Asset team", description = "Can edit" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await x.Admin.DeleteAsync($"{Base}/groups/{b}")).StatusCode);       // also leaves group a
        var after = (await J(await x.Admin.GetAsync(Base + "/groups"))).EnumerateArray().Single();
        Assert.Equal(0, after.GetProperty("members").GetArrayLength());
    }

    [PgFact]
    public async Task GrantsAreCheckedAndNotRepeated()
    {
        var x = await Start();
        var g = await Group(x.Admin, "Editors");
        Assert.Equal(HttpStatusCode.Created, (await Grant(x.Admin, "group", g, "assets", "modify")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Grant(x.Admin, "group", g, "assets", "modify")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Grant(x.Admin, "group", g, "assets", "modify", x.CoA)).StatusCode);     // the same right for one company is separate
        Assert.Equal(HttpStatusCode.BadRequest, (await Grant(x.Admin, "group", g, "vault", "read")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Grant(x.Admin, "group", g, "assets", "own")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Grant(x.Admin, "group", Guid.NewGuid().ToString(), "assets", "read")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Grant(x.Admin, "user", Guid.NewGuid().ToString(), "assets", "read")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Grant(x.Admin, "group", g, "assets", "read", 987654)).StatusCode);
        var rows = await J(await x.Admin.GetAsync(Base + "/grants"));
        Assert.Equal(2, rows.GetArrayLength());
        var id = rows[0].GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await x.Admin.DeleteAsync($"{Base}/grants/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await x.Admin.DeleteAsync($"{Base}/grants/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await x.Admin.DeleteAsync($"{Base}/groups/{g}")).StatusCode);
        Assert.Equal(0, (await J(await x.Admin.GetAsync(Base + "/grants"))).GetArrayLength());     // a deleted group takes its grants with it
    }

    [PgFact]
    public async Task DelegationDecidesWhoAddsModifiesAndDeletesAssets()
    {
        var x = await Start();
        var viewer = await TestSupport.ClientFor(factory, "view@dg.test", Pw);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/assets", Json(new { name = "DG New" }))).StatusCode);

        // a grant to the person
        var added = await Grant(x.Admin, "user", x.ViewId, "assets", "add"); Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.PostAsync("/api/assets", Json(new { name = "DG New" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsync("/api/assets/" + x.AssetA, Json(new { notes = "x" }))).StatusCode);   // adding does not allow changing
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync("/api/assets/" + x.AssetA)).StatusCode);
        await x.Admin.DeleteAsync($"{Base}/grants/{(await J(added)).GetProperty("id").GetInt64()}");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/assets", Json(new { name = "DG New 2" }))).StatusCode);      // revoked

        // a grant to a group that holds a group that holds the person
        var outer = await Group(x.Admin, "Outer"); var inner = await Group(x.Admin, "Inner");
        await Member(x.Admin, outer, "group", inner); await Member(x.Admin, inner, "user", x.ViewId);
        await Grant(x.Admin, "group", outer, "assets", "modify");
        Assert.Equal(HttpStatusCode.OK, (await viewer.PutAsync("/api/assets/" + x.AssetA, Json(new { notes = "changed by delegate" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync("/api/assets/" + x.AssetA)).StatusCode);                         // delete is its own right
        await Grant(x.Admin, "group", inner, "assets", "delete");
        Assert.Equal(HttpStatusCode.OK, (await viewer.DeleteAsync("/api/assets/" + x.AssetB)).StatusCode);

        var mine = await J(await viewer.GetAsync(Base + "/mine"));
        Assert.Equal("all", mine.GetProperty("assets").GetProperty("modify").GetString());
        Assert.Equal("none", mine.GetProperty("assets").GetProperty("add").GetString());
    }

    [PgFact]
    public async Task CustomerAdministratorsSeeOnlyTheirCompanyAndReadOnlyByDefault()
    {
        var x = await Start();
        var cadm = await TestSupport.ClientFor(factory, "cadm@dg.test", Pw);
        Assert.Equal(HttpStatusCode.Forbidden, (await cadm.GetAsync("/api/assets")).StatusCode);               // not tied to a company yet
        Assert.Equal(HttpStatusCode.OK, (await x.Admin.PutAsync($"{Base}/user-companies/{x.CadmId}", Json(new { companyId = x.CoA }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await x.Admin.PutAsync($"{Base}/user-companies/{x.CadmId}", Json(new { companyId = 987654 }))).StatusCode);

        var list = await J(await cadm.GetAsync("/api/assets"));
        Assert.Equal(1, list.GetProperty("total").GetInt64());
        Assert.Equal("DG Laptop A", list.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.OK, (await cadm.GetAsync("/api/assets/" + x.AssetA)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cadm.GetAsync("/api/assets/" + x.AssetB)).StatusCode);   // another customer's asset
        Assert.Equal(HttpStatusCode.NotFound, (await cadm.GetAsync($"/api/assets/{x.AssetB}/device")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cadm.GetAsync($"/api/assets/{x.AssetB}/applications")).StatusCode);
        var companies = await J(await cadm.GetAsync("/api/assets/companies"));
        Assert.Equal(1, companies.GetArrayLength()); Assert.Equal("DG Alpha", companies[0].GetProperty("name").GetString());
        Assert.Equal(1, (await J(await cadm.GetAsync("/api/assets/summary"))).GetProperty("total").GetInt64());
        var csv = await (await cadm.GetAsync("/api/assets/export.csv")).Content.ReadAsStringAsync();
        Assert.Contains("DG Laptop A", csv); Assert.DoesNotContain("DG Laptop B", csv);
        Assert.Equal(HttpStatusCode.Forbidden, (await cadm.GetAsync("/api/assets/software")).StatusCode);      // lists across every company

        // read only unless someone is granted more; a licensed grant is limited to their own company
        Assert.Equal(HttpStatusCode.Forbidden, (await cadm.PutAsync("/api/assets/" + x.AssetA, Json(new { notes = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Grant(x.Admin, "user", x.CadmId, "assets", "modify", x.CoA)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cadm.PutAsync("/api/assets/" + x.AssetA, Json(new { notes = "updated by the customer" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cadm.PutAsync("/api/assets/" + x.AssetB, Json(new { notes = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cadm.DeleteAsync("/api/assets/" + x.AssetA)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Grant(x.Admin, "user", x.CadmId, "assets", "add", x.CoA)).StatusCode);
        var made = await J(await cadm.PostAsync("/api/assets", Json(new { name = "DG Customer Added" })));
        var list2 = await J(await cadm.GetAsync("/api/assets?search=Customer%20Added"));
        Assert.Equal(1, list2.GetProperty("total").GetInt64());                                                // it landed in their company
        Assert.Equal(HttpStatusCode.Forbidden, (await cadm.PostAsync("/api/assets/import", new StringContent("name\r\nx\r\n", Encoding.UTF8, "text/csv"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await x.Admin.PutAsync($"{Base}/user-companies/{x.CadmId}", Json(new { companyId = (int?)null }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cadm.GetAsync("/api/assets")).StatusCode);              // tie removed
    }

    [PgFact]
    public async Task ACustomerEmployeeCanBeGivenReadingOnlyOnPurpose()
    {
        var x = await Start();
        var cust = await TestSupport.ClientFor(factory, "cust@dg.test", Pw);
        Assert.Equal(HttpStatusCode.Forbidden, (await cust.GetAsync("/api/assets")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Grant(x.Admin, "user", x.CustId, "assets", "read", x.CoB)).StatusCode);
        var list = await J(await cust.GetAsync("/api/assets"));
        Assert.Equal(1, list.GetProperty("total").GetInt64());
        Assert.Equal("DG Laptop B", list.GetProperty("items")[0].GetProperty("name").GetString());
    }
}
