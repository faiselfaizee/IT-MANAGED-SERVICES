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

    [PgFact]
    public async Task AServersManagementControllerIsReadBesideTheMainReadAndMergedIntoTheSameAsset()
    {
        var api = await TestSupport.AdminClient(factory);
        await api.GetAsync("/api/assets/types");
        await Sql("truncate assets.collectors, assets.creds, assets.shells, assets.rc_log, assets.jobs, assets.agent_q, assets.agents, assets.enroll_tokens, assets.amc, assets.rentals, assets.history, assets.assets, assets.companies, assets.types restart identity cascade");
        var co = (await J(await api.PostAsync("/api/assets/companies", Json(new { name = "Acme Ltd" })))).GetProperty("id").GetInt32();
        var tok = (await J(await api.PostAsync("/api/assets/collectors/tokens", Json(new { companyId = co, label = "HQ" })))).GetProperty("token").GetString()!;
        var anon = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var en = await J(await anon.PostAsync("/collector/v1/enroll", Json(new { token = tok, name = "HQ controller", hostname = "COL-1", platform = "windows", version = "2.11.0" })));
        var cid = en.GetProperty("collectorId").GetString()!;
        var col = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        col.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", en.GetProperty("key").GetString()); col.DefaultRequestHeaders.Add("X-Collector-Id", cid);
        async Task Cred(object o) => Assert.True((await api.PostAsync("/api/assets/discover/creds", Json(o))).IsSuccessStatusCode);
        await Cred(new { name = "Acme iDRAC (other)", kind = "bmc", category = "other", username = "root2", secret = "b0", companyId = co });
        await Cred(new { name = "Acme iDRAC", kind = "bmc", category = "server", vendor = "Dell iDRAC", username = "root", secret = "calvin", companyId = co });
        await Cred(new { name = "Acme ESXi", kind = "vmware", category = "virtualization", username = "audit", secret = "p1", companyId = co });

        await col.PostAsync("/collector/v1/checkin", Json(new { found = new object[] { new { ip = "192.168.31.21", hostname = "", open = new[] { 443, 902 }, mac = "" }, new { ip = "192.168.31.60", hostname = "", open = new[] { 443, 623 }, mac = "" } } }));
        await api.PostAsync($"/api/assets/collectors/{cid}/adopt", Json(new { ips = new[] { "192.168.31.21", "192.168.31.60" } }));
        var items = (await J(await api.GetAsync("/api/assets"))).GetProperty("items").EnumerateArray().ToList();
        var esx = items.Single(x => x.GetProperty("name").GetString()!.Contains("31.21")).GetProperty("id").GetInt64();
        var ilo = items.Single(x => x.GetProperty("name").GetString()!.Contains("31.60")).GetProperty("id").GetInt64();

        // a bad address and a missing controller are refused
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{esx}/monitor", Json(new { mode = "vmware", collectorId = cid, bmc = "not an address!" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{esx}/monitor", Json(new { mode = "", collectorId = "", bmc = "192.168.31.50" }))).StatusCode);

        // the ESXi host gets its iDRAC address; the other device is an iLO read on its own
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/assets/{esx}/monitor", Json(new { mode = "vmware", collectorId = cid, bmc = "192.168.31.50" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/assets/{ilo}/monitor", Json(new { mode = "bmc", collectorId = cid }))).StatusCode);
        var work = (await J(await col.PostAsync("/collector/v1/checkin", Json(new { })))).GetProperty("assets").EnumerateArray().ToList();
        var we = work.Single(x => x.GetProperty("id").GetString() == esx.ToString());
        Assert.Equal("192.168.31.50", we.GetProperty("bmc").GetString()); Assert.True(we.GetProperty("readNow").GetBoolean());
        Assert.Equal(new[] { "Acme ESXi" }, we.GetProperty("logins").EnumerateArray().Select(l => l.GetProperty("name").GetString()));
        Assert.Equal(new[] { "Acme iDRAC", "Acme iDRAC (other)" }, we.GetProperty("bmcLogins").EnumerateArray().Select(l => l.GetProperty("name").GetString())); // filed under Physical servers first
        var wi = work.Single(x => x.GetProperty("id").GetString() == ilo.ToString());
        Assert.Equal("bmc", wi.GetProperty("monitor").GetString()); Assert.Equal("Acme iDRAC", wi.GetProperty("logins")[0].GetProperty("name").GetString()); Assert.Equal("", wi.GetProperty("bmc").GetString());

        // the VMware read, then the iDRAC read for the same asset
        var vmware = new { schema = 1, hostname = "esx02.acme.local", make = "Dell Inc.", model = "PowerEdge R750", serial_number = "", cpu = new { model = "Xeon Gold 6338", count = 2, total_cores = 64 },
            hardware_extra = new { platform = "vmware", api = "VMware ESXi 8.0.2", vms = new object[] { new { name = "SQL01", power = "On" } }, power_supplies = new object[] { new { name = "PS from ESXi", state = "OK" } } } };
        var idrac = new { schema = 1, make = "Dell Inc.", model = "PowerEdge R750 (iDRAC)", serial_number = "CN7792", bios_version = "1.11.2", ram_bytes = 274877906944L, cpu = new { model = "Intel Xeon Gold 6338", count = 2, total_cores = 64, logical = 128 },
            hardware_extra = new { platform = "redfish", api = "15G Monolithic 7.00.00.171 (Redfish 1.17.0)", host_state = new { power = "On", overall = "Warning" },
                memory_modules = new object[] { new { slot = "DIMM.Socket.A2", size_bytes = 137438953472L, state = "OK" }, new { slot = "DIMM.Socket.A10", size_bytes = 137438953472L, state = "Critical" } },
                drives = new object[] { new { name = "SSD 0", model = "MZ7LH960", media = "SSD", size_bytes = 960197124096L, state = "OK" } },
                power_supplies = new object[] { new { name = "PS1 Status", model = "1400W", state = "OK", reading = "312 W out" }, new { name = "PS2 Status", state = "Critical", reading = "" } } } };
        Assert.Equal(HttpStatusCode.OK, (await col.PostAsync("/collector/v1/checkin", Json(new { results = new { inventories = new object[] { new { asset = esx.ToString(), raw = vmware } }, bmc = new object[] { new { asset = esx.ToString(), raw = idrac } } } }))).StatusCode);

        var dev = await J(await api.GetAsync($"/api/assets/{esx}/device"));
        Assert.Equal("PowerEdge R750", dev.GetProperty("identity").GetProperty("model").GetString()); // the main read wins where it has a value
        Assert.Equal("CN7792", dev.GetProperty("identity").GetProperty("serial").GetString());       // filled from the iDRAC where it was empty
        Assert.Equal("1.11.2", dev.GetProperty("identity").GetProperty("bios").GetString());
        Assert.Equal(274877906944L, dev.GetProperty("identity").GetProperty("ramBytes").GetInt64());
        var hw = dev.GetProperty("hardware");
        Assert.Equal("SQL01", hw.GetProperty("vms")[0].GetProperty("name").GetString());              // VMware tables kept
        Assert.Equal(2, hw.GetProperty("memory_modules").GetArrayLength());                            // iDRAC tables added
        Assert.Equal("PS2 Status", hw.GetProperty("power_supplies")[1].GetProperty("name").GetString()); // the iDRAC's power supplies replace ESXi's
        Assert.Equal("VMware ESXi 8.0.2", hw.GetProperty("api").GetString()); Assert.Contains("Redfish", hw.GetProperty("bmc_api").GetString());
        Assert.Equal("192.168.31.50", dev.GetProperty("watch").GetProperty("bmc").GetString());
        Assert.NotEqual(JsonValueKind.Null, dev.GetProperty("watch").GetProperty("bmcReadAt").ValueKind);

        // the next VMware read keeps the iDRAC tables; the export shows both
        await col.PostAsync("/collector/v1/checkin", Json(new { results = new { inventories = new object[] { new { asset = esx.ToString(), raw = vmware } } } }));
        Assert.Equal(2, (await J(await api.GetAsync($"/api/assets/{esx}/device"))).GetProperty("hardware").GetProperty("memory_modules").GetArrayLength());
        var csv = await (await api.GetAsync($"/api/assets/{esx}/export.csv")).Content.ReadAsStringAsync();
        foreach (var s in new[] { ",Memory modules,", ",Physical drives,", ",Virtual machines,", "DIMM.Socket.A10", "PS2 Status" }) Assert.Contains(s, csv);

        // a device read only through its management address can still be read now; clearing the address stops it
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/assets/{esx}/monitor", Json(new { mode = "ping", collectorId = cid, bmc = "192.168.31.50" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/assets/{esx}/read-now", Json(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/assets/{esx}/monitor", Json(new { mode = "ping", collectorId = cid, bmc = "" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync($"/api/assets/{esx}/read-now", Json(new { }))).StatusCode);
    }

    [PgFact]
    public async Task EachHypervisorGetsOnlyItsOwnLoginsAndAHyperVHostsAgentReportsItsMachines()
    {
        var api = await TestSupport.AdminClient(factory);
        await api.GetAsync("/api/assets/types");
        await Sql("truncate assets.collectors, assets.creds, assets.shells, assets.rc_log, assets.jobs, assets.agent_q, assets.agents, assets.enroll_tokens, assets.amc, assets.rentals, assets.history, assets.assets, assets.companies, assets.types restart identity cascade");
        var co = (await J(await api.PostAsync("/api/assets/companies", Json(new { name = "Acme Ltd" })))).GetProperty("id").GetInt32();
        var tok = (await J(await api.PostAsync("/api/assets/collectors/tokens", Json(new { companyId = co, label = "HQ" })))).GetProperty("token").GetString()!;
        var anon = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var en = await J(await anon.PostAsync("/collector/v1/enroll", Json(new { token = tok, name = "HQ", hostname = "COL-1", platform = "windows", version = "2.12.0" })));
        var cid = en.GetProperty("collectorId").GetString()!;
        var col = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        col.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", en.GetProperty("key").GetString()); col.DefaultRequestHeaders.Add("X-Collector-Id", cid);
        foreach (var (name, kind) in new[] { ("PVE token", "proxmox"), ("Prism", "nutanix"), ("XCP", "xen"), ("ESXi", "vmware") })
            Assert.True((await api.PostAsync("/api/assets/discover/creds", Json(new { name, kind, category = "virtualization", username = kind == "proxmox" ? "audit@pve!deskask" : "u", secret = "s", companyId = co }))).IsSuccessStatusCode);
        await col.PostAsync("/collector/v1/checkin", Json(new { found = new object[] { new { ip = "10.0.0.5", hostname = "pve01", open = new[] { 22, 8006 }, mac = "" }, new { ip = "10.0.0.6", hostname = "ntnx", open = new[] { 9440 }, mac = "" }, new { ip = "10.0.0.7", hostname = "xcp01", open = new[] { 443 }, mac = "" } } }));
        await api.PostAsync($"/api/assets/collectors/{cid}/adopt", Json(new { ips = new[] { "10.0.0.5", "10.0.0.6", "10.0.0.7" } }));
        var ids = (await J(await api.GetAsync("/api/assets"))).GetProperty("items").EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!, x => x.GetProperty("id").GetInt64());
        foreach (var (n, mode) in new[] { ("pve01", "proxmox"), ("ntnx", "nutanix"), ("xcp01", "xen") })
            Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/assets/{ids[n]}/monitor", Json(new { mode, collectorId = cid }))).StatusCode);
        var work = (await J(await col.PostAsync("/collector/v1/checkin", Json(new { })))).GetProperty("assets").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!, x => x);
        Assert.Equal(new[] { "PVE token" }, work[ids["pve01"].ToString()].GetProperty("logins").EnumerateArray().Select(l => l.GetProperty("name").GetString()));
        Assert.Equal(new[] { "Prism" }, work[ids["ntnx"].ToString()].GetProperty("logins").EnumerateArray().Select(l => l.GetProperty("name").GetString()));
        Assert.Equal(new[] { "XCP" }, work[ids["xcp01"].ToString()].GetProperty("logins").EnumerateArray().Select(l => l.GetProperty("name").GetString()));

        // a Proxmox read lands like any other: containers and VMs on the Hardware tab
        var pve = new { schema = 1, hostname = "pve01", os_name = "Proxmox VE", os_version = "8.2.4", ram_bytes = 137438953472L, hardware_extra = new { platform = "proxmox", api = "Proxmox VE 8.2.4",
            vms = new object[] { new { name = "pihole", kind = "Container", power = "On" }, new { name = "win2022", kind = "VM", power = "Off" } } } };
        await col.PostAsync("/collector/v1/checkin", Json(new { results = new { inventories = new object[] { new { asset = ids["pve01"].ToString(), raw = pve } } } }));
        var dev = await J(await api.GetAsync($"/api/assets/{ids["pve01"]}/device"));
        Assert.Equal("Container", dev.GetProperty("hardware").GetProperty("vms")[0].GetProperty("kind").GetString());
        Assert.Equal("Proxmox VE", dev.GetProperty("system").GetProperty("os").GetString());

        // an agent on a Hyper-V host reports its virtual machines with its normal inventory
        var et = (await J(await api.PostAsync("/api/assets/enrolments", Json(new { companyId = co })))).GetProperty("token").GetString()!;
        var agent = await J(await anon.PostAsync("/agent/v1/enroll", Json(new { token = et, platform = "windows", version = "2.12.0", inventory = new { schema = 1, hostname = "HV01", os_family = "windows", serial_number = "SN-HV0001",
            hardware_extra = new { platform = "hyperv", api = "Microsoft Hyper-V on Windows Server 2022", vms = new object[] { new { name = "DC01", power = "On", cpus = 2, memory_bytes = 4294967296L, host = "HV01" } } } } })));
        var hv = long.Parse(agent.GetProperty("assetId").GetString()!);
        dev = await J(await api.GetAsync($"/api/assets/{hv}/device"));
        Assert.Equal("DC01", dev.GetProperty("hardware").GetProperty("vms")[0].GetProperty("name").GetString());
        Assert.Contains("Hyper-V", dev.GetProperty("hardware").GetProperty("api").GetString());
    }
}
