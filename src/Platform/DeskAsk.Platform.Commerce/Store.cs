using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace DeskAsk.Platform.Commerce;

/// <summary>The vendor's business details: on invoices, the store's footer and its legal pages. Edited in the vendor console (setting "business").</summary>
public sealed record Business(
    string LegalName = "", string TradeName = "DeskAsk", string Address = "", string City = "", string State = "", string Pincode = "", string Country = "India",
    string Gstin = "", string Cin = "", string Pan = "", string Sac = "998315", string Lut = "",
    string SupportEmail = "", string SupportPhone = "", string GrievanceName = "", string GrievanceDesignation = "", string GrievanceEmail = "",
    string InvoicePrefix = "DA", string TermsUrl = "", string PrivacyUrl = "", string RefundUrl = "", string DeliveryUrl = "",
    string TenantDomain = "deskask.in", int TrialDays = 14, int TrialKeepDays = 45)
{
    public string StateCode => Gstin.Length >= 2 ? Gstin[..2] : "";
    /// <summary>What must be set before the store can take orders.</summary>
    public string? Missing() =>
        LegalName == "" ? "the legal name" : Address == "" ? "the address" : !Store.GstinOk(Gstin) ? "the GSTIN" : SupportEmail == "" ? "the support email" : null;

    public static async Task<Business> LoadAsync(CommerceOptions o, CancellationToken ct)
    {
        var raw = await CommerceDb.GetSettingAsync(o, "business", ct);
        if (raw == null) return new Business();
        try { return JsonSerializer.Deserialize<Business>(raw, Licences.Json) ?? new Business(); } catch (JsonException) { return new Business(); }
    }
}

