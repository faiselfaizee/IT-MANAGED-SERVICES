using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

/// <summary>Accessories and personal devices: own serial numbers, attached to a main device, bulk add, what a person holds, stock.</summary>
public class PeripheralsTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public PeripheralsTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    async Task<HttpClient> Admin()
    {
        var api = await TestSupport.AdminClient(factory);
        await api.GetAsync("/api/assets/types");
        await using var db = new NpgsqlConnection(Pg); await db.OpenAsync();
        await using var c = new NpgsqlCommand("truncate assets.history, assets.assets, assets.types restart identity cascade; alter sequence assets.tag_seq restart", db); await c.ExecuteNonQueryAsync();
        return api;
    }
    static async Task<int> Type(HttpClient api, string name, bool peripheral)
    {
        var r = await api.PostAsync("/api/assets/types", Json(new { name, peripheral, fields = new object[0] }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode); return (await J(r)).GetProperty("id").GetInt32();
    }
    static async Task<long> Asset(HttpClient api, object body)
    {
        var r = await api.PostAsync("/api/assets", Json(body)); Assert.Equal(HttpStatusCode.OK, r.StatusCode); return (await J(r)).GetProperty("id").GetInt64();
    }

    [PgFact]
    public async Task AccessoryAttachesToItsDesktopAndIsFoundBySerial()
    {
        var api = await Admin(); var pc = await Type(api, "Desktop", false); var kb = await Type(api, "Keyboard", true);
        var desktop = await Asset(api, new { name = "PC-1", typeId = pc, serial = "DESK-111", assignedTo = "ann@x.test", purchaseDate = "2026-01-10", warrantyMonths = 36, invoiceNumber = "INV-9" });
        var key = await Asset(api, new { name = "Keyboard", typeId = kb, serial = "KB-222", parentId = desktop });

        var att = await J(await api.GetAsync($"/api/assets/{key}/attached"));
        Assert.Equal("DESK-111", att.GetProperty("parent").GetProperty("serial").GetString());
        Assert.True(att.GetProperty("peripheral").GetBoolean());

        // it took the purchase details and the holder from the desktop
        var one = await J(await api.GetAsync($"/api/assets/{key}")); var a = one.GetProperty("asset");
        Assert.Equal("ann@x.test", a.GetProperty("assignedTo").GetString());
        Assert.Equal("INV-9", a.GetProperty("invoiceNumber").GetString());

        // the desktop lists it, and searching the desktop's serial finds the keyboard too
        var kids = await J(await api.GetAsync($"/api/assets/{desktop}/attached"));
        Assert.Equal("KB-222", kids.GetProperty("items")[0].GetProperty("serial").GetString());
        var found = await J(await api.GetAsync("/api/assets?search=DESK-111"));
        Assert.Equal(2, found.GetProperty("total").GetInt32());

        // an accessory is not the "no agent" problem
        var none = await J(await api.GetAsync("/api/assets?agent=none"));
        Assert.Equal(1, none.GetProperty("total").GetInt32());
    }

    [PgFact]
    public async Task AttachRules()
    {
        var api = await Admin(); var pc = await Type(api, "Desktop", false); var kb = await Type(api, "Keyboard", true);
        var d1 = await Asset(api, new { name = "PC-1", typeId = pc, serial = "D1" }); var d2 = await Asset(api, new { name = "PC-2", typeId = pc, serial = "D2" });
        var k = await Asset(api, new { name = "KB", typeId = kb, serial = "K1", parentId = d1 });
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsync($"/api/assets/{d1}/attach", Json(new { parentId = d1 }))).StatusCode);       // not to itself
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsync($"/api/assets/{d2}/attach", Json(new { parentId = k }))).StatusCode);        // not to an attached item
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsync($"/api/assets/{d1}/attach", Json(new { parentId = d2 }))).StatusCode);       // d1 has an item attached
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync($"/api/assets/{k}/attach", Json(new { parentId = d2 }))).StatusCode);                  // moved
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync($"/api/assets/{k}/attach", Json(new { parentId = (long?)null }))).StatusCode);        // detached
        Assert.Equal(0, (await J(await api.GetAsync($"/api/assets/{d2}/attached"))).GetProperty("items").GetArrayLength());
    }

    [PgFact]
    public async Task BulkAddSkipsDuplicateSerials()
    {
        var api = await Admin(); var pc = await Type(api, "Desktop", false); var mon = await Type(api, "Monitor", true);
        var d = await Asset(api, new { name = "PC", typeId = pc, serial = "D1" });
        var r = await api.PostAsync("/api/assets/bulk-add", Json(new { typeId = mon, parentId = d, name = "Monitor", items = new[] { new { serial = "M1", name = "" }, new { serial = "M2", name = "" }, new { serial = "m1", name = "" } } }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode); var b = await J(r);
        Assert.Equal(2, b.GetProperty("created").GetArrayLength());
        Assert.Equal(1, b.GetProperty("skipped").GetArrayLength());
        Assert.Equal(2, (await J(await api.GetAsync($"/api/assets/{d}/attached"))).GetProperty("items").GetArrayLength());

        var q = await J(await api.PostAsync("/api/assets/bulk-add", Json(new { typeId = mon, quantity = 3 })));
        Assert.Equal(3, q.GetProperty("created").GetArrayLength());
    }

    [PgFact]
    public async Task PersonHoldsHandOverTakeBackAndStock()
    {
        var api = await Admin(); var pc = await Type(api, "Desktop", false); var kb = await Type(api, "Keyboard", true);
        var d = await Asset(api, new { name = "PC", typeId = pc, serial = "D1", status = "stock" });
        var k = await Asset(api, new { name = "KB", typeId = kb, serial = "K1", status = "stock", parentId = d });

        var stock = await J(await api.GetAsync("/api/assets/stock"));
        Assert.Contains(stock.GetProperty("items").EnumerateArray(), x => x.GetProperty("type").GetString() == "Keyboard" && x.GetProperty("stock").GetInt64() == 1);

        var give = await api.PostAsync("/api/assets/holder/handover", Json(new { ids = new[] { d }, mode = "assign", to = "bob@x.test", condition = "good", includeAttached = true }));
        Assert.Equal(HttpStatusCode.OK, give.StatusCode);
        var holder = await J(await api.GetAsync("/api/assets/holder?name=bob@x.test"));
        Assert.Equal(2, holder.GetProperty("items").GetArrayLength());                                   // the keyboard went with the desktop
        var people = await J(await api.GetAsync("/api/assets/holders"));
        Assert.Equal("bob@x.test", people.GetProperty("items")[0].GetProperty("name").GetString());

        var back = await api.PostAsync("/api/assets/holder/handover", Json(new { ids = new[] { d }, mode = "return", condition = "good", includeAttached = true }));
        Assert.Equal(HttpStatusCode.OK, back.StatusCode);
        Assert.Equal(0, (await J(await api.GetAsync("/api/assets/holder?name=bob@x.test"))).GetProperty("items").GetArrayLength());
        var one = (await J(await api.GetAsync($"/api/assets/{k}"))).GetProperty("asset");
        Assert.Equal("", one.GetProperty("assignedTo").GetString());
    }
}
