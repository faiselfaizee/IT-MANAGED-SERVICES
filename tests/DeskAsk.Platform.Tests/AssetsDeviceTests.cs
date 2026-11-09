using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using DeskAsk.Platform;
using Npgsql;
using Xunit;

public class AssetsDeviceTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public AssetsDeviceTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    async Task<HttpClient> Admin()
    {
        var api = await TestSupport.AdminClient(factory);
        await api.GetAsync("/api/assets/types");
        await using var db = new NpgsqlConnection(Pg); await db.OpenAsync();
        await using var c = new NpgsqlCommand("truncate assets.amc, assets.rentals, assets.history, assets.assets, assets.companies, assets.types restart identity cascade; alter sequence assets.tag_seq restart", db);
        await c.ExecuteNonQueryAsync();
        return api;
    }

    static object Inv(string host, string serial, params string[] apps) => new
    {
        schema = 1, hostname = host, os_family = "windows", os_name = "Windows 11 Pro", os_version = "10.0.26100", make = "Gigabyte Technology Co., Ltd.", model = "B760M GAMING WIFI6E",
        serial_number = serial, bios_version = "F9", ram_bytes = 34359738368L, domain_type = "workgroup", last_patch_date = "2026-10-20", uptime_s = 3600,
        cpu = new { manufacturer = "GenuineIntel", model = "Intel Core i5-13400", count = 1, total_cores = 10, logical = 16, socket = "LGA1700" },
        disks = new object[] { new { name = "\\\\.\\physicaldrive0", model = "MIPHI MP100", type = "SSD", size_bytes = 512110190592L, partitions = new object[] { new { name = "C:", size_bytes = 511000000000L, fs = "NTFS", free_bytes = 100000000000L } } } },
        nics = new object[]
        {
            new { name = "Ethernet", description = "Realtek PCIe 2.5GbE", mac = "30-56-0f-62-c2-47", connected = true, speed_mbps = 100, ips = new[] { "192.168.0.113", "fe80::1" } },
            new { name = "Wi-Fi", description = "Intel AX210", mac = "B8:F7:75:E4:84:19", connected = false, speed_mbps = 0, ips = new string[0] }
        },
        installed_apps = apps.Select(a => new { name = a.Split('|')[0], version = a.Split('|')[1], publisher = a.Split('|')[2] }).ToArray(),
        antivirus = new object[] { new { name = "Windows Defender", enabled = true, up_to_date = true } }
    };

    async Task<long> NewAsset(HttpClient api, string name)
    {
        var r = await api.PostAsync("/api/assets", Json(new { name }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await J(r)).GetProperty("id").GetInt64();
    }

    [PgFact]
    public async Task CompaniesAreManagedAndProtected()
    {
        var api = await Admin();
        var r = await api.PostAsync("/api/assets/companies", Json(new { name = "Acme Ltd", contactEmail = "it@acme.test", remoteAllowed = true }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var id = (await J(r)).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Conflict, (await api.PostAsync("/api/assets/companies", Json(new { name = "ACME LTD" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/companies", Json(new { name = "" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync("/api/assets/companies/" + id, Json(new { phone = "123" }))).StatusCode);

        var a = await NewAsset(api, "PC 1");
        Assert.Equal(HttpStatusCode.OK, (await api.PatchAsync("/api/assets/" + a + "/ownership", Json(new { companyId = id, category = "IT hardware" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PatchAsync("/api/assets/" + a + "/ownership", Json(new { companyId = 9999 }))).StatusCode);
        var list = await J(await api.GetAsync("/api/assets/companies"));
        Assert.Equal(1, list[0].GetProperty("assets").GetInt32());
        Assert.Equal("123", list[0].GetProperty("phone").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await api.DeleteAsync("/api/assets/companies/" + id)).StatusCode);
        await api.PatchAsync("/api/assets/" + a + "/ownership", Json(new { companyId = (int?)null }));
        Assert.Equal(HttpStatusCode.OK, (await api.DeleteAsync("/api/assets/companies/" + id)).StatusCode);
    }

    [PgFact]
    public async Task InventoryFillsTheDeviceTabs()
    {
        var api = await Admin();
        var a = await NewAsset(api, "Front desk PC");
        var r = await api.PostAsync("/api/assets/" + a + "/inventory", Json(Inv("DESKTOP-8IFK774", "SN-0001", "7-Zip|26.05|Igor Pavlov", "Google Chrome|156.0|Google LLC")));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var d = await J(await api.GetAsync("/api/assets/" + a + "/device"));
        Assert.Equal("DESKTOP-8IFK774", d.GetProperty("system").GetProperty("hostname").GetString());
        Assert.Equal("Intel", d.GetProperty("identity").GetProperty("cpuMake").GetString());
        Assert.Equal(16, d.GetProperty("identity").GetProperty("logical").GetInt32());
        Assert.Equal("Protected", d.GetProperty("system").GetProperty("antivirus").GetString());
        Assert.Equal("2026-10-20", d.GetProperty("system").GetProperty("lastPatch").GetString());
        Assert.Equal("192.168.0.113", d.GetProperty("system").GetProperty("ip").GetString());
        Assert.Equal(1, d.GetProperty("storage").GetArrayLength());
        Assert.Equal("NTFS", d.GetProperty("storage")[0].GetProperty("partitions")[0].GetProperty("fs").GetString());
        var nics = d.GetProperty("network");
        Assert.Equal(2, nics.GetArrayLength());
        Assert.Equal("30:56:0F:62:C2:47", nics[0].GetProperty("mac").GetString());
        Assert.Equal("SN-0001", d.GetProperty("identity").GetProperty("serial").GetString());
        Assert.Equal("IT hardware", d.GetProperty("category").GetString());

        var apps = await J(await api.GetAsync("/api/assets/" + a + "/applications?q=zip"));
        Assert.Equal(1, apps.GetProperty("count").GetInt32());
        Assert.Equal("26.05", apps.GetProperty("items")[0].GetProperty("version").GetString());
    }

    [PgFact]
    public async Task SerialOfAnotherAssetIsNotCopied()
    {
        var api = await Admin();
        var a = await NewAsset(api, "A"); var b = await NewAsset(api, "B");
        await api.PostAsync("/api/assets/" + a + "/inventory", Json(Inv("A", "SN-X", "x|1|y")));
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/assets/" + b + "/inventory", Json(Inv("B", "sn-x", "x|1|y")))).StatusCode);
        var d = await J(await api.GetAsync("/api/assets/" + b + "/device"));
        Assert.Equal("", d.GetProperty("identity").GetProperty("serial").GetString());
        Assert.Equal("B", d.GetProperty("system").GetProperty("hostname").GetString());
    }

    [PgFact]
    public async Task SoftwareInventoryAcrossComputers()
    {
        var api = await Admin();
        var a = await NewAsset(api, "A"); var b = await NewAsset(api, "B");
        await api.PostAsync("/api/assets/" + a + "/inventory", Json(Inv("A", "S1", "7-Zip|26.05|Igor Pavlov", "Chrome|156|Google")));
        await api.PostAsync("/api/assets/" + b + "/inventory", Json(Inv("B", "S2", "7-Zip|25.01|Igor Pavlov", "=cmd()|1|evil")));
        var list = await J(await api.GetAsync("/api/assets/software"));
        var first = list.GetProperty("items")[0];
        Assert.Equal("7-Zip", first.GetProperty("name").GetString());
        Assert.Equal(2, first.GetProperty("computers").GetInt32());
        Assert.Equal(2, first.GetProperty("versions").GetInt32());
        var where = await J(await api.GetAsync("/api/assets/software/computers?name=7-zip&version=25.01"));
        Assert.Equal(1, where.GetProperty("items").GetArrayLength());
        Assert.Equal("B", where.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.GetAsync("/api/assets/software/computers")).StatusCode);
        var csv = await (await api.GetAsync("/api/assets/software/export.csv")).Content.ReadAsStringAsync();
        Assert.StartsWith("Program,Publisher,Computers,Versions", csv);
        Assert.Contains("'=cmd()", csv);
        Assert.DoesNotContain("\n=cmd()", csv);
    }

    [PgFact]
    public async Task AmcAndRentalsMarkTheDevice()
    {
        var api = await Admin();
        var co = (await J(await api.PostAsync("/api/assets/companies", Json(new { name = "Acme" })))).GetProperty("id").GetInt32();
        var a = await NewAsset(api, "A");
        var next = DateTime.UtcNow.AddYears(1).ToString("yyyy-MM-dd"); var last = DateTime.UtcNow.AddYears(-1).ToString("yyyy-MM-dd");
        var r = await api.PostAsync("/api/assets/amc", Json(new { title = "Gold", companyId = co, start = last, end = next, visits = 12, slaPct = 95, assetIds = new[] { a } }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var amcId = (await J(r)).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/amc", Json(new { title = "Bad", assetIds = new[] { 999999 } }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/amc", Json(new { title = "Bad", start = next, end = last }))).StatusCode);
        var d = await J(await api.GetAsync("/api/assets/" + a + "/device"));
        Assert.True(d.GetProperty("ownership").GetProperty("underAmc").GetBoolean());
        Assert.Equal("Gold", d.GetProperty("ownership").GetProperty("amc").GetString());

        Assert.Equal(HttpStatusCode.OK, (await api.PutAsync("/api/assets/amc/" + amcId, Json(new { end = last }))).StatusCode);
        d = await J(await api.GetAsync("/api/assets/" + a + "/device"));
        Assert.False(d.GetProperty("ownership").GetProperty("underAmc").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync("/api/assets/rentals", Json(new { title = "Laptop rental", companyId = co, assetIds = new[] { a } }))).StatusCode);
        d = await J(await api.GetAsync("/api/assets/" + a + "/device"));
        Assert.True(d.GetProperty("ownership").GetProperty("rented").GetBoolean());
        var amcs = await J(await api.GetAsync("/api/assets/amc"));
        Assert.Equal(95, amcs[0].GetProperty("slaPct").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await api.DeleteAsync("/api/assets/amc/" + amcId)).StatusCode);
    }

    [PgFact]
    public async Task RemoteSwitchIsAuditedAndNeedsAdmin()
    {
        var api = await Admin();
        var a = await NewAsset(api, "A");
        var r = await api.PostAsync("/api/assets/" + a + "/remote", Json(new { on = false }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var d = await J(await api.GetAsync("/api/assets/" + a + "/device"));
        Assert.False(d.GetProperty("remote").GetProperty("assetAllows").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync("/api/assets/" + a + "/remote", Json(new { on = "yes" }))).StatusCode);
        var anon = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/assets/" + a + "/remote", Json(new { on = true }))).StatusCode);
    }
}