/// <summary>Prices, tax and the totals the buyer pays. Worked out on the server only; a price from the browser is never used.</summary>
public static class Store
{
    public static bool GstinOk(string? g) => g != null && Regex.IsMatch(g, "^[0-3][0-9][A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$");
    public static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "www", "dev", "hrms", "platform", "api", "admin", "mail", "smtp", "imap", "store", "shop", "support", "help", "status", "rustdesk", "test", "demo", "app", "portal", "login", "account", "billing", "docs", "blog", "cdn", "static", "ftp", "vpn", "ns1", "ns2", "deskask", "damazetech" };
    public static readonly Regex TenantId = new("^[a-z][a-z0-9-]{1,30}$");

    public sealed record Plan(int Id, string Module, string SizeLabel, Dictionary<string, int> Limits, JsonElement Prices, int Sort);

    public static async Task<List<Plan>> PlansAsync(NpgsqlConnection db, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand("select id, module, size_label, limits::text, prices::text, sort from commerce.plans where active order by sort, id", db);
        await using var rd = await q.ExecuteReaderAsync(ct);
        var list = new List<Plan>();
        while (await rd.ReadAsync(ct))
            list.Add(new Plan(rd.GetInt32(0), rd.GetString(1), rd.GetString(2), JsonSerializer.Deserialize<Dictionary<string, int>>(rd.GetString(3)) ?? new(),
                JsonDocument.Parse(rd.GetString(4)).RootElement.Clone(), rd.GetInt32(5)));
        return list;
    }

    static decimal? Num(JsonElement e, params string[] path)
    {
        foreach (var p in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null; }
        return e.ValueKind == JsonValueKind.Number ? e.GetDecimal() : null;
    }

    /// <summary>The price of one plan for a currency, kind and period; null when it is not sold that way.</summary>
    public static decimal? Price(Plan p, string currency, string kind, string period)
    {
        if (currency == "INR")
        {
            var year = Num(p.Prices, "saas", "inr", "year");
            if (year == null) return null;
            if (kind == "onprem") return period == "year" ? Math.Round(year.Value * 0.8m / 100m) * 100m : null;
            return period == "year" ? year : Math.Round(year.Value / 12m * 1.2m / 10m) * 10m;
        }
        if (kind == "onprem") return period == "year" ? Num(p.Prices, "onprem", "usd", "year") : null;
        return Num(p.Prices, "saas", "usd", period);
    }

    public sealed record QuoteRequest(string Kind, string Period, Dictionary<string, int> Plans, string Country, string StateCode, string Gstin);
    public sealed record QuoteLine(string Module, int PlanId, string Description, decimal Amount);
    public sealed record TaxLine(string Name, decimal Rate, decimal Amount);
    public sealed record Quote(string Currency, string Kind, string Period, List<QuoteLine> Lines, decimal Discount, decimal Subtotal, List<TaxLine> Taxes, decimal Tax, decimal Total,
        string TaxNote, Dictionary<string, Dictionary<string, int>> Licence);

    static readonly Dictionary<string, string> ModuleNames = new()
    { ["servicedesk"] = "Service Desk", ["assets"] = "Asset management", ["monitoring"] = "Monitoring", ["adsuite"] = "AD Manager", ["vault"] = "Vault", ["backup"] = "Backup" };
    public static string ModuleName(string m) => ModuleNames.TryGetValue(m, out var n) ? n : m;

    /// <summary>Works out an order. Returns the quote, or the reason it cannot be sold that way.</summary>
    public static (Quote? Quote, string? Error) Make(QuoteRequest r, List<Plan> plans, Business biz)
    {
        if (r.Kind is not ("saas" or "onprem")) return (null, "Choose SaaS or on-prem.");
        if (r.Period is not ("year" or "month")) return (null, "Choose yearly or monthly billing.");
        if (r.Kind == "onprem" && r.Period != "year") return (null, "On-prem plans are sold by the year.");
        if (r.Plans.Count == 0) return (null, "Choose at least one module.");
        var country = (r.Country ?? "").Trim().ToUpperInvariant();
        if (!Regex.IsMatch(country, "^[A-Z]{2}$")) return (null, "Choose your country.");
        var currency = country == "IN" ? "INR" : "USD";
        var lines = new List<QuoteLine>(); var licence = new Dictionary<string, Dictionary<string, int>>();
        foreach (var (module, planId) in r.Plans)
        {
            var p = plans.FirstOrDefault(x => x.Id == planId && x.Module == module);
            if (p == null) return (null, "That size is not on sale.");
            var price = Price(p, currency, r.Kind, r.Period);
            if (price == null) return (null, ModuleName(module) + " " + p.SizeLabel + " is not sold this way.");
            lines.Add(new QuoteLine(module, p.Id, ModuleName(module) + ", " + p.SizeLabel + (r.Kind == "onprem" ? ", on-prem" : "") + (r.Period == "year" ? ", 1 year" : ", 1 month"), price.Value));
            licence[module] = new Dictionary<string, int>(p.Limits);
        }
        if (licence.ContainsKey(Modules.Monitoring) && !licence.ContainsKey(Modules.Assets)) return (null, "Monitoring needs Asset management: add an Asset management size too.");
        var gross = lines.Sum(l => l.Amount);
        var discount = lines.Count >= 4 ? Math.Round(gross * 0.25m, 2) : 0m;   // suite discount: 4 or more modules
        var subtotal = gross - discount;
        var taxes = new List<TaxLine>(); var note = "";
        if (currency == "INR")
        {
            var buyerState = GstinOk(r.Gstin) ? r.Gstin[..2] : (r.StateCode ?? "").Trim();
            if (!Regex.IsMatch(buyerState, "^[0-9]{2}$")) return (null, "Choose your state.");
            if (buyerState == biz.StateCode && biz.StateCode != "")
            {
                taxes.Add(new TaxLine("CGST", 9, Math.Round(subtotal * 0.09m, 2)));
                taxes.Add(new TaxLine("SGST", 9, Math.Round(subtotal * 0.09m, 2)));
            }
            else taxes.Add(new TaxLine("IGST", 18, Math.Round(subtotal * 0.18m, 2)));
        }
        else note = "Export of services: supply meant for export under a Letter of Undertaking without payment of IGST" + (biz.Lut != "" ? " (LUT " + biz.Lut + ")" : "") + ". Any taxes in your country are yours to account for.";
        var tax = taxes.Sum(t => t.Amount);
        return (new Quote(currency, r.Kind, r.Period, lines, discount, subtotal, taxes, tax, subtotal + tax, note, licence), null);
    }

    public static string Money(decimal v, string currency) => (currency == "INR" ? "INR " + Indian(v) : "USD " + v.ToString("#,##0.00", CultureInfo.InvariantCulture));

    /// <summary>Indian grouping: 1,53,500.00.</summary>
    public static string Indian(decimal v)
    {
        var neg = v < 0; v = Math.Abs(Math.Round(v, 2));
        var whole = ((long)Math.Floor(v)).ToString(CultureInfo.InvariantCulture); var frac = ((int)Math.Round((v - Math.Floor(v)) * 100)).ToString("00");
        if (whole.Length > 3)
        {
            var head = whole[..^3]; var tail = whole[^3..]; var parts = new List<string>();
            while (head.Length > 2) { parts.Insert(0, head[^2..]); head = head[..^2]; }
            if (head.Length > 0) parts.Insert(0, head);
            whole = string.Join(",", parts) + "," + tail;
        }
        return (neg ? "-" : "") + whole + "." + frac;
    }
}

