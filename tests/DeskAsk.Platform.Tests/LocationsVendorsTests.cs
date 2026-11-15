using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

public class LocationsVendorsTests : IClassFixture<WebApplicationFactory<Program>>
{
    const string Pw = "Loc-Test-Pass-9";
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;
    public LocationsVendorsTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b => { TestSupport.Configure(b); b.UseSetting("Licensing:AllowUnlicensed", "true"); b.UseSetting("Platform:Roles", "all"); b.UseSetting("Jobs:PollSeconds", "1"); });

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task Sql(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); await c.ExecuteNonQueryAsync(); }
    static async Task<object?> Scalar(string sql) { await using var db = new NpgsqlConnection(Pg); await db.OpenAsync(); await using var c = new NpgsqlCommand(sql, db); return await c.ExecuteScalarAsync(); }
    static async Task Until(Func<bool> ok) { for (var i = 0; i < 60 && !ok(); i++) await Task.Delay(500); }

    [PgFact]
    public async Task LocationsVendorsPurchaseDetailsAndReminders()
    {
        var admin = await TestSupport.AdminClient(factory); using var smtp = new FakeSmtp();
        await admin.GetAsync("/api/assets/types");
        await Sql("truncate assets.events, assets.ifstats, assets.port_watch, assets.metrics, assets.collectors, assets.creds, assets.agent_upd_log, assets.shells, assets.rc_log, assets.jobs, assets.agent_q, assets.agents, assets.enroll_tokens, assets.history, assets.assets, assets.companies, assets.amc, assets.rentals restart identity cascade");
        await Sql("truncate mail.outbox, mail.outbox_files restart identity; delete from assets.renewal_notices; delete from core.settings where key = 'assets.renewals'; delete from identity.users where \"Email\" like '%@loc.test'");
        await Sql("update jobs.recurring set next_run = now() + interval '1 day' where name = 'assets.renewals'");

        var acme = (await J(await admin.PostAsync("/api/assets/companies", Json(new { name = "Acme India", contactEmail = "it@acme.loc.test", renewalTo = "acme-team@msp.loc.test" })))).GetProperty("id").GetInt32();
        var globex = (await J(await admin.PostAsync("/api/assets/companies", Json(new { name = "Globex" })))).GetProperty("id").GetInt32();
        Assert.Equal("acme-team@msp.loc.test", (await J(await admin.GetAsync("/api/assets/companies"))).EnumerateArray().Single(c => c.GetProperty("id").GetInt32() == acme).GetProperty("renewalTo").GetString());

        // ---- locations: country > state > site; a site may also sit straight in a country
        async Task<int> Place(HttpClient c, object o) { var r = await c.PostAsync("/api/assets/places", Json(o)); Assert.Equal(HttpStatusCode.OK, r.StatusCode); return (await J(r)).GetProperty("id").GetInt32(); }
        var india = await Place(admin, new { companyId = acme, level = "country", name = "India" });
        var ka = await Place(admin, new { companyId = acme, level = "state", parentId = india, name = "Karnataka" });
        var blr = await Place(admin, new { companyId = acme, level = "site", parentId = ka, name = "Bengaluru HQ", code = "BLR01", address = "MG Road", city = "Bengaluru" });
        var uae = await Place(admin, new { companyId = acme, level = "country", name = "UAE" });
        var dxb = await Place(admin, new { companyId = acme, level = "site", parentId = uae, name = "Dubai office", code = "DXB01" });
        var gsite = await Place(admin, new { companyId = globex, level = "country", name = "India" }); // same name, another customer: fine
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/assets/places", Json(new { companyId = acme, level = "state", parentId = ka, name = "Inside a state" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/assets/places", Json(new { companyId = acme, level = "site", parentId = blr, name = "Inside a site" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/assets/places", Json(new { companyId = acme, level = "site", parentId = gsite, name = "Other customer's parent" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/assets/places", Json(new { companyId = acme, level = "site", parentId = ka, name = "Mysuru", code = "blr01" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/assets/places", Json(new { companyId = acme, level = "state", parentId = india, name = "karnataka" }))).StatusCode);

        // a customer administrator manages their own company's locations and vendors, and nobody else's
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/platform/users", Json(new { email = "cadm@loc.test", name = "Acme admin", role = "cadmin", password = Pw }))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/platform/users", Json(new { email = "eng@loc.test", name = "Viewer", role = "viewer", password = Pw }))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/platform/users", Json(new { email = "ravi@acme.loc.test", name = "Ravi Kumar", role = "customer", password = Pw }))).StatusCode);
        var cid = (await J(await admin.GetAsync("/api/platform/users"))).EnumerateArray().Single(x => x.GetProperty("email").GetString() == "cadm@loc.test").GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/platform/access/user-companies/{cid}", Json(new { companyId = acme }))).StatusCode);
        var cadm = await TestSupport.ClientFor(factory, "cadm@loc.test", Pw);
        var eng = await TestSupport.ClientFor(factory, "eng@loc.test", Pw);
        var mine = await J(await cadm.GetAsync("/api/assets/places"));
        Assert.All(mine.GetProperty("items").EnumerateArray(), p => Assert.Equal(acme, p.GetProperty("companyId").GetInt32()));
        Assert.True(mine.GetProperty("canManage").GetBoolean());
        var mys = await Place(cadm, new { companyId = acme, level = "site", parentId = ka, name = "Mysuru plant", code = "MYS01" });
        Assert.Equal(HttpStatusCode.Forbidden, (await cadm.PostAsync("/api/assets/places", Json(new { companyId = globex, level = "country", name = "Japan" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await eng.PostAsync("/api/assets/places", Json(new { companyId = acme, level = "country", name = "Japan" }))).StatusCode);
        Assert.False((await J(await eng.GetAsync("/api/assets/places"))).GetProperty("canManage").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await cadm.PutAsync($"/api/assets/places/{mys}", Json(new { contactName = "Plant IT", contactEmail = "plant@acme.loc.test" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await cadm.PutAsync($"/api/assets/places/{mys}", Json(new { contactEmail = "not an email" }))).StatusCode);

        // ---- vendors: registrations checked; shared vendors only by administrators
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/assets/vendors", Json(new { name = "Bad GST", gst = "12345" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/assets/vendors", Json(new { name = "Mismatch", gst = "29ABCDE1234F1Z5", pan = "ZZZZZ9999Z" }))).StatusCode);
        var shared = await J(await admin.PostAsync("/api/assets/vendors", Json(new { name = "Dell Technologies", contactEmail = "sales@dell.loc.test", renewalEmail = "renewals@dell.loc.test", gst = "29aabcd1234e1zx", pan = "AABCD1234E", tan = "BLRD12345F",
            otherIds = new[] { new { label = "MSME (Udyam)", value = "UDYAM-KR-03-0012345" } } })));
        var dell = shared.GetProperty("id").GetInt32();
        var local = (await J(await cadm.PostAsync("/api/assets/vendors", Json(new { companyId = acme, name = "Sri Sai Systems", contactEmail = "accounts@srisai.loc.test", vat = "", paymentTerms = "30 days" })))).GetProperty("id").GetInt32();
        var noMail = (await J(await admin.PostAsync("/api/assets/vendors", Json(new { companyId = acme, name = "Quiet vendor", contactEmail = "quiet@vendor.loc.test", remind = false })))).GetProperty("id").GetInt32();
        var gvendor = (await J(await admin.PostAsync("/api/assets/vendors", Json(new { companyId = globex, name = "Globex supplier" })))).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Forbidden, (await cadm.PostAsync("/api/assets/vendors", Json(new { name = "Shared by a customer?" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cadm.PutAsync($"/api/assets/vendors/{dell}", Json(new { notes = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/assets/vendors", Json(new { name = "dell technologies" }))).StatusCode);
        var vlist = (await J(await cadm.GetAsync("/api/assets/vendors"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(vlist, v => v.GetProperty("id").GetInt32() == dell && !v.GetProperty("canEdit").GetBoolean());
        Assert.Contains(vlist, v => v.GetProperty("id").GetInt32() == local && v.GetProperty("canEdit").GetBoolean());
        Assert.DoesNotContain(vlist, v => v.GetProperty("id").GetInt32() == gvendor);
        Assert.Equal("29AABCD1234E1ZX", vlist.Single(v => v.GetProperty("id").GetInt32() == dell).GetProperty("gst").GetString());

        // ---- assets with site, vendor, purchase details and a warranty period
        async Task<long> Asset(object o) { var r = await admin.PostAsync("/api/assets", Json(o)); Assert.Equal(HttpStatusCode.OK, r.StatusCode); return (await J(r)).GetProperty("id").GetInt64(); }
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var bought = today.AddMonths(-36).AddDays(30);
        var lap = await Asset(new { name = "Latitude 5440", companyId = acme, siteId = blr, vendorId = dell, poNumber = "PO-4500012", invoiceNumber = "INV-88", purchaseDate = bought.ToString("yyyy-MM-dd"), warrantyMonths = 36, assignedTo = "Ravi Kumar", serial = "8HX2LQ3" });
        var detail = (await J(await admin.GetAsync($"/api/assets/{lap}"))).GetProperty("asset");
        Assert.Equal(bought.AddMonths(36).AddDays(-1).ToString("yyyy-MM-dd"), detail.GetProperty("warrantyEnd").GetString()); // 36 months from purchase, less a day
        Assert.Equal("Bengaluru HQ", detail.GetProperty("site").GetString()); Assert.Equal("BLR01", detail.GetProperty("siteCode").GetString());
        Assert.Equal("Karnataka", detail.GetProperty("state").GetString()); Assert.Equal("India", detail.GetProperty("country").GetString()); Assert.Equal("Dell Technologies", detail.GetProperty("vendor").GetString());
        var srv = await Asset(new { name = "Dubai NAS", companyId = acme, siteId = dxb, vendorId = local, warrantyEnd = today.AddDays(14).ToString("yyyy-MM-dd") });
        Assert.Equal("UAE", (await J(await admin.GetAsync($"/api/assets/{srv}"))).GetProperty("asset").GetProperty("country").GetString());
        var quiet = await Asset(new { name = "Quiet switch", companyId = acme, siteId = blr, vendorId = noMail, warrantyEnd = today.AddDays(20).ToString("yyyy-MM-dd") });
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/assets", Json(new { name = "Wrong site", companyId = globex, siteId = blr }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/assets", Json(new { name = "Wrong vendor", companyId = acme, vendorId = gvendor }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync($"/api/assets/{lap}", Json(new { siteId = india }))).StatusCode); // a country is not a site

        // filters: a country takes in its sites; vendor; search by site code
        long Total(JsonElement e) => e.GetProperty("total").GetInt64();
        Assert.Equal(2, Total(await J(await admin.GetAsync($"/api/assets?place={india}"))));
        Assert.Equal(1, Total(await J(await admin.GetAsync($"/api/assets?place={uae}"))));
        Assert.Equal(1, Total(await J(await admin.GetAsync($"/api/assets?vendor={dell}"))));
        Assert.Equal(1, Total(await J(await admin.GetAsync("/api/assets?search=DXB01"))));
        Assert.Equal(1, Total(await J(await admin.GetAsync("/api/assets?search=PO-4500012"))));
        var csv = await (await admin.GetAsync($"/api/assets/export.csv?place={india}")).Content.ReadAsStringAsync();
        Assert.Contains("country,state,site,site_code,vendor,po_number,invoice_number,warranty_months", csv);
        Assert.Contains("India,Karnataka,Bengaluru HQ,BLR01,Dell Technologies,PO-4500012,INV-88,36", csv);

        // a place in use cannot go; its assets can move
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/assets/places/{ka}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/assets/places/{blr}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/assets/vendors/{dell}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cadm.DeleteAsync($"/api/assets/places/{mys}")).StatusCode);

        // import with company, site code and vendor
        var imp = new StringContent("name,company,site_code,vendor,po_number,purchase_date,warranty_months\nImported printer,Acme India,BLR01,Sri Sai Systems,PO-77,2025-01-10,12\nBad site,Acme India,NOPE,,,,\n", Encoding.UTF8, "text/csv");
        var ir = await J(await admin.PostAsync("/api/assets/import", imp));
        Assert.Equal(1, ir.GetProperty("created").GetInt32()); Assert.Equal("unknown_site", ir.GetProperty("errors")[0].GetProperty("error").GetString());
        Assert.Equal("2026-01-09", (string)(await Scalar("select to_char(warranty_end, 'YYYY-MM-DD') from assets.assets where name = 'Imported printer' and site_id is not null and vendor_id is not null and company_id is not null"))!);

        // changing an asset's customer drops a site and vendor of the old customer, keeps a shared vendor
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsync($"/api/assets/{lap}/ownership", Json(new { companyId = globex }))).StatusCode);
        var moved = (await J(await admin.GetAsync($"/api/assets/{lap}"))).GetProperty("asset");
        Assert.Equal(JsonValueKind.Null, moved.GetProperty("siteId").ValueKind); Assert.Equal(dell, moved.GetProperty("vendorId").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsync($"/api/assets/{lap}/ownership", Json(new { companyId = acme }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync($"/api/assets/{lap}", Json(new { siteId = blr }))).StatusCode);

        // ---- AMC with a vendor, renewed: the old contract stops reminding
        var amc = (await J(await admin.PostAsync("/api/assets/amc", Json(new { companyId = acme, title = "Server room AMC", vendorId = local, contractNo = "AMC-2025-118", cost = 120000.5, start = today.AddYears(-1).ToString("yyyy-MM-dd"), end = today.AddDays(10).ToString("yyyy-MM-dd"), assetIds = new[] { srv } })))).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/assets/amc", Json(new { companyId = acme, title = "x", vendorId = gvendor }))).StatusCode);
        var old = (await J(await admin.PostAsync("/api/assets/amc", Json(new { companyId = acme, title = "Old printer AMC", vendorId = dell, end = today.AddDays(5).ToString("yyyy-MM-dd") })))).GetProperty("id").GetInt32();
        var renewed = await J(await admin.PostAsync($"/api/assets/amc/{old}/renew", Json(new { contractNo = "AMC-2026-7", cost = 9000 })));
        Assert.Equal(today.AddDays(6).ToString("yyyy-MM-dd"), renewed.GetProperty("start").GetString());
        Assert.Equal(today.AddDays(6).AddYears(1).AddDays(-1).ToString("yyyy-MM-dd"), renewed.GetProperty("end").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/assets/amc/{old}/renew", Json(new { }))).StatusCode);
        var contracts = (await J(await admin.GetAsync("/api/assets/amc"))).EnumerateArray().ToList();
        Assert.Equal(renewed.GetProperty("id").GetInt32(), contracts.Single(c => c.GetProperty("id").GetInt32() == old).GetProperty("renewedById").GetInt32());
        Assert.Equal("AMC-2025-118", contracts.Single(c => c.GetProperty("id").GetInt32() == amc).GetProperty("contractNo").GetString());
        var vd = await J(await cadm.GetAsync($"/api/assets/vendors/{local}"));
        Assert.Equal(2, vd.GetProperty("assets").GetArrayLength()); Assert.Equal(1, vd.GetProperty("amc").GetArrayLength());

        // ---- reminders one month and 15 days ahead: team, customer's team and contact, the user, the vendor
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/platform/mail/config", Json(new { provider = "smtp", from = "desk@example.test", fromName = "DeskAsk", host = "127.0.0.1", port = smtp.Port, security = "none", user = "", password = "" }))).StatusCode);
        var defaults = await J(await admin.GetAsync("/api/assets/renewals/settings"));
        Assert.Equal(new[] { 30, 15 }, defaults.GetProperty("days").EnumerateArray().Select(d => d.GetInt32()).ToArray());
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/assets/renewals/settings", Json(new { enabled = true, days = new[] { 30, 15 }, to = "noc@msp.loc.test", customers = true, users = true, vendors = true }))).StatusCode);
        var up = (await J(await cadm.GetAsync("/api/assets/renewals/upcoming?days=30"))).GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(up, x => x.GetProperty("kind").GetString() == "amc" && x.GetProperty("vendor").GetString() == "Sri Sai Systems");
        Assert.DoesNotContain(up, x => x.GetProperty("title").GetString() == "Old printer AMC"); // renewed
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/assets/renewals/run", null)).StatusCode);
        string[] Box(string addr) => smtp.Snapshot().Where(m => m.Contains("To: " + addr) || m.Contains("<" + addr + ">")).ToArray();
        await Until(() => Box("renewals@dell.loc.test").Length > 0 && Box("accounts@srisai.loc.test").Length > 0 && Box("ravi@acme.loc.test").Length > 0 && Box("noc@msp.loc.test").Length > 0);
        Assert.Contains("Latitude 5440", Box("ravi@acme.loc.test")[0]);                       // the user, about their laptop
        Assert.DoesNotContain("Dubai NAS", Box("ravi@acme.loc.test")[0]);
        Assert.Contains("Latitude 5440", Box("renewals@dell.loc.test")[0]);                   // the vendor's renewal address, about what it sold
        Assert.Contains("PO-4500012", Box("renewals@dell.loc.test")[0]);
        Assert.DoesNotContain("Dubai NAS", Box("renewals@dell.loc.test")[0]);
        Assert.Contains("Server room AMC", Box("accounts@srisai.loc.test")[0]); Assert.Contains("Dubai NAS", Box("accounts@srisai.loc.test")[0]);
        Assert.Empty(Box("quiet@vendor.loc.test"));                                            // a vendor set not to be mailed
        foreach (var a in new[] { "noc@msp.loc.test", "acme-team@msp.loc.test", "it@acme.loc.test" })
        {
            var m = Box(a); Assert.Single(m);
            Assert.Contains("Latitude 5440", m[0]); Assert.Contains("Server room AMC", m[0]); Assert.Contains("Quiet switch", m[0]);
        }
        // the 15-day window for the NAS and the AMC, the 30-day window for the laptop; nothing twice
        Assert.Equal(1L, Convert.ToInt64(await Scalar($"select count(*) from assets.renewal_notices where kind = 'warranty' and ref_id = {lap} and days = 30")));
        Assert.Equal(1L, Convert.ToInt64(await Scalar($"select count(*) from assets.renewal_notices where kind = 'warranty' and ref_id = {srv} and days = 15")));
        var count = smtp.Snapshot().Length;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/assets/renewals/run", null)).StatusCode);
        await Task.Delay(4000);
        Assert.Equal(count, smtp.Snapshot().Length);
        Assert.Contains((await J(await admin.GetAsync("/api/assets/renewals/upcoming?days=30"))).GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetInt64() == lap && x.GetProperty("remindersSent")[0].GetInt32() == 30);
        _ = quiet;
    }
}
