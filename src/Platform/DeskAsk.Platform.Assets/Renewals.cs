using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.Assets;

/// <summary>When reminders go out, and to whom.</summary>
public sealed class RenewalSettings
{
    public const string Key = "assets.renewals";
    public bool Enabled { get; set; }
    /// <summary>Days before the end date at which a reminder goes out: one month and 15 days unless changed.</summary>
    public int[] Days { get; set; } = { 30, 15 };
    /// <summary>Your own addresses (comma separated) that get every reminder.</summary>
    public string To { get; set; } = "";
    /// <summary>Also mail each company's contact about its own renewals.</summary>
    public bool Customers { get; set; } = true;
    /// <summary>Also mail the person an asset is assigned to about its warranty.</summary>
    public bool Users { get; set; } = true;
    /// <summary>Also mail the vendor (its renewal address, or its contact) about what was bought from it, unless the vendor is set not to be mailed.</summary>
    public bool Vendors { get; set; } = true;

    public static async Task<RenewalSettings> LoadAsync(ISettings s, CancellationToken ct)
    {
        var raw = await s.GetAsync(Key, ct); if (raw == null) return new RenewalSettings();
        try { return JsonSerializer.Deserialize<RenewalSettings>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new RenewalSettings(); } catch (JsonException) { return new RenewalSettings(); }
    }
}

/// <summary>One thing that ends soon: a warranty, an AMC contract or a rental, with the people to tell.</summary>
internal sealed record Renewal(string Kind, long Id, string Title, DateOnly Due, int? CompanyId, string Company, string Contact,
    string Team, string User, int? VendorId, string Vendor, string VendorEmail, string Detail, string Site);

internal static class RenewalQueries
{
    public static async Task<List<Renewal>> UpcomingAsync(NpgsqlConnection db, int withinDays, CancellationToken ct, IReadOnlyList<int>? scope = null)
    {
        var o = new List<Renewal>();
        const string vmail = "case when v.remind and v.active then coalesce(nullif(v.renewal_email, ''), v.contact_email) else '' end";
        string sql = $@"
            select 'warranty', a.id, a.tag || ' ' || a.name, a.warranty_end, a.company_id, coalesce(c.name,''), coalesce(c.contact_email,''), coalesce(c.renewal_to,''), a.assigned_to,
                   a.vendor_id, coalesce(v.name,''), coalesce({vmail}, ''),
                   concat_ws(', ', nullif(concat_ws(' ', nullif(a.manufacturer,''), nullif(a.model,'')), ''), case when a.serial <> '' then 'serial ' || a.serial end,
                     case when a.purchase_date is not null then 'bought ' || to_char(a.purchase_date, 'FMDD Mon YYYY') end, case when a.po_number <> '' then 'PO ' || a.po_number end,
                     case when a.invoice_number <> '' then 'invoice ' || a.invoice_number end),
                   coalesce(s.name, '')
              from assets.assets a left join assets.companies c on c.id = a.company_id left join assets.vendors v on v.id = a.vendor_id left join assets.places s on s.id = a.site_id
             where a.status not in ('retired','lost') and a.warranty_end between current_date and current_date + @d and (not @sc or a.company_id = any(@scope))
            union all
            select 'amc', m.id, m.title, m.end_date, m.company_id, coalesce(c.name,''), coalesce(c.contact_email,''), coalesce(c.renewal_to,''), '',
                   m.vendor_id, coalesce(v.name,''), coalesce({vmail}, ''),
                   concat_ws(', ', case when m.contract_no <> '' then 'contract ' || m.contract_no end, jsonb_array_length(m.asset_ids) || ' assets'), ''
              from assets.amc m left join assets.companies c on c.id = m.company_id left join assets.vendors v on v.id = m.vendor_id
             where m.end_date between current_date and current_date + @d and not exists (select 1 from assets.amc n where n.previous_id = m.id) and (not @sc or m.company_id = any(@scope))
            union all
            select 'rental', m.id, m.title, m.end_date, m.company_id, coalesce(c.name,''), coalesce(c.contact_email,''), coalesce(c.renewal_to,''), '', null::int, '', '', '', ''
              from assets.rentals m left join assets.companies c on c.id = m.company_id where m.end_date between current_date and current_date + @d and (not @sc or m.company_id = any(@scope))
            order by 4";
        await using var cmd = new NpgsqlCommand(sql, db); cmd.Parameters.AddWithValue("d", withinDays); AssetsAccess.Bind(cmd, scope);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            o.Add(new Renewal(rd.GetString(0), rd.GetInt64(1), rd.GetString(2), DateOnly.FromDateTime(rd.GetDateTime(3)), rd.IsDBNull(4) ? null : rd.GetInt32(4), rd.GetString(5), rd.GetString(6),
                rd.GetString(7), rd.GetString(8), rd.IsDBNull(9) ? null : rd.GetInt32(9), rd.GetString(10), rd.GetString(11), rd.GetString(12), rd.GetString(13)));
        return o;
    }
    public static string KindName(string k) => k switch { "warranty" => "Warranty", "amc" => "AMC contract", _ => "Rental" };
    public static IEnumerable<string> Split(string list) => list.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Contains('@'));

    /// <summary>One line of a reminder: what, which customer and site, when it ends, and the purchase details.</summary>
    public static string Line(Renewal r, bool forVendor) =>
        $"- {KindName(r.Kind)}: {r.Title}{(r.Detail != "" ? ", " + r.Detail : "")}{(r.Company != "" || r.Site != "" ? " (" + string.Join(", ", new[] { r.Company, r.Site }.Where(x => x != "")) + ")" : "")} ends {r.Due.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)}"
        + (!forVendor && r.Vendor != "" ? $", vendor {r.Vendor}" : "");
}