/// <summary>Razorpay (India): orders, checkout signatures and webhooks. Keys are kept in commerce.settings, the secrets sealed.</summary>
public static class Razorpay
{
    const string Purpose = "commerce.razorpay";
    public static HttpMessageHandler? TestHandler { get; set; }
    static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(30) };
    static HttpClient Client() => TestHandler != null ? new HttpClient(TestHandler, false) { Timeout = TimeSpan.FromSeconds(30) } : Shared;

    public sealed record Keys(string KeyId, string Secret, string WebhookSecret)
    {
        public bool Ready => KeyId != "" && Secret != "";
        public string Mode => KeyId.StartsWith("rzp_live_") ? "live" : "test";
    }
    sealed record Stored(string KeyId = "", string Secret = "", string WebhookSecret = "");

    public static async Task<Keys> LoadAsync(CommerceOptions o, IDataProtectionProvider dp, CancellationToken ct)
    {
        var raw = await CommerceDb.GetSettingAsync(o, "razorpay", ct);
        if (raw == null) return new Keys("", "", "");
        try
        {
            var s = JsonSerializer.Deserialize<Stored>(raw, Licences.Json) ?? new Stored();
            var p = dp.CreateProtector(Purpose);
            return new Keys(s.KeyId, s.Secret == "" ? "" : p.Unprotect(s.Secret), s.WebhookSecret == "" ? "" : p.Unprotect(s.WebhookSecret));
        }
        catch (Exception e) when (e is JsonException or CryptographicException) { return new Keys("", "", ""); }
    }

    /// <summary>Saves the keys. An empty secret keeps the one already saved.</summary>
    public static async Task SaveAsync(CommerceOptions o, IDataProtectionProvider dp, string keyId, string secret, string webhookSecret, CancellationToken ct)
    {
        var cur = await LoadAsync(o, dp, ct); var p = dp.CreateProtector(Purpose);
        var s = new Stored(keyId, p.Protect(secret == "" ? cur.Secret : secret), (webhookSecret == "" ? cur.WebhookSecret : webhookSecret) is { Length: > 0 } w ? p.Protect(w) : "");
        if ((secret == "" ? cur.Secret : secret) == "") s = s with { Secret = "" };
        await CommerceDb.SetSettingAsync(o, "razorpay", JsonSerializer.Serialize(s, Licences.Json), ct);
    }

    /// <summary>Creates a Razorpay order for an amount in the smallest unit (paise, cents). Returns its id.</summary>
    public static async Task<string> CreateOrderAsync(Keys k, long amount, string currency, string receipt, Dictionary<string, string> notes, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.razorpay.com/v1/orders")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { amount, currency, receipt, notes }), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(k.KeyId + ":" + k.Secret)));
        using var r = await Client().SendAsync(req, ct);
        var text = await r.Content.ReadAsStringAsync(ct);
        if (!r.IsSuccessStatusCode)
        {
            var detail = "";
            try { detail = JsonDocument.Parse(text).RootElement.GetProperty("error").GetProperty("description").GetString() ?? ""; } catch (Exception) { }
            throw new InvalidOperationException("Razorpay refused the order (" + (int)r.StatusCode + ")" + (detail == "" ? "." : ": " + detail));
        }
        return JsonDocument.Parse(text).RootElement.GetProperty("id").GetString() ?? throw new InvalidOperationException("Razorpay sent no order id.");
    }

    static string Hmac(string secret, string data) => Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    static bool Same(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes((b ?? "").ToLowerInvariant()));

    /// <summary>The signature Checkout returns: HMAC-SHA256 of "order_id|payment_id" with the key secret.</summary>
    public static bool PaymentSignatureOk(string secret, string orderId, string paymentId, string signature) => secret != "" && Same(Hmac(secret, orderId + "|" + paymentId), signature);
    /// <summary>The X-Razorpay-Signature header: HMAC-SHA256 of the raw body with the webhook secret.</summary>
    public static bool WebhookSignatureOk(string secret, string body, string signature) => secret != "" && Same(Hmac(secret, body), signature);
    public static string Sign(string secret, string data) => Hmac(secret, data);   // tests
}

