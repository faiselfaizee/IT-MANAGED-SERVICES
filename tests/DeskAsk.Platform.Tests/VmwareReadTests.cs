using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

/// <summary>An ESXi host found by a scan: read as VMware through the controller with the customer's VMware login; hardware shows on the device and in the export.</summary>
public class VmwareReadTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public VmwareReadTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }

    [PgFact]
    public async Task AnEsxiHostIsReadWithTheCustomersVmwareLoginAndItsHardwareIsShownAndExported()
    {
        var api = await TestSupport.AdminClient(factory);
        await api.GetAsync("/api/assets/types");
        await Sql("truncate assets.collectors, assets.creds, assets.shells, assets.rc_log, assets.jobs, assets.agent_q, assets.agents, assets.enroll_tokens, assets.amc, assets.rentals, assets.history, assets.assets, assets.companies, assets.types restart identity cascade");
        var co = (await J(await api.PostAsync("/api/assets/companies", Json(new { name = "Acme Ltd" })))).GetProperty("id").GetInt32();
        var other = (await J(await api.PostAsync("/api/assets/companies", Json(new { name = "Globex" })))).GetProperty("id").GetInt32();
        var tok = (await J(await api.PostAsync("/api/assets/collectors/tokens", Json(new { companyId = co, label = "HQ" })))).GetProperty("token").GetString()!;
        var anon = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var en = await J(await anon.PostAsync("/collector/v1/enroll", Json(new { token = tok, name = "HQ controller", hostname = "COL-1", platform = "windows", version = "2.10.0" })));
        var cid = en.GetProperty("collectorId").GetString()!;
        var col = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        col.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", en.GetProperty("key").GetString()); col.DefaultRequestHeaders.Add("X-Collector-Id", cid);

        // logins: Acme's VMware (filed under VMware), a shared VMware, Globex's VMware (must never reach Acme's controller) and Acme's Windows login
        async Task Cred(object o) => Assert.True((await api.PostAsync("/api/assets/discover/creds", Json(o))).IsSuccessStatusCode);
        await Cred(new { name = "shared vmware", kind = "vmware", category = "", username = "ro", secret = "s0", companyId = (int?)null });
        await Cred(new { name = "Acme ESXi", kind = "vmware", category = "virtualization", vendor = "VMware", username = "audit", secret = "p1", companyId = co });
        await Cred(new { name = "Globex ESXi", kind = "vmware", category = "virtualization", username = "g", secret = "p2", companyId = other });
        await Cred(new { name = "Acme domain", kind = "windows", category = "computer", username = "ACME\\adm", secret = "p3", companyId = co });

        // the scan found the host by IP; it is added as an asset
        await col.PostAsync("/collector/v1/checkin", Json(new { found = new object[] { new { ip = "192.168.31.20", hostname = "", open = new[] { 22, 443, 902 }, mac = "" } } }));
        Assert.Equal(1, (await J(await api.PostAsync($"/api/assets/collectors/{cid}/adopt", Json(new { ips = new[] { "192.168.31.20" } })))).GetProperty("added").GetInt32());
        var aid = (await J(await api.GetAsync("/api/assets"))).GetProperty("items").EnumerateArray().Single().GetProperty("id").GetInt64();

        // choose "VMware ESXi or vCenter": the controller is woken and reads it at once
        await col.GetAsync("/collector/v1/wait?wait=1");
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/assets/{aid}/monitor", Json(new { mode = "vmware", collectorId = cid }))).StatusCode);
        Assert.True((await J(await col.GetAsync("/collector/v1/wait?wait=5"))).GetProperty("now").GetBoolean());
        var dev = await J(await api.GetAsync($"/api/assets/{aid}/device"));
        Assert.Equal("vmware", dev.GetProperty("watch").GetProperty("mode").GetString()); Assert.True(dev.GetProperty("watch").GetProperty("readRequested").GetBoolean());

        var work = await J(await col.PostAsync("/collector/v1/checkin", Json(new { })));
        var a = work.GetProperty("assets").EnumerateArray().Single();
        Assert.Equal("vmware", a.GetProperty("monitor").GetString()); Assert.True(a.GetProperty("readNow").GetBoolean());
        var logins = a.GetProperty("logins").EnumerateArray().Select(l => l.GetProperty("name").GetString()).ToList();
        Assert.Equal(new[] { "Acme ESXi", "shared vmware" }, logins); // the customer's own first; no Windows login, nothing of Globex
        Assert.Equal("p1", a.GetProperty("logins")[0].GetProperty("secret").GetString());

        // the controller sends what it read (the shape the Go reader produces)
        var raw = new
        {
            schema = 1, hostname = "esx01.acme.local", os_family = "vmware", os_name = "VMware ESXi", os_version = "8.0.2 build 22380479", make = "Dell Inc.", model = "PowerEdge R740",
            serial_number = "7XK2LM3", bios_version = "2.19.1 2023-03-14", is_server = true, ram_bytes = 274877906944L, uptime_s = 1000,
            cpu = new { manufacturer = "Intel", model = "Intel(R) Xeon(R) Gold 6230 CPU @ 2.10GHz", count = 2, total_cores = 40, logical = 80, socket = "Intel(R) Xeon(R) Gold 6230 CPU @ 2.10GHz" },
            disks = new object[] { new { name = "Local DELL Disk", model = "DELL PERC H730P Mini", type = "SSD", size_bytes = 1999307276288L, partitions = Array.Empty<object>() } },
            nics = new object[] { new { name = "vmnic0", description = "ntg3", mac = "24:6e:96:aa:bb:01", connected = true, speed_mbps = 1000, ips = Array.Empty<string>() } },
            hardware_extra = new
            {
                platform = "vmware", api = "VMware ESXi 8.0.2 build-22380479",
                power_supplies = new object[] { new { name = "Power Supply 2 PS2 Status: Power Supply AC lost", state = "Critical", reading = "0" } },
                sensors = new object[] { new { name = "Power Supply 2 PS2 Status: Power Supply AC lost", type = "power", state = "Critical", reading = "0" } },
                gpus = new object[] { new { name = "Tesla T4", vendor = "NVIDIA Corporation", slot = "0000:03:00.0" } },
                datastores = new object[] { new { name = "datastore1", type = "VMFS", capacity_bytes = 1999307276288L, free_bytes = 823822794752L, accessible = true } },
                vms = new object[] { new { name = "SQL01", power = "On", host = "esx01.acme.local", guest_os = "Microsoft Windows Server 2022 (64-bit)", ip = "10.0.0.21", cpus = 8, memory_bytes = 34359738368L, disk_bytes = 107374182400L } },
            },
        };
        Assert.Equal(HttpStatusCode.OK, (await col.PostAsync("/collector/v1/checkin", Json(new { results = new { inventories = new object[] { new { asset = aid.ToString(), raw } } } }))).StatusCode);

        dev = await J(await api.GetAsync($"/api/assets/{aid}/device"));
        Assert.Equal("7XK2LM3", dev.GetProperty("identity").GetProperty("serial").GetString());
        Assert.Equal(2, dev.GetProperty("identity").GetProperty("cpus").GetInt64()); Assert.Equal(40, dev.GetProperty("identity").GetProperty("cores").GetInt64());
        Assert.Equal("VMware ESXi", dev.GetProperty("system").GetProperty("os").GetString());
        Assert.False(dev.GetProperty("watch").GetProperty("readRequested").GetBoolean()); // the read closed the request
        var hw = dev.GetProperty("hardware");
        Assert.Equal("Tesla T4", hw.GetProperty("gpus")[0].GetProperty("name").GetString());
        Assert.Equal("Critical", hw.GetProperty("power_supplies")[0].GetProperty("state").GetString());
        Assert.Equal("SQL01", hw.GetProperty("vms")[0].GetProperty("name").GetString());
        Assert.Equal(1000, dev.GetProperty("network")[0].GetProperty("speed_mbps").GetInt64());

        // the next read is only on request again
        Assert.False((await J(await col.PostAsync("/collector/v1/checkin", Json(new { })))).GetProperty("assets")[0].GetProperty("readNow").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/assets/{aid}/read-now", Json(new { }))).StatusCode);
        Assert.True((await J(await col.PostAsync("/collector/v1/checkin", Json(new { })))).GetProperty("assets")[0].GetProperty("readNow").GetBoolean());
        // a failed read is logged and closes the request
        await col.PostAsync("/collector/v1/checkin", Json(new { results = new { errors = new object[] { new { asset = aid.ToString(), error = "VMware login \"Acme ESXi\": the VMware login was refused" } } } }));
        Assert.False((await J(await api.GetAsync($"/api/assets/{aid}/device"))).GetProperty("watch").GetProperty("readRequested").GetBoolean());

        // the export and PDF carry the hardware tables
        var csv = await (await api.GetAsync($"/api/assets/{aid}/export.csv")).Content.ReadAsStringAsync();
        foreach (var s in new[] { ",Power supplies,", ",Graphics cards,", ",Datastores,", ",Virtual machines,", "Tesla T4", "SQL01", "Power Supply AC lost" }) Assert.Contains(s, csv);
        var pdf = Encoding.Latin1.GetString(await (await api.GetAsync($"/api/assets/{aid}/export.pdf")).Content.ReadAsByteArrayAsync());
        Assert.Contains("Virtual machines", pdf); Assert.Contains("Tesla T4", pdf);

        // a device not read in depth cannot be read now; an agent inventory keeps the hardware tables
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/assets/{aid}/monitor", Json(new { mode = "ping", collectorId = cid }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{aid}/read-now", Json(new { }))).StatusCode);
        Assert.Equal(0, (await J(await col.PostAsync("/collector/v1/checkin", Json(new { })))).GetProperty("assets")[0].GetProperty("logins").GetArrayLength());
    }
}
