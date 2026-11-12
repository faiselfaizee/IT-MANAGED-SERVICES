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
    /// <summary>Days before the end date at which a reminder goes out.</summary>
    public int[] Days { get; set; } = { 30, 7 };
    /// <summary>Your own addresses (comma separated) that get every reminder.</summary>
    public string To { get; set; } = "";
    /// <summary>Also mail each company's contact about its own renewals.</summary>
    public bool Customers { get; set; } = true;

    public static async Task<RenewalSettings> LoadAsync(ISettings s, CancellationToken ct)
    {
        var raw = await s.GetAsync(Key, ct); if (raw == null) return new RenewalSettings();
        try { return JsonSerializer.Deserialize<RenewalSettings>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? new RenewalSettings(); } catch (JsonException) { return new RenewalSettings(); }
    }
}

/// <summary>One thing that ends soon: a warranty, an AMC contract or a rental.</summary>
internal sealed record Renewal(string Kind, long Id, string Title, DateOnly Due, int? CompanyId, string Company, string Contact);

internal static class RenewalQueries
{
    public static async Task<List<Renewal>> UpcomingAsync(NpgsqlConnection db, int withinDays, CancellationToken ct)
    {
        var o = new List<Renewal>();
        const string sql = @"
            select 'warranty', a.id, a.tag || ' ' || a.name, a.warranty_end, a.company_id, coalesce(c.name,''), coalesce(c.contact_email,'')
              from assets.assets a left join assets.companies c on c.id = a.company_id
             where a.status not in ('retired','lost') and a.warranty_end between current_date and current_date + @d
            union all
            select 'amc', m.id, m.title, m.end_date, m.company_id, coalesce(c.name,''), coalesce(c.contact_email,'')
              from assets.amc m left join assets.companies c on c.id = m.company_id where m.end_date between current_date and current_date + @d
            union all
            select 'rental', m.id, m.title, m.end_date, m.company_id, coalesce(c.name,''), coalesce(c.contact_email,'')
              from assets.rentals m left join assets.companies c on c.id = m.company_id where m.end_date between current_date and current_date + @d
            order by 4";
        await using var cmd = new NpgsqlCommand(sql, db); cmd.Parameters.AddWithValue("d", withinDays);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) o.Add(new Renewal(rd.GetString(0), rd.GetInt64(1), rd.GetString(2), DateOnly.FromDateTime(rd.GetDateTime(3)), rd.IsDBNull(4) ? null : rd.GetInt32(4), rd.GetString(5), rd.GetString(6)));
        return o;
    }
    public static string KindName(string k) => k switch { "warranty" => "Warranty", "amc" => "AMC contract", _ => "Rental" };
}

/// <summary>Mails a reminder when a warranty, AMC contract or rental is about to end. Each reminder goes out once per end date and window, so running again changes nothing.</summary>
public sealed class RenewalsJob : IJobHandler
{
    public const string JobType = "assets.renewals";
    public string Type => JobType;

    public async Task RunAsync(JobContext job, CancellationToken ct)
    {
        var settings = job.Services.GetService<ISettings>(); var mail = job.Services.GetService<IMailQueue>();
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
        var to = new Dictionary<string, List<Renewal>>(StringComparer.OrdinalIgnoreCase);
        void Add(string addr, Renewal r) { addr = addr.Trim(); if (addr == "") return; if (!to.TryGetValue(addr, out var l)) to[addr] = l = new(); l.Add(r); }
        foreach (var (r, _) in fresh)
        {
            foreach (var a in cfg.To.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)) Add(a, r);
            if (cfg.Customers && r.Contact != "") Add(r.Contact, r);
        }
        var sent = false;
        foreach (var (addr, list) in to)
        {
            var sb = new StringBuilder(list.Count == 1 ? "This is coming to an end soon:\n\n" : "These are coming to an end soon:\n\n");
            foreach (var r in list.OrderBy(x => x.Due)) sb.Append("- ").Append(RenewalQueries.KindName(r.Kind)).Append(": ").Append(r.Title).Append(r.Company != "" ? " (" + r.Company + ")" : "").Append(" ends ").Append(r.Due.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("\nPlease arrange the renewal in good time.");
            if (await mail.EnqueueAsync(addr, list.Count == 1 ? "Renewal due: " + list[0].Title : list.Count + " renewals are due soon", sb.ToString(), "renewals", ct)) sent = true;
        }
        if (!sent && to.Count > 0) return;   // mail is not set up yet: try again on the next run
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
            if (cfg.Enabled && cfg.To == "" && !cfg.Customers) return Results.BadRequest(new { error = "bad_input", detail = "Choose who gets the reminders: your own address, the customers, or both." });
            await s.SetAsync(RenewalSettings.Key, JsonSerializer.Serialize(cfg, opts), ct);
            await audit.WriteAsync("assets.renewals_settings", cfg.Enabled ? "on" : "off", string.Join(",", cfg.Days), ct: ct);
            return Results.Ok(cfg);
        }));
        r.MapGet("/api/assets/renewals/upcoming", async (int? days, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct);
            var list = await RenewalQueries.UpcomingAsync(db, Math.Clamp(days ?? 90, 1, 365), ct);
            return Results.Ok(new { items = list.Select(x => new { kind = x.Kind, id = x.Id, title = x.Title, due = x.Due.ToString("yyyy-MM-dd"), companyId = x.CompanyId, company = x.Company, daysLeft = x.Due.DayNumber - DateOnly.FromDateTime(DateTime.UtcNow).DayNumber }) });
        }).RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Read, allCompanies: true);
        Admin(r.MapPost("/api/assets/renewals/run", async (IJobQueue jobs, CancellationToken ct) =>
        {
            await jobs.EnqueueAsync(RenewalsJob.JobType, "{}", null, "assets.renewals", ct);
            return Results.Ok(new { ok = true });
        }));
    }
}