/// <summary>Invoices: one numbered series per financial year (April to March), a PDF, kept with the order.</summary>
public static class Invoices
{
    /// <summary>The financial year of a date, as "2026-27".</summary>
    public static string Fy(DateTime d) { var y = d.Month >= 4 ? d.Year : d.Year - 1; return y + "-" + ((y + 1) % 100).ToString("00"); }

    public static async Task<string> NextNumberAsync(NpgsqlConnection db, NpgsqlTransaction? tx, string prefix, DateTime at, CancellationToken ct)
    {
        var fy = Fy(at);
        await using var q = new NpgsqlCommand("insert into commerce.invoice_series (fy, next) values (@f, 2) on conflict (fy) do update set next = commerce.invoice_series.next + 1 returning next - 1", db, tx);
        q.Parameters.AddWithValue("f", fy);
        var n = (int)(await q.ExecuteScalarAsync(ct))!;
        var p = Regex.Replace(prefix.ToUpperInvariant(), "[^A-Z0-9]", "");
        if (p.Length is 0 or > 4) p = "DA";
        return p + "/" + fy + "/" + n.ToString("00000");   // 16 characters with a 2-letter prefix (GST allows 16)
    }

    public sealed record Buyer(string Company, string Name, string Email, string Phone, string Address, string City, string StateCode, string StateName, string Pincode, string Country, string Gstin);

