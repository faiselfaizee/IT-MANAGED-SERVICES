using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using DeskAsk.Platform;
using DeskAsk.Platform.Assets;
using DeskAsk.Platform.Licensing;
using Npgsql;
using Xunit;

public class AssetsTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public AssetsTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    async Task<HttpClient> Admin()
    {
        var api = await TestSupport.AdminClient(factory);
        await api.GetAsync("/api/assets/types");   // makes sure the schema is there
        await Sql("truncate assets.history, assets.assets, assets.types restart identity cascade; alter sequence assets.tag_seq restart");
        return api;
    }
    static async Task Sql(string sql)
    {
        await using var db = new NpgsqlConnection(Pg); await db.OpenAsync();
        await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync();
    }
    static async Task<int> NewType(HttpClient api, string name = "Laptop", object? fields = null)
    {
        var r = await api.PostAsync("/api/assets/types", Json(new { name, fields = fields ?? new object[0] }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await J(r)).GetProperty("id").GetInt32();
    }
    static async Task<(HttpStatusCode Code, JsonElement Body)> Post(HttpClient api, object o) { var r = await api.PostAsync("/api/assets", Json(o)); return (r.StatusCode, await J(r)); }

    [PgFact]
    public async Task NeedsSignInAndAModuleLicence()
    {
        var anon = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/assets")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/assets", Json(new { name = "x" }))).StatusCode);

        var unlicensed = factory.WithWebHostBuilder(b => b.UseSetting("Licensing:AllowUnlicensed", "false").UseSetting("Licensing:CacheSeconds", "0"));
        var admin = await TestSupport.AdminClient(unlicensed);
        await admin.DeleteAsync("/api/platform/settings/" + LicenseResolver.SettingKey);
        var r = await admin.GetAsync("/api/assets");
        Assert.Equal(HttpStatusCode.PaymentRequired, r.StatusCode);
        Assert.Equal("module_not_licensed", (await J(r)).GetProperty("error").GetString());
    }

    [PgFact]
    public async Task TypesWithExtraFields()
    {
        var api = await Admin();
        var id = await NewType(api, "Laptop", new object[]
        {
            new { key = "ram_gb", label = "RAM (GB)", kind = "number" },
            new { key = "os", label = "Operating system", kind = "choice", options = new[] { "Windows", "Linux" } },
            new { key = "encrypted", kind = "bool" },
        });
        Assert.Equal(HttpStatusCode.Conflict, (await api.PostAsync("/api/assets/types", Json(new { name = "laptop" }))).StatusCode);   // names are unique, any case
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/types", Json(new { name = "X", fields = new[] { new { key = "a", kind = "weird" } } }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/types", Json(new { name = "X", fields = new[] { new { key = "Bad Key", kind = "text" } } }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/types", Json(new { name = "X", fields = new[] { new { key = "c", kind = "choice" } } }))).StatusCode);   // choice needs options
        var list = await J(await api.GetAsync("/api/assets/types"));
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal(3, list[0].GetProperty("fields").GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync("/api/assets/types/" + id, Json(new { name = "Notebook" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.PutAsync("/api/assets/types/9999", Json(new { name = "Nope" }))).StatusCode);
    }

    [PgFact]
    public async Task CreateReadSearchAndPage()
    {
        var api = await Admin();
        var t = await NewType(api, "Laptop", new object[] { new { key = "ram_gb", kind = "number" }, new { key = "os", kind = "choice", options = new[] { "Windows", "Linux" } } });
        var a = await Post(api, new { name = "Dell Latitude 5440", typeId = t, serial = "SN-100", manufacturer = "Dell", model = "Latitude 5440", assignedTo = "asha@acme.test", location = "HQ 2nd floor",
            purchaseDate = "2026-01-15", purchaseCost = 1200.5, warrantyEnd = "2029-01-15", custom = new { ram_gb = 16, os = "Windows" } });
        Assert.Equal(HttpStatusCode.OK, a.Code);
        Assert.Equal("AST-000001", a.Body.GetProperty("tag").GetString());
        var b = await Post(api, new { name = "HP ProBook", typeId = t, serial = "SN-200", status = "in_use" });
        var c = await Post(api, new { name = "Spare monitor", serial = "SN-300", status = "retired" });
        Assert.Equal("AST-000003", c.Body.GetProperty("tag").GetString());

        var one = await J(await api.GetAsync("/api/assets/" + a.Body.GetProperty("id").GetInt64()));
        var asset = one.GetProperty("asset");
        Assert.Equal("Laptop", asset.GetProperty("type").GetString());
        Assert.Equal("2026-01-15", asset.GetProperty("purchaseDate").GetString());
        Assert.Equal(1200.5m, asset.GetProperty("purchaseCost").GetDecimal());
        Assert.Equal(16, asset.GetProperty("custom").GetProperty("ram_gb").GetInt32());
        Assert.Equal("created", one.GetProperty("history")[0].GetProperty("action").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync("/api/assets/9999")).StatusCode);

        async Task<JsonElement> List(string q) => await J(await api.GetAsync("/api/assets" + q));
        Assert.Equal(3, (await List("")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await List("?search=latitude")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await List("?search=sn-200")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await List("?search=ASHA@")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await List("?search=%25")).GetProperty("total").GetInt32());   // '%' is searched for, not a wildcard
        Assert.Equal(1, (await List("?status=in_use")).GetProperty("total").GetInt32());
        Assert.Equal(2, (await List("?typeId=" + t)).GetProperty("total").GetInt32());
        Assert.Equal(1, (await List("?assignedTo=ASHA@acme.test")).GetProperty("total").GetInt32());
        var page = await List("?take=2&skip=2");
        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal(1, page.GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.GetAsync("/api/assets?status=bogus")).StatusCode);
    }

    [PgFact]
    public async Task BadInputIsRefused()
    {
        var api = await Admin();
        var t = await NewType(api, "Laptop", new object[] { new { key = "ram_gb", kind = "number" }, new { key = "os", kind = "choice", options = new[] { "Windows", "Linux" } } });
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(api, new { serial = "x" })).Code);   // no name
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(api, new { name = "a", status = "broken" })).Code);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(api, new { name = "a", purchaseDate = "09/11/2026" })).Code);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(api, new { name = "a", purchaseCost = -5 })).Code);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(api, new { name = "a", typeId = 999 })).Code);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(api, new { name = "a", typeId = t, custom = new { nope = 1 } })).Code);          // not a field of this type
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(api, new { name = "a", typeId = t, custom = new { ram_gb = "lots" } })).Code);   // wrong kind
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(api, new { name = "a", typeId = t, custom = new { os = "Amiga" } })).Code);      // not an option
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(api, new { name = new string('x', 201) })).Code);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(api, new { name = "a", tag = "has space" })).Code);
        Assert.Equal(HttpStatusCode.OK, (await Post(api, new { name = "ok", serial = "S-1", tag = "OFFICE-1" })).Code);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(api, new { name = "twin", serial = "s-1" })).Code);        // serial is unique, any case
        Assert.Equal(HttpStatusCode.Conflict, (await Post(api, new { name = "twin", tag = "OFFICE-1" })).Code);      // tag is unique
        Assert.Equal(1, (await J(await api.GetAsync("/api/assets"))).GetProperty("total").GetInt32());   // nothing half-made
    }

    [PgFact]
    public async Task ChangesAreKeptInTheHistory()
    {
        var api = await Admin();
        var id = (await Post(api, new { name = "Laptop 1", serial = "H-1", purchaseCost = 1000 })).Body.GetProperty("id").GetInt64();
        async Task<JsonElement> Put(object o) => await J(await api.PutAsync("/api/assets/" + id, Json(o)));
        Assert.Equal(2, (await Put(new { status = "in_use", assignedTo = "ravi@acme.test" })).GetProperty("changed").GetInt32());
        Assert.Equal(1, (await Put(new { status = "repair" })).GetProperty("changed").GetInt32());
        Assert.Equal(0, (await Put(new { status = "repair", assignedTo = "ravi@acme.test" })).GetProperty("changed").GetInt32());   // same values: nothing happens
        Assert.Equal(1, (await Put(new { purchaseCost = 1200.5 })).GetProperty("changed").GetInt32());
        Assert.Equal(0, (await Put(new { purchaseCost = 1200.5 })).GetProperty("changed").GetInt32());   // 1200.50 stored, 1200.5 sent: the same
        Assert.Equal(HttpStatusCode.NotFound, (await api.PutAsync("/api/assets/9999", Json(new { name = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsync("/api/assets/" + id, Json(new { status = "nonsense" }))).StatusCode);

        var h = (await J(await api.GetAsync("/api/assets/" + id))).GetProperty("history");
        Assert.Equal(new[] { "updated", "status", "updated", "created" }, h.EnumerateArray().Select(x => x.GetProperty("action").GetString()).ToArray());
        var first = h[2].GetProperty("changes");   // the first change
        Assert.Equal("stock", first.GetProperty("status").GetProperty("from").GetString());
        Assert.Equal("in_use", first.GetProperty("status").GetProperty("to").GetString());
        Assert.Equal("admin@acme.test", h[0].GetProperty("actor").GetString());
    }

    [PgFact]
    public async Task TypeInUseCannotBeDeleted_AssetsCanBe()
    {
        var api = await Admin();
        var t = await NewType(api);
        var id = (await Post(api, new { name = "a", typeId = t })).Body.GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.Conflict, (await api.DeleteAsync("/api/assets/types/" + t)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.DeleteAsync("/api/assets/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.DeleteAsync("/api/assets/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.DeleteAsync("/api/assets/types/" + t)).StatusCode);
    }

    [PgFact]
    public async Task CsvImportAndExport()
    {
        var api = await Admin();
        await NewType(api, "Laptop");
        var csv = "name,type,status,serial,assigned_to,purchase_cost,notes\r\n"
                + "Dell 1,Laptop,in_use,C-1,asha@acme.test,950.25,\"has a crack, left corner\"\r\n"
                + "\"=HYPERLINK(\"\"http://evil\"\")\",laptop,stock,C-2,,,\"line one\nline two\"\r\n"
                + "Mystery,Gadget,stock,C-3,,,\r\n"
                + "Copy of Dell,Laptop,stock,c-1,,,\r\n"
                + "Bad cost,Laptop,stock,C-4,,abc,\r\n";
        var res = await J(await api.PostAsync("/api/assets/import", new StringContent(csv, Encoding.UTF8, "text/csv")));
        Assert.Equal(2, res.GetProperty("created").GetInt32());
        Assert.Equal(1, res.GetProperty("skipped").GetInt32());
        var errs = res.GetProperty("errors").EnumerateArray().ToDictionary(e => e.GetProperty("line").GetInt32(), e => e.GetProperty("error").GetString());
        Assert.Equal("unknown_type", errs[4]);
        Assert.Equal("duplicate", errs[5]);
        Assert.Equal("bad_input", errs[6]);

        var again = await J(await api.PostAsync("/api/assets/import?createTypes=true", new StringContent("name,type\r\nNew thing,Gadget\r\n", Encoding.UTF8, "text/csv")));
        Assert.Equal(1, again.GetProperty("created").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/import", new StringContent("serial\r\nx\r\n", Encoding.UTF8, "text/csv"))).StatusCode);   // no name column

        var export = await (await api.GetAsync("/api/assets/export.csv")).Content.ReadAsStringAsync();
        Assert.Contains("\"has a crack, left corner\"", export);
        Assert.Contains("'=HYPERLINK", export);   // a spreadsheet must not run it as a formula
        Assert.DoesNotContain(",=HYPERLINK", export);
        var back = Csv.Parse(export);
        Assert.Equal(1 + 3, back.Count);
        Assert.Equal("line one\nline two", back[2][12]);
        Assert.Equal("import", await Scalar("select source from assets.assets where serial = 'C-1'"));
    }

    static async Task<string> Scalar(string sql)
    {
        await using var db = new NpgsqlConnection(Pg); await db.OpenAsync();
        await using var c = new NpgsqlCommand(sql, db); return (string)(await c.ExecuteScalarAsync())!;
    }

    [PgFact]
    public async Task SummaryCountsStatusTypeAndWarranty()
    {
        var api = await Admin();
        var t = await NewType(api);
        var soon = DateTime.UtcNow.Date.AddDays(10).ToString("yyyy-MM-dd"); var later = DateTime.UtcNow.Date.AddDays(60).ToString("yyyy-MM-dd"); var gone = DateTime.UtcNow.Date.AddDays(-5).ToString("yyyy-MM-dd");
        await Post(api, new { name = "a", typeId = t, warrantyEnd = soon });
        await Post(api, new { name = "b", typeId = t, warrantyEnd = later, status = "in_use" });
        await Post(api, new { name = "c", warrantyEnd = gone });
        await Post(api, new { name = "d", warrantyEnd = soon, status = "retired" });   // retired: not counted for warranty
        var s = await J(await api.GetAsync("/api/assets/summary"));
        Assert.Equal(4, s.GetProperty("total").GetInt64());
        Assert.Equal(2, s.GetProperty("byStatus").GetProperty("stock").GetInt64());
        Assert.Equal(1, s.GetProperty("byStatus").GetProperty("retired").GetInt64());
        Assert.Equal(1, s.GetProperty("warrantyEndsIn30Days").GetInt64());
        Assert.Equal(2, s.GetProperty("warrantyEndsIn90Days").GetInt64());
        Assert.Equal(1, s.GetProperty("warrantyExpired").GetInt64());
        Assert.Contains(s.GetProperty("byType").EnumerateArray(), x => x.GetProperty("type").GetString() == "Laptop" && x.GetProperty("count").GetInt64() == 2);
    }

    [PgFact]
    public async Task TheLicenceLimitCountsActiveAssets()
    {
        var vendor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var f = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Licensing:AllowUnlicensed", "false"); b.UseSetting("Licensing:CacheSeconds", "0");
            b.UseSetting("Licensing:VendorPublicKeyPem", vendor.ExportSubjectPublicKeyInfoPem());
        });
        var api = await TestSupport.AdminClient(f);
        await api.GetAsync("/api/platform/settings");
        await Sql("truncate assets.history, assets.assets, assets.types restart identity cascade");
        var now = DateTimeOffset.UtcNow;
        var token = LicenseCodec.Issue(vendor, new LicensePayload(1, "dev", "starter", now.ToUnixTimeSeconds(), now.AddDays(-1).ToUnixTimeSeconds(), now.AddDays(30).ToUnixTimeSeconds(), 14,
            new() { ["assets"] = new ModuleGrant(new() { ["max_assets"] = 2 }) }));
        try
        {
            await api.DeleteAsync("/api/platform/settings/" + LicenseResolver.SettingKey);
            Assert.Equal(HttpStatusCode.OK, (await api.PutAsync("/api/platform/license", Json(new { token }))).StatusCode);
            var a = (await Post(api, new { name = "one" })).Body.GetProperty("id").GetInt64();
            Assert.Equal(HttpStatusCode.OK, (await Post(api, new { name = "two" })).Code);
            var third = await Post(api, new { name = "three" });
            Assert.Equal(HttpStatusCode.PaymentRequired, third.Code);
            Assert.Equal("limit_reached", third.Body.GetProperty("error").GetString());
            Assert.Equal(HttpStatusCode.OK, (await api.PutAsync("/api/assets/" + a, Json(new { status = "retired" }))).StatusCode);   // retired ones do not count
            var c = await Post(api, new { name = "three" });
            Assert.Equal(HttpStatusCode.OK, c.Code);
            Assert.Equal(HttpStatusCode.PaymentRequired, (await api.PutAsync("/api/assets/" + a, Json(new { status = "in_use" }))).StatusCode);   // bringing it back would pass the limit
            var imp = await J(await api.PostAsync("/api/assets/import", new StringContent("name\r\nx\r\n", Encoding.UTF8, "text/csv")));
            Assert.Equal(0, imp.GetProperty("created").GetInt32());
            Assert.Equal("limit_reached", imp.GetProperty("errors")[0].GetProperty("error").GetString());
        }
        finally { await api.DeleteAsync("/api/platform/settings/" + LicenseResolver.SettingKey); }
    }
}
