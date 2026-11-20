using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

/// <summary>
/// 6.3: the Reports pages. Every asset and monitoring report runs, finds rows by search, exports CSV, and refuses bad filters; the Backup status
/// report and the Vault access report answer; the browser-tab icon is covered in BrandingTests.
/// </summary>
public class ReportsTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public ReportsTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); });

    static async Task<JsonElement> J(HttpResponseMessage r) { var t = await r.Content.ReadAsStringAsync(); Assert.True(r.IsSuccessStatusCode, t); return JsonDocument.Parse(t).RootElement.Clone(); }
    static async Task<object?> Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); return await c.ExecuteScalarAsync(); }

    [PgFact]
    public async Task EveryReportRunsFindsAndExports()
    {
        var api = await TestSupport.AdminClient(factory);
        await api.GetAsync("/api/assets/types"); await api.GetAsync("/api/monitoring/dashboard"); await api.GetAsync("/api/backup/jobs"); await api.GetAsync("/api/vault/me");
        var tag = "rpt" + Guid.NewGuid().ToString("N")[..8];
        var id = (long)(await Sql($@"insert into assets.assets (name, status, hostname, manufacturer, model, serial, cpu_model, ram_bytes, total_storage_bytes, os_name, antivirus_status, warranty_end,
              disks, nics, installed_apps, antivirus, m_disks, m_services, m_at, m_services_at, monitored)
            values ('{tag}-srv', 'in_use', '{tag}-host', 'Dell', 'R640', 'SN-{tag}', 'Xeon Silver', 34359738368, 1099511627776, 'Windows Server 2022', 'Protected', current_date + 10,
              '[{{""name"":""Disk 0"",""type"":""SSD"",""size_bytes"":500000000000,""partitions"":[{{""name"":""C:"",""mount"":""C:\\\\"",""fs"":""NTFS"",""size_bytes"":400000000000,""free_bytes"":40000000000}}]}}]',
              '[{{""name"":""Ethernet0"",""mac"":""00:11:22:33:44:55"",""connected"":true,""speed_mbps"":1000,""ips"":[""10.0.0.5""]}}]',
              '[{{""name"":""{tag} Agent"",""version"":""1.2.3"",""publisher"":""Acme""}}]',
              '[{{""name"":""Microsoft Defender"",""enabled"":true}}]',
              '[{{""mount"":""C:"",""total"":400000000000,""free"":40000000000,""pct"":10}}]',
              '[{{""name"":""Spooler"",""kind"":""service"",""running"":false,""state"":""stopped""}}]', now(), now(), true) returning id"))!;
        await Sql($"insert into assets.metrics_hour (asset_id, hour, cpu_avg, cpu_max, mem_avg, mem_max, disk_free_min, n) values ({id}, date_trunc('hour', now()) - interval '1 hour', 40, 95, 60, 80, 10, 60)");
        await Sql($"insert into monitoring.alerts (akey, rule_name, metric, severity, asset_id, asset_name, text) values ('{tag}', 'Disk space', 'disk', 'warning', {id}, '{tag}-srv', 'C: has 10% free')");

        // asset reports
        var cat = await J(await api.GetAsync("/api/assets/report-catalog"));
        Assert.Equal(new[] { "inventory", "hardware", "disks", "network", "software", "antivirus", "contracts", "licences", "agents" }, cat.EnumerateArray().Select(x => x.GetProperty("key").GetString()));
        foreach (var (key, want) in new[] { ("inventory", "SN-" + tag), ("hardware", "Xeon Silver"), ("disks", "NTFS"), ("network", "00:11:22:33:44:55"), ("software", tag + " Agent"), ("antivirus", "Microsoft Defender"), ("contracts", "Ends within 30 days") })
        {
            var rep = await J(await api.GetAsync($"/api/assets/report/{key}?q={tag}"));
            Assert.True(rep.GetProperty("total").GetInt32() >= 1, key);
            var row = rep.GetProperty("rows")[0];
            Assert.Equal(id.ToString(), row[0].GetString());   // the record id comes first, for links
            Assert.Equal(rep.GetProperty("columns").GetArrayLength() + 1, row.GetArrayLength());
            Assert.Contains(row.EnumerateArray(), v => v.GetString() == want);
            var csv = await api.GetAsync($"/api/assets/report/{key}.csv?q={tag}");
            Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
            Assert.Contains(want, await csv.Content.ReadAsStringAsync());
        }
        Assert.Equal(0, (await J(await api.GetAsync($"/api/assets/report/inventory?q={tag}&status=retired"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/assets/report/licences")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/assets/report/agents")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.GetAsync("/api/assets/report/secrets")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.GetAsync("/api/assets/report/inventory?company=x")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.GetAsync("/api/assets/report/inventory?status=gone")).StatusCode);
        Assert.Equal(0, (await J(await api.GetAsync("/api/assets/report/inventory?q=%25%27%3B--"))).GetProperty("total").GetInt32());   // the search is a value, never SQL

        // monitoring reports
        var mcat = await J(await api.GetAsync("/api/monitoring/report-catalog"));
        Assert.Equal(new[] { "alerts", "resources", "disks", "traffic", "services" }, mcat.EnumerateArray().Select(x => x.GetProperty("key").GetString()));
        foreach (var (key, want) in new[] { ("alerts", "C: has 10% free"), ("resources", "95.0"), ("disks", "10"), ("services", "stopped") })
        {
            var rep = await J(await api.GetAsync($"/api/monitoring/report/{key}?q={tag}&days=7"));
            Assert.True(rep.GetProperty("total").GetInt32() >= 1, key);
            Assert.Contains(rep.GetProperty("rows")[0].EnumerateArray(), v => v.GetString() == want);
            Assert.Contains(want, await (await api.GetAsync($"/api/monitoring/report/{key}.csv?q={tag}&days=7")).Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/monitoring/report/traffic")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.GetAsync("/api/monitoring/report/alerts?days=999")).StatusCode);

        // backup status and vault access
        var bk = await J(await api.GetAsync("/api/backup/report?days=30"));
        Assert.True(bk.TryGetProperty("summary", out _));
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/backup/report.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.GetAsync("/api/backup/report?company=x")).StatusCode);
        var va = await J(await api.GetAsync("/api/vault/reports/access?days=7"));
        Assert.True(va.TryGetProperty("items", out _));
        Assert.Equal(HttpStatusCode.BadRequest, (await api.GetAsync("/api/vault/reports/access?action=deleted")).StatusCode);

        // nobody signed out reads a report
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/assets/report/inventory")).StatusCode);
    }
}