    static readonly string[] Ones = { "", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen" };
    static readonly string[] Tens = { "", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety" };
    static string Two(long n) => n < 20 ? Ones[n] : Tens[n / 10] + (n % 10 == 0 ? "" : " " + Ones[n % 10]);
    static string Three(long n) => (n >= 100 ? Ones[n / 100] + " Hundred" + (n % 100 == 0 ? "" : " ") : "") + Two(n % 100);

    /// <summary>An amount in words: Indian style (lakh, crore) for rupees, international for dollars.</summary>
    public static string Words(decimal amount, string currency)
    {
        var whole = (long)Math.Floor(amount); var cents = (int)Math.Round((amount - whole) * 100);
        string w;
        if (whole == 0) w = "Zero";
        else if (currency == "INR")
        {
            var parts = new List<string>();
            if (whole >= 10000000) { parts.Add(Three(whole / 10000000) + " Crore"); whole %= 10000000; }
            if (whole >= 100000) { parts.Add(Two(whole / 100000) + " Lakh"); whole %= 100000; }
            if (whole >= 1000) { parts.Add(Two(whole / 1000) + " Thousand"); whole %= 1000; }
            if (whole > 0) parts.Add(Three(whole));
            w = string.Join(" ", parts);
        }
        else
        {
            var parts = new List<string>();
            foreach (var (div, name) in new[] { (1_000_000_000L, " Billion"), (1_000_000L, " Million"), (1_000L, " Thousand") })
                if (whole >= div) { parts.Add(Three(whole / div) + name); whole %= div; }
            if (whole > 0) parts.Add(Three(whole));
            w = string.Join(" ", parts);
        }
        return currency == "INR" ? "Rupees " + w + (cents > 0 ? " and " + Two(cents) + " Paise" : "") + " Only"
                                 : "US Dollars " + w + (cents > 0 ? " and " + Two(cents) + " Cents" : "") + " Only";
    }

    /// <summary>The invoice PDF.</summary>
    public static byte[] Pdf(Business biz, string number, DateTime at, Buyer b, Store.Quote q, string paymentRef)
    {
        var pdf = new PdfDoc();
        pdf.Heading(q.Currency == "INR" ? "Tax Invoice" : "Invoice (export of services)", 16, rule: true);
        pdf.Paragraph(biz.LegalName, 11, bold: true);
        pdf.Paragraph(biz.Address + (biz.City != "" ? ", " + biz.City : "") + (biz.State != "" ? ", " + biz.State : "") + (biz.Pincode != "" ? " " + biz.Pincode : "") + ", " + biz.Country, 9);
        pdf.Paragraph("GSTIN " + biz.Gstin + (biz.Cin != "" ? "   CIN " + biz.Cin : "") + (biz.Pan != "" ? "   PAN " + biz.Pan : ""), 9);
        if (biz.SupportEmail != "") pdf.Paragraph(biz.SupportEmail + (biz.SupportPhone != "" ? "   " + biz.SupportPhone : ""), 9);
        pdf.Gap(8);
        pdf.Facts(new List<(string, string)>
        {
            ("Invoice number", number), ("Invoice date", at.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)),
            ("Place of supply", q.Currency == "INR" ? (b.StateName != "" ? b.StateName + " (" + b.StateCode + ")" : b.StateCode) : b.Country + " (outside India)"),
            ("Payment reference", paymentRef),
        });
        pdf.Gap(6);
        pdf.Paragraph("Bill to", 10, bold: true);
        pdf.Paragraph(b.Company, 10);
        pdf.Paragraph(string.Join(", ", new[] { b.Address, b.City, b.StateName, b.Pincode, b.Country }.Where(x => !string.IsNullOrWhiteSpace(x))), 9);
        if (b.Gstin != "") pdf.Paragraph("GSTIN " + b.Gstin, 9);
        pdf.Paragraph(b.Name + "  " + b.Email + (b.Phone != "" ? "  " + b.Phone : ""), 9);
        pdf.Gap(8);
        var rows = q.Lines.Select((l, i) => new[] { (i + 1).ToString(), l.Description, biz.Sac, Store.Money(l.Amount, q.Currency) }).ToList();
        if (q.Discount > 0) rows.Add(new[] { "", "Suite discount (25%, 4 or more modules)", "", "- " + Store.Money(q.Discount, q.Currency) });
        rows.Add(new[] { "", "Taxable value", "", Store.Money(q.Subtotal, q.Currency) });
        foreach (var t in q.Taxes) rows.Add(new[] { "", t.Name + " " + t.Rate.ToString("0.##", CultureInfo.InvariantCulture) + "%", "", Store.Money(t.Amount, q.Currency) });
        rows.Add(new[] { "", "Total", "", Store.Money(q.Total, q.Currency) });
        pdf.Table(new[] { "#", "Description", "SAC", "Amount" }, new[] { 0.06, 0.6, 0.12, 0.22 }, rows, 9);
        pdf.Gap(6);
        pdf.Paragraph("Amount in words: " + Words(q.Total, q.Currency), 9);
        if (q.TaxNote != "") pdf.Paragraph(q.TaxNote, 9);
        pdf.Gap(10);
        pdf.Paragraph("Paid in full. This is a computer-generated invoice and needs no signature.", 8);
        return pdf.ToBytes();
    }
}
