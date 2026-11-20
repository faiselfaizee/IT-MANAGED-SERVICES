using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeskAsk.Platform;
using DeskAsk.Platform.Commerce;
using DeskAsk.Platform.Licensing;
using DeskAsk.Platform.Tenancy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

/// <summary>
/// Release 6.1, the store: business details, prices with GST, Razorpay checkout (a fake Razorpay answers), webhooks, invoices, SaaS customers
/// created on payment, free trials, extending and deleting them. The test host is the vendor platform; its customer "dev" is the vendor's own.
/// </summary>
public class CommerceStoreTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly ECDsa Vendor = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    const string Secret = "rzp-test-secret", Hook = "rzp-webhook-secret";
    readonly WebApplicationFactory<Program> factory;
    public CommerceStoreTests(WebApplicationFactory<Program> f) => factory = f.WithWebHostBuilder(b =>
    {
        TestSupport.Configure(b);
        b.UseSetting("Licensing:VendorPublicKeyPem", Vendor.ExportSubjectPublicKeyInfoPem());
        b.UseSetting("Licensing:CacheSeconds", "0");
        b.UseSetting("Licensing:CatalogConnection", Pg);
        b.UseSetting("Commerce:Enabled", "true");
        b.UseSetting("Commerce:VendorTenant", "dev");
        b.UseSetting("Commerce:Connection", Pg);
    });

    sealed class FakeRazorpay : HttpMessageHandler
    {
        public int Orders;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Assert.Equal("https://api.razorpay.com/v1/orders", req.RequestUri!.ToString());
            Assert.Equal("Basic", req.Headers.Authorization!.Scheme);
            var n = Interlocked.Increment(ref Orders);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"order_T" + n.ToString("0000") + Guid.NewGuid().ToString("N")[..8] + "\",\"status\":\"created\"}") });
        }
    }

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
    static async Task<JsonElement> J(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();
    static async Task<object?> Scalar(string sql, params (string, object)[] p)
    {
        await using var db = new NpgsqlConnection(Pg); await db.OpenAsync();
        await using var c = new NpgsqlCommand(sql, db);
        foreach (var (n, v) in p) c.Parameters.AddWithValue(n, v);
        return await c.ExecuteScalarAsync();
    }
    static string Rand() => Convert.ToHexString(RandomNumberGenerator.GetBytes(3)).ToLowerInvariant();

    static object Buyer => new { company = "Acme Industries", name = "Asha Raman", email = "asha@acme.test", phone = "+91 90000 00000", address = "12 Anna Salai", city = "Chennai", pincode = "600002", stateName = "Tamil Nadu" };

    [Fact]
    public void PricesTaxAndWords()
    {
        var biz = new Business(LegalName: "DamazeTech", Gstin: "33AAKCD8221B1Z3");
        var prices = JsonDocument.Parse("{\"saas\":{\"usd\":{\"year\":1190,\"month\":119},\"inr\":{\"year\":68000}},\"onprem\":{\"usd\":{\"year\":955}}}").RootElement;
        var plans = new List<Store.Plan>
        {
            new(1, "servicedesk", "10 agents", new() { ["agents"] = 10 }, prices, 1), new(2, "assets", "500 assets", new() { ["max_assets"] = 500 }, prices, 2),
            new(3, "monitoring", "150 devices", new() { ["max_devices"] = 150 }, prices, 3), new(4, "vault", "25 users", new() { ["max_users"] = 25 }, prices, 4),
        };
        var same = Store.Make(new("saas", "year", new() { ["servicedesk"] = 1 }, "IN", "33", ""), plans, biz).Quote!;
        Assert.Equal(68000m, same.Subtotal);
        Assert.Equal(new[] { "CGST", "SGST" }, same.Taxes.Select(t => t.Name));
        Assert.Equal(80240m, same.Total);
        var other = Store.Make(new("saas", "year", new() { ["servicedesk"] = 1 }, "IN", "", "29AAACA1234A1Z5"), plans, biz).Quote!;   // state from the buyer's GSTIN
        Assert.Equal("IGST", Assert.Single(other.Taxes).Name);
        Assert.Equal(12240m, other.Tax);
        var export = Store.Make(new("saas", "month", new() { ["servicedesk"] = 1 }, "US", "", ""), plans, biz).Quote!;
        Assert.Equal("USD", export.Currency); Assert.Empty(export.Taxes); Assert.Equal(119m, export.Total); Assert.Contains("export", export.TaxNote);
        Assert.Equal(54400m, Store.Make(new("onprem", "year", new() { ["servicedesk"] = 1 }, "IN", "33", ""), plans, biz).Quote!.Subtotal);   // on-prem: 20% less
        var suite = Store.Make(new("saas", "year", new() { ["servicedesk"] = 1, ["assets"] = 2, ["monitoring"] = 3, ["vault"] = 4 }, "IN", "33", ""), plans, biz).Quote!;
        Assert.Equal(68000m, suite.Discount);   // 25% of 4 x 68,000
        Assert.Contains("Asset management", Store.Make(new("saas", "year", new() { ["monitoring"] = 3 }, "IN", "33", ""), plans, biz).Error);
        Assert.NotNull(Store.Make(new("onprem", "month", new() { ["servicedesk"] = 1 }, "IN", "33", ""), plans, biz).Error);
        Assert.Equal("Rupees One Lakh Fifty Three Thousand Five Hundred Only", Invoices.Words(153500m, "INR"));
        Assert.Equal("US Dollars Two Thousand Four Hundred Eighty Five and Fifty Cents Only", Invoices.Words(2485.50m, "USD"));
        Assert.Equal("1,53,500.00", Store.Indian(153500m));
        Assert.Equal("2026-27", Invoices.Fy(new DateTime(2026, 11, 19)));
        Assert.Equal("2025-26", Invoices.Fy(new DateTime(2026, 3, 31)));
        Assert.True(Store.GstinOk("33AAKCD8221B1Z3")); Assert.False(Store.GstinOk("33AAKCD8221B1Y3"));
        Assert.True(Razorpay.PaymentSignatureOk("s", "order_1", "pay_1", Razorpay.Sign("s", "order_1|pay_1")));
        Assert.False(Razorpay.PaymentSignatureOk("s", "order_1", "pay_2", Razorpay.Sign("s", "order_1|pay_1")));
    }

    [Fact]
    public void AVendorKeyThatCannotBeReadNeverStopsTheServer()
    {
        var pem = Vendor.ExportSubjectPublicKeyInfoPem();
        var body = string.Concat(pem.Split('\n').Where(l => !l.StartsWith("-----")));
        foreach (var form in new[] { pem, pem.Replace("\n", "\\n"), body })
            using (var k = VendorKey.Read(form)) Assert.Equal(LicenseCodec.KeyId(Vendor), LicenseCodec.KeyId(k!));
        Assert.Null(VendorKey.Read("-----BEGIN PUBLIC KEY-----\\nMFkw....\\n-----END PUBLIC KEY-----"));
        Assert.Null(VendorKey.Read(Vendor.ExportPkcs8PrivateKeyPem()));   // not a public key
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Licensing:VendorPublicKeyPem"] = "MFkw...." }).Build();
        var v = new VendorKey(cfg);
        Assert.NotNull(v.Problem);
        Assert.NotEqual("configuration", v.Source);
    }

    [PgFact]
    public async Task Store_OrdersTrialsAndInvoices_EndToEnd()
    {
        var fake = new FakeRazorpay();
        Razorpay.TestHandler = fake;
        var admin = await TestSupport.AdminClient(factory);
        var anon = factory.CreateClient();
        await SchemaMigrator.RunAsync(Pg, "catalog", CatalogSchema.Migrations, CancellationToken.None);
        var made = new List<string>();
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/commerce/signing-key", Json(new { pem = Vendor.ExportPkcs8PrivateKeyPem() }))).StatusCode);

            // 1. closed until business details and payment keys are set
            Assert.False((await J(await anon.GetAsync("/api/store/info"))).GetProperty("open").GetBoolean());
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/commerce/business", Json(new { legalName = "X", gstin = "33AAKCD8221B1Y3" }))).StatusCode);
            var biz = await admin.PutAsync("/api/commerce/business", Json(new
            {
                legalName = "DamazeTech Solutions (OPC) Private Limited", address = "Plot No. A4, Chandrasekaran Avenue, 1st Street, Thuraipakkam", city = "Chennai", state = "Tamil Nadu", pincode = "600097",
                gstin = "33AAKCD8221B1Z3", cin = "U63999TN2024OPC169692", pan = "AAKCD8221B", sac = "998315", supportEmail = "servicedesk@deskask.in", supportPhone = "+91 90425 35749",
                grievanceName = "Faisel Ahmed", grievanceDesignation = "Chief Executive Officer", invoicePrefix = "DA", tenantDomain = "deskask.test", trialDays = 14, trialKeepDays = 45,
            }));
            Assert.True(biz.IsSuccessStatusCode, await biz.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsync("/api/commerce/payments", Json(new { keyId = "not-a-key" }))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/commerce/payments", Json(new { keyId = "rzp_test_TuVf3mkccS7MvN", keySecret = Secret, webhookSecret = Hook }))).StatusCode);
            var pay = await J(await admin.GetAsync("/api/commerce/payments"));
            Assert.Equal("test", pay.GetProperty("razorpay").GetProperty("mode").GetString());
            Assert.True(pay.GetProperty("razorpay").GetProperty("hasSecret").GetBoolean());
            var info = await J(await anon.GetAsync("/api/store/info"));
            Assert.True(info.GetProperty("open").GetBoolean());
            Assert.Equal("Faisel Ahmed", info.GetProperty("business").GetProperty("grievanceName").GetString());

            // 2. a quote is worked out on the server
            var plans = await J(await anon.GetAsync("/api/store/plans"));
            int Plan(string m, string size) => plans.EnumerateArray().First(p => p.GetProperty("module").GetString() == m && p.GetProperty("sizeLabel").GetString() == size).GetProperty("id").GetInt32();
            var sd10 = Plan("servicedesk", "10 agents"); var as500 = Plan("assets", "500 assets");
            var q = await J(await anon.PostAsync("/api/store/quote", Json(new { kind = "saas", period = "year", plans = new Dictionary<string, int> { ["servicedesk"] = sd10, ["assets"] = as500 }, country = "IN", stateCode = "33" })));
            Assert.Equal(142000m, q.GetProperty("subtotal").GetDecimal());
            Assert.Equal(167560m, q.GetProperty("total").GetDecimal());

            // 3. names
            Assert.False((await J(await anon.GetAsync("/api/store/available?id=www"))).GetProperty("available").GetBoolean());
            Assert.False((await J(await anon.GetAsync("/api/store/available?id=dev"))).GetProperty("available").GetBoolean());   // reserved

            // 4. an on-prem order, confirmed by the browser's signed answer
            var co = await anon.PostAsync("/api/store/checkout", Json(new { kind = "onprem", period = "year", plans = new Dictionary<string, int> { ["servicedesk"] = sd10 }, country = "IN", stateCode = "29", buyer = Buyer }));
            Assert.True(co.IsSuccessStatusCode, await co.Content.ReadAsStringAsync());
            var c1 = await J(co);
            var order1 = c1.GetProperty("orderId").GetString()!; var access1 = c1.GetProperty("access").GetString()!;
            var rz1 = c1.GetProperty("razorpay").GetProperty("order_id").GetString()!;
            Assert.Equal(64192_00, c1.GetProperty("razorpay").GetProperty("amount").GetInt64());   // 54,400 + 18% IGST, in paise
            Assert.Equal("rzp_test_TuVf3mkccS7MvN", c1.GetProperty("razorpay").GetProperty("key").GetString());
            Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsync("/api/store/checkout/confirm", Json(new { razorpay_order_id = rz1, razorpay_payment_id = "pay_A1", razorpay_signature = "00" }))).StatusCode);
            Assert.Equal("pending", (await J(await anon.GetAsync("/api/store/orders/" + order1 + "?t=" + access1))).GetProperty("status").GetString());
            var conf = await anon.PostAsync("/api/store/checkout/confirm", Json(new { razorpay_order_id = rz1, razorpay_payment_id = "pay_A1", razorpay_signature = Razorpay.Sign(Secret, rz1 + "|pay_A1") }));
            Assert.True(conf.IsSuccessStatusCode, await conf.Content.ReadAsStringAsync());
            var st1 = await J(await anon.GetAsync("/api/store/orders/" + order1 + "?t=" + access1));
            Assert.True(st1.GetProperty("status").GetString() == "fulfilled", st1.ToString());
            Assert.Matches("^DKA-([A-Z2-9]{5}-){4}[A-Z2-9]{5}$", st1.GetProperty("licenceKey").GetString()!);
            Assert.StartsWith("DA/" + Invoices.Fy(DateTime.UtcNow) + "/", st1.GetProperty("invoice").GetString());
            Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/store/orders/" + order1 + "?t=wrong")).StatusCode);
            var pdf = await anon.GetAsync("/api/store/orders/" + order1 + "/invoice?t=" + access1);
            Assert.Equal("application/pdf", pdf.Content.Headers.ContentType!.MediaType);
            Assert.StartsWith("%PDF", Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]));
            // the licence key from the order activates (the subscription is on-prem, 14 grace days)
            Assert.Equal(HttpStatusCode.OK, (await anon.PostAsync("/api/store/activate", Json(new { key = st1.GetProperty("licenceKey").GetString(), installation = new string('d', 32), tenant = "beta" }))).StatusCode);

            // 5. a second order completed by the webhook; a replay and the late browser answer do nothing more
            var c2 = await J(await anon.PostAsync("/api/store/checkout", Json(new { kind = "onprem", period = "year", plans = new Dictionary<string, int> { ["assets"] = as500 }, country = "US", buyer = Buyer })));
            var order2 = c2.GetProperty("orderId").GetString()!; var rz2 = c2.GetProperty("razorpay").GetProperty("order_id").GetString()!;
            Assert.Equal("USD", c2.GetProperty("razorpay").GetProperty("currency").GetString());
            var body = JsonSerializer.Serialize(new { @event = "payment.captured", payload = new { payment = new { entity = new { id = "pay_B2", order_id = rz2, status = "captured" } } } });
            HttpRequestMessage Hooked(string sig, string eventId)
            {
                var m = new HttpRequestMessage(HttpMethod.Post, "/api/store/webhooks/razorpay") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                m.Headers.Add("X-Razorpay-Signature", sig); m.Headers.Add("X-Razorpay-Event-Id", eventId); return m;
            }
            var eventId = "evt_" + Rand();
            Assert.Equal(HttpStatusCode.BadRequest, (await anon.SendAsync(Hooked("bad", eventId))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await anon.SendAsync(Hooked(Razorpay.Sign(Hook, body), eventId))).StatusCode);
            Assert.True((await J(await anon.SendAsync(Hooked(Razorpay.Sign(Hook, body), eventId)))).GetProperty("duplicate").GetBoolean());
            Assert.Equal(HttpStatusCode.OK, (await anon.PostAsync("/api/store/checkout/confirm", Json(new { razorpay_order_id = rz2, razorpay_payment_id = "pay_B2", razorpay_signature = Razorpay.Sign(Secret, rz2 + "|pay_B2") }))).StatusCode);
            Assert.Equal(1L, await Scalar("select count(*) from commerce.invoices where order_id = @o", ("o", Guid.Parse(order2))));
            var orders = await J(await admin.GetAsync("/api/commerce/orders"));
            Assert.Contains(orders.EnumerateArray(), o => o.GetProperty("id").GetString() == order2 && o.GetProperty("status").GetString() == "fulfilled");
            var invoices = await J(await admin.GetAsync("/api/commerce/invoices"));
            Assert.True(invoices.GetArrayLength() >= 2);
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/commerce/invoices/" + invoices[0].GetProperty("id").GetInt64() + "/pdf")).StatusCode);

            // 6. a SaaS order creates the customer: database, licence, first administrator
            var shop = "shop-" + Rand(); made.Add(shop);
            var c3 = await J(await anon.PostAsync("/api/store/checkout", Json(new { kind = "saas", period = "month", plans = new Dictionary<string, int> { ["servicedesk"] = sd10 }, country = "IN", stateCode = "33", tenantId = shop, buyer = Buyer })));
            var rz3 = c3.GetProperty("razorpay").GetProperty("order_id").GetString()!;
            var conf3 = await anon.PostAsync("/api/store/checkout/confirm", Json(new { razorpay_order_id = rz3, razorpay_payment_id = "pay_C3", razorpay_signature = Razorpay.Sign(Secret, rz3 + "|pay_C3") }));
            Assert.True(conf3.IsSuccessStatusCode, await conf3.Content.ReadAsStringAsync());
            var st3 = await J(await anon.GetAsync("/api/store/orders/" + c3.GetProperty("orderId").GetString() + "?t=" + c3.GetProperty("access").GetString()));
            Assert.True(st3.GetProperty("status").GetString() == "fulfilled", st3.ToString());
            Assert.Equal("https://" + shop + ".deskask.test", st3.GetProperty("address").GetString());
            Assert.Equal(true, await Scalar("select active from platform.tenants where id = @i", ("i", shop)));
            var token = (string)(await Scalar("select token from platform.licenses where tenant_id = @i", ("i", shop)))!;
            Assert.Equal(LicenseState.Valid, LicenseCodec.Verify(token, Vendor, shop, DateTimeOffset.UtcNow).State);
            var conn = (string)(await Scalar("select connection_string from platform.tenants where id = @i", ("i", shop)))!;
            await using (var tdb = new NpgsqlConnection(conn))
            {
                await tdb.OpenAsync();
                await using var u = new NpgsqlCommand("select \"Role\" from identity.users where \"Email\" = 'asha@acme.test'", tdb);
                Assert.Equal("admin", await u.ExecuteScalarAsync());
                await using var all = new NpgsqlCommand("select count(*) from identity.users", tdb);
                Assert.Equal(1L, await all.ExecuteScalarAsync());   // the buyer only, never the platform's bootstrap administrator
            }
            Assert.False((await J(await anon.GetAsync("/api/store/available?id=" + shop))).GetProperty("available").GetBoolean());

            // 7. a free trial; extended by the vendor; then deleted with its database
            var trial = "trial-" + Rand(); made.Add(trial);
            var tr = await anon.PostAsync("/api/store/trial", Json(new { company = "Gamma Ltd", name = "Ravi", email = "ravi@gamma.test", tenantId = trial, modules = new[] { "monitoring" } }));
            Assert.True(tr.IsSuccessStatusCode, await tr.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsync("/api/store/trial", Json(new { company = "Gamma Ltd", name = "Ravi", email = "ravi@gamma.test", tenantId = trial, modules = new[] { "vault" } }))).StatusCode);   // taken
            Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsync("/api/store/trial", Json(new { company = "Z", name = "Z", email = "z@mailinator.com", tenantId = "z-" + Rand(), modules = new[] { "vault" } }))).StatusCode);
            var subs = await J(await admin.GetAsync("/api/commerce/subscriptions?q=" + trial));
            var ts = subs.EnumerateArray().Single().GetProperty("sub");
            Assert.Equal("trial", ts.GetProperty("state").GetString());
            Assert.True(ts.GetProperty("lines").TryGetProperty("assets", out _));   // monitoring brings asset management
            var tsId = ts.GetProperty("id").GetString();
            var ends = ts.GetProperty("endsAt").GetDateTime();
            var ext = await J(await admin.PostAsync("/api/commerce/subscriptions/" + tsId + "/extend", Json(new { days = 7 })));
            Assert.True((ext.GetProperty("endsAt").GetDateTime() - ends).TotalDays is > 6.9 and < 7.1);
            var tl = (string)(await Scalar("select token from platform.licenses where tenant_id = @i", ("i", trial)))!;
            Assert.Equal("trial", LicenseCodec.Verify(tl, Vendor, trial, DateTimeOffset.UtcNow).Payload!.Plan);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/commerce/subscriptions/" + tsId + "/delete-customer", Json(new { confirm = "nope" }))).StatusCode);
            var tconn = (string)(await Scalar("select connection_string from platform.tenants where id = @i", ("i", trial)))!;
            Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/commerce/subscriptions/" + tsId + "/delete-customer", Json(new { confirm = trial }))).StatusCode);
            Assert.Null(await Scalar("select 1 from platform.tenants where id = @i", ("i", trial)));
            Assert.Null(await Scalar("select 1 from pg_database where datname = @n", ("n", new NpgsqlConnectionStringBuilder(tconn).Database!)));

            // website pages: the built-in draft, edited in the console, back to the draft
            var terms = await J(await anon.GetAsync("/api/store/pages/terms"));
            Assert.Equal("Terms of Service", terms.GetProperty("title").GetString());
            Assert.Contains("Law and disputes", terms.GetProperty("markdown").GetString());
            Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/store/pages/secret")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsync("/api/commerce/pages/terms", Json(new { markdown = "x" }))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/commerce/pages/terms", Json(new { markdown = "### New terms\n\nReviewed by counsel." }))).StatusCode);
            Assert.Contains("Reviewed by counsel", (await J(await anon.GetAsync("/api/store/pages/terms"))).GetProperty("markdown").GetString());
            Assert.Equal(5, (await J(await admin.GetAsync("/api/commerce/pages"))).GetArrayLength());
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsync("/api/commerce/pages/terms", Json(new { markdown = "" }))).StatusCode);
            Assert.Contains("Law and disputes", (await J(await anon.GetAsync("/api/store/pages/terms"))).GetProperty("markdown").GetString());

            // Administration › Subscription answers for every customer; the store link is the website's
            var mine = await J(await admin.GetAsync("/api/platform/subscription"));
            Assert.True(mine.GetProperty("store").GetBoolean()); Assert.Equal("https://www.deskask.in/pricing", mine.GetProperty("storeUrl").GetString());
            Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/platform/subscription/invoices/" + invoices[0].GetProperty("id").GetInt64() + "/pdf")).StatusCode);   // another customer's invoice

            // the vendor console stays closed to everyone else
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/commerce/business")).StatusCode);
        }
        finally
        {
            Razorpay.TestHandler = null;
            var prov = factory.Services.GetRequiredService<ITenantProvisioner>();
            foreach (var t in made) try { await prov.DeleteAsync(t); } catch (Exception) { }
        }
    }
}