/// <summary>
/// Mails a reminder when a warranty, AMC contract or rental is about to end: to your team (everyone and the customer's own team), the
/// customer's contact, the asset's user and the vendor. Each reminder goes out once per end date and window, so running again changes nothing.
/// </summary>
public sealed class RenewalsJob : IJobHandler
{
    public const string JobType = "assets.renewals";
    public string Type => JobType;

    public async Task RunAsync(JobContext job, CancellationToken ct)
    {
        var settings = job.Services.GetService<ISettings>(); var mail = job.Services.GetService<IMailQueue>(); var users = job.Services.GetService<IUserDirectory>();
        if (settings == null || mail == null) return;
        var cfg = await RenewalSettings.LoadAsync(settings, ct);
        if (!cfg.Enabled || cfg.Days.Length == 0) return;
        await using var db = new NpgsqlConnection(job.Tenant.ConnectionString); await db.OpenAsync(ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var fresh = new List<(Renewal r, int window)>();
        foreach (var r in await RenewalQueries.UpcomingAsync(db, cfg.Days.Max(), ct))
        {
            var left = r.Due.DayNumber - today.DayNumber;
            var window = cfg.Days.Where(d => left <= d).DefaultIfEmpty(-1).Min(); if (window < 0) continue;   // the tightest window it has reached
            await using var c = new NpgsqlCommand("select 1 from assets.renewal_notices where kind = @k and ref_id = @i and due = @d and days = @w", db);
            c.Parameters.AddWithValue("k", r.Kind); c.Parameters.AddWithValue("i", r.Id); c.Parameters.Add(new NpgsqlParameter("d", NpgsqlTypes.NpgsqlDbType.Date) { Value = r.Due.ToDateTime(TimeOnly.MinValue) }); c.Parameters.AddWithValue("w", window);
            if (await c.ExecuteScalarAsync(ct) == null) fresh.Add((r, window));
        }
        if (fresh.Count == 0) return;

        // the person an asset is assigned to: an address, or the name of a DeskAsk user
        var people = users == null ? new List<UserRef>() : (await users.ListAsync(ct)).Where(u => u.Active && u.Email.Contains('@')).ToList();
        string UserMail(string assigned)
        {
            assigned = assigned.Trim(); if (assigned == "") return "";
            if (assigned.Contains('@') && !assigned.Contains(' ')) return assigned;
            return people.FirstOrDefault(u => string.Equals(u.Name, assigned, StringComparison.OrdinalIgnoreCase))?.Email ?? "";
        }

        // who gets what: our side (team, customer, user) and vendors get different letters; an address on our side does not also get the vendor's
        var ours = new Dictionary<string, (string Name, List<Renewal> Items)>(StringComparer.OrdinalIgnoreCase);
        var theirs = new Dictionary<string, (string Name, List<Renewal> Items)>(StringComparer.OrdinalIgnoreCase);
        void Add(Dictionary<string, (string Name, List<Renewal> Items)> d, string addr, string name, Renewal r)
        {
            addr = addr.Trim(); if (addr == "" || !addr.Contains('@')) return;
            if (!d.TryGetValue(addr, out var e)) d[addr] = e = (name, new());
            if (!e.Items.Contains(r)) e.Items.Add(r);
        }
        foreach (var (r, _) in fresh)
        {
            foreach (var a in RenewalQueries.Split(cfg.To)) Add(ours, a, "team", r);
            foreach (var a in RenewalQueries.Split(r.Team)) Add(ours, a, "team", r);
            if (cfg.Customers) foreach (var a in RenewalQueries.Split(r.Contact)) Add(ours, a, r.Company, r);
            if (cfg.Users && r.Kind == "warranty") { var um = UserMail(r.User); if (um != "") Add(ours, um, r.User.Contains('@') ? "" : r.User, r); }
            if (cfg.Vendors && r.VendorId != null) foreach (var a in RenewalQueries.Split(r.VendorEmail)) Add(theirs, a, r.Vendor, r);
        }
        foreach (var k in theirs.Keys.Where(ours.ContainsKey).ToList()) theirs.Remove(k);
        var replyTo = string.Join(", ", RenewalQueries.Split(cfg.To));

        var sent = false; var tried = 0;
        foreach (var (addr, (name, list)) in ours)
        {
            tried++;
            var items = list.OrderBy(x => x.Due).ToList();
            var values = new Dictionary<string, string>
            {
                ["name"] = name is "" or "team" ? "team" : name, ["items"] = string.Join("\n", items.Select(x => RenewalQueries.Line(x, false))), ["count"] = items.Count.ToString(CultureInfo.InvariantCulture),
                ["first"] = RenewalQueries.KindName(items[0].Kind) + " of " + items[0].Title, ["days"] = (items[0].Due.DayNumber - today.DayNumber).ToString(CultureInfo.InvariantCulture), ["path"] = "#/assets/renewals",
            };
            if (await mail.EnqueueTemplateAsync("asset_renewal", addr, values, "renewals", ct)) sent = true;
        }
        foreach (var (addr, (name, list)) in theirs)
        {
            tried++;
            var items = list.OrderBy(x => x.Due).ToList();
            var values = new Dictionary<string, string>
            {
                ["vendor"] = name, ["items"] = string.Join("\n", items.Select(x => RenewalQueries.Line(x, true))), ["count"] = items.Count.ToString(CultureInfo.InvariantCulture),
                ["first"] = RenewalQueries.KindName(items[0].Kind) + " of " + items[0].Title, ["days"] = (items[0].Due.DayNumber - today.DayNumber).ToString(CultureInfo.InvariantCulture),
                ["reply_to"] = replyTo == "" ? "this email" : replyTo,
            };
            if (await mail.EnqueueTemplateAsync("asset_renewal_vendor", addr, values, "renewals-vendor", ct)) sent = true;
        }
        if (!sent && tried > 0) return;   // mail is not set up yet: try again on the next run
        foreach (var (r, window) in fresh)
        {
            await using var c = new NpgsqlCommand("insert into assets.renewal_notices (kind, ref_id, due, days) values (@k,@i,@d,@w) on conflict do nothing", db);
            c.Parameters.AddWithValue("k", r.Kind); c.Parameters.AddWithValue("i", r.Id); c.Parameters.Add(new NpgsqlParameter("d", NpgsqlTypes.NpgsqlDbType.Date) { Value = r.Due.ToDateTime(TimeOnly.MinValue) }); c.Parameters.AddWithValue("w", window);
            await c.ExecuteNonQueryAsync(ct);
        }
    }
}

internal static class RenewalsApi
{
    public static void Map(IEndpointRouteBuilder r, string key)
    {
        RouteHandlerBuilder Admin(RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        Admin(r.MapGet("/api/assets/renewals/settings", async (ISettings s, CancellationToken ct) => Results.Ok(await RenewalSettings.LoadAsync(s, ct))));
        Admin(r.MapPut("/api/assets/renewals/settings", async (JsonElement b, ISettings s, IAuditLog audit, CancellationToken ct) =>
        {
            RenewalSettings? cfg;
            try { cfg = b.Deserialize<RenewalSettings>(opts); } catch (JsonException) { cfg = null; }
            if (cfg == null) return Results.BadRequest(new { error = "bad_input", detail = "That is not a valid reminder setting." });
            cfg.Days = cfg.Days.Where(d => d is >= 1 and <= 365).Distinct().OrderByDescending(d => d).ToArray();
            if (cfg.Enabled && cfg.Days.Length == 0) return Results.BadRequest(new { error = "bad_input", detail = "Give at least one number of days before the end date, between 1 and 365." });
            cfg.To = (cfg.To ?? "").Trim(); if (cfg.To.Length > 500) return Results.BadRequest(new { error = "bad_input", detail = "Too many addresses." });
            if (cfg.Enabled && cfg.To == "" && !cfg.Customers && !cfg.Users && !cfg.Vendors) return Results.BadRequest(new { error = "bad_input", detail = "Choose who gets the reminders: your own address, the customers, the users, the vendors, or several." });
            foreach (var a in cfg.To.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)) if (!a.Contains('@')) return Results.BadRequest(new { error = "bad_input", detail = $"\"{a}\" is not an email address." });
            await s.SetAsync(RenewalSettings.Key, JsonSerializer.Serialize(cfg, opts), ct);
            await audit.WriteAsync("assets.renewals_settings", cfg.Enabled ? "on" : "off", string.Join(",", cfg.Days), ct: ct);
            return Results.Ok(cfg);
        }));
        // staff see every customer; a customer administrator sees their own company's
        r.MapGet("/api/assets/renewals/upcoming", async (int? days, HttpContext http, ITenantContext t, ISettings s, CancellationToken ct) =>
        {
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read);
            await using var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct);
            var list = await RenewalQueries.UpcomingAsync(db, Math.Clamp(days ?? 90, 1, 365), ct, scope);
            var sent = new Dictionary<(string, long), List<int>>();
            await using (var c = new NpgsqlCommand("select kind, ref_id, days from assets.renewal_notices where due >= current_date", db))
            await using (var rd = await c.ExecuteReaderAsync(ct)) while (await rd.ReadAsync(ct)) { var k = (rd.GetString(0), rd.GetInt64(1)); if (!sent.TryGetValue(k, out var l)) sent[k] = l = new(); l.Add(rd.GetInt32(2)); }
            var cfg = await RenewalSettings.LoadAsync(s, ct);
            return Results.Ok(new { items = list.Select(x => new
            {
                kind = x.Kind, id = x.Id, title = x.Title, due = x.Due.ToString("yyyy-MM-dd"), companyId = x.CompanyId, company = x.Company, daysLeft = x.Due.DayNumber - DateOnly.FromDateTime(DateTime.UtcNow).DayNumber,
                vendorId = x.VendorId, vendor = x.Vendor, detail = x.Detail, site = x.Site, remindersSent = sent.GetValueOrDefault((x.Kind, x.Id)) ?? new List<int>(),
                vendorMailed = cfg.Vendors && x.VendorEmail != "",
            }) });
        }).RequireAuthorization(p => p.RequireRole(AssetsAccess.Readers)).RequireModule(key).RequireAccess(key, Acts.Read);
        Admin(r.MapPost("/api/assets/renewals/run", async (IJobQueue jobs, CancellationToken ct) =>
        {
            await jobs.EnqueueAsync(RenewalsJob.JobType, "{}", null, "assets.renewals", ct);
            return Results.Ok(new { ok = true });
        }));
    }
}
