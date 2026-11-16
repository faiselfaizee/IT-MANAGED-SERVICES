using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.Backup;

/// <summary>Backup screens: customer tiles and rings, the job list, a job with its runs, settings, report senders, report keys, and the report address for scripts.</summary>
public static class BackupApi
{
    public static readonly string[] Statuses = { "success", "warning", "failed", "missed", "waiting", "ignored" };

    public sealed record Job(long Id, int? CompanyId, string Company, long? AssetId, string AssetName, string Tool, string Name, string Source, int EveryHours, bool Watched,
        string Notes, string LastResult, DateTime? LastAt, DateTime? LastSuccessAt, string LastMessage, long LastBytes, int Fails, string Alert, string TicketNumber,
        DateTime Created, string Status);

    static async Task<List<Job>> Load(NpgsqlConnection db, IReadOnlyList<int>? scope, long? id, int graceHours, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand(@"select id, company_id, company, asset_id, asset_name, tool, name, source, every_hours, watched, notes, last_result, last_at,
                last_success_at, last_message, last_bytes, fails, alert, ticket_number, created_at
            from backup.jobs where (not @sc or company_id = any(@scope)) and (@id = 0 or id = @id) order by company, asset_name, name", db);
        BackupModule.P(c, "sc", scope != null); BackupModule.P(c, "scope", (scope ?? Array.Empty<int>()).ToArray()); BackupModule.P(c, "id", id ?? 0);
        var o = new List<Job>(); var now = DateTime.UtcNow;
        await using var rd = await c.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            DateTime? D(int i) => rd.IsDBNull(i) ? null : DateTime.SpecifyKind(rd.GetDateTime(i), DateTimeKind.Utc);
            var watched = rd.GetBoolean(9); var every = rd.GetInt32(8); var last = rd.GetString(11); var lastAt = D(12); var created = DateTime.SpecifyKind(rd.GetDateTime(19), DateTimeKind.Utc);
            o.Add(new Job(rd.GetInt64(0), rd.IsDBNull(1) ? null : rd.GetInt32(1), rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetInt64(3), rd.GetString(4), rd.GetString(5), rd.GetString(6),
                rd.GetString(7), every, watched, rd.GetString(10), last, lastAt, D(13), rd.GetString(14), rd.GetInt64(15), rd.GetInt32(16), rd.GetString(17), rd.GetString(18), created,
                BackupChecker.Status(watched, every, last, lastAt, created, graceHours, now)));
        }
        return o;
    }

    /// <summary>within 1 day, 1 to 7 days, older, never (jobs not watched are left out).</summary>
    public static string Age(Job j, DateTime now) => j.LastSuccessAt is not DateTime t ? "never" : t > now.AddDays(-1) ? "day" : t > now.AddDays(-7) ? "week" : "older";

    static Dictionary<string, object?> Summary(int? companyId, string company, IReadOnlyCollection<Job> jobs, int devicesWithTools)
    {
        var now = DateTime.UtcNow; var w = jobs.Where(j => j.Watched).ToList();
        int S(string s) => jobs.Count(j => j.Status == s);
        return new()
        {
            ["companyId"] = companyId, ["company"] = company, ["jobs"] = jobs.Count, ["devices"] = jobs.Where(j => j.AssetId != null).Select(j => j.AssetId).Distinct().Count(),
            ["devicesWithTools"] = devicesWithTools,
            ["success"] = S("success"), ["warning"] = S("warning"), ["failed"] = S("failed"), ["missed"] = S("missed"), ["waiting"] = S("waiting"), ["ignored"] = S("ignored"),
            ["ageDay"] = w.Count(j => Age(j, now) == "day"), ["ageWeek"] = w.Count(j => Age(j, now) == "week"), ["ageOlder"] = w.Count(j => Age(j, now) == "older"), ["ageNever"] = w.Count(j => Age(j, now) == "never"),
            ["byTool"] = jobs.GroupBy(j => j.Tool).ToDictionary(g => g.Key, g => g.Count()),
            ["alerts"] = jobs.Count(j => j.Alert != ""),
        };
    }

    static int Worst(Dictionary<string, object?> s) => (int)s["failed"]! * 1000 + (int)s["missed"]! * 100 + (int)s["warning"]! * 10;

    static object Shape(Job j) => new
    {
        id = j.Id, companyId = j.CompanyId, company = j.Company, assetId = j.AssetId, assetName = j.AssetName, tool = j.Tool, toolName = BackupModule.ToolNames.GetValueOrDefault(j.Tool, j.Tool),
        name = j.Name, source = j.Source, everyHours = j.EveryHours, watched = j.Watched, notes = j.Notes, lastResult = j.LastResult, lastAt = j.LastAt, lastSuccessAt = j.LastSuccessAt,
        lastMessage = j.LastMessage, lastBytes = j.LastBytes, fails = j.Fails, alert = j.Alert, ticketNumber = j.TicketNumber, created = j.Created, status = j.Status,
    };

    static async Task<Dictionary<long, (string Tools, DateTime Seen)>> ToolsSeen(NpgsqlConnection db, CancellationToken ct)
    {
        var o = new Dictionary<long, (string, DateTime)>();
        await using var c = new NpgsqlCommand("select asset_id, string_agg(tool, ',' order by tool), max(seen_at) from backup.tools where seen_at > now() - interval '7 days' group by asset_id", db);
        await using var rd = await c.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) o[rd.GetInt64(0)] = (rd.GetString(1), rd.GetDateTime(2));
        return o;
    }

    static async Task<IReadOnlyList<int>?> Scope(HttpContext http, string act, CancellationToken ct) =>
        await http.RequestServices.GetRequiredService<IAccess>().ScopeAsync(http.User, Modules.Backup, act, ct);

    static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    static string? Result(string? r) => (r ?? "").Trim().ToLowerInvariant() switch
    {
        "success" or "succeeded" or "successful" or "ok" or "completed" or "0" => "success",
        "warning" or "warn" or "partial" or "1" => "warning",
        "failed" or "fail" or "failure" or "error" or "2" => "failed",
        _ => null,
    };

    static DateTime? When(JsonElement b, string n) =>
        b.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
    static string Str(JsonElement b, string n) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        RouteHandlerBuilder Read(RouteHandlerBuilder b) => b.RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Read);
        RouteHandlerBuilder Modify(RouteHandlerBuilder b) => b.RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Modify);
        RouteHandlerBuilder Admin(RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);

        // every customer this person may see as a tile, and all of them together
        Read(r.MapGet("/api/backup/dashboard", async (HttpContext http, ITenantContext t, ISettings settings, CancellationToken ct) =>
        {
            var scope = await Scope(http, Acts.Read, ct);
            var s = await BackupSettings.LoadAsync(settings, ct);
            await using var db = await BackupModule.Open(t, ct);
            var jobs = await Load(db, scope, null, s.GraceHours, ct);
            var seen = await ToolsSeen(db, ct);
            var devices = await http.RequestServices.GetRequiredService<IDeviceHealth>().SnapshotAsync(scope, null, ct);
            var seenBy = devices.Where(d => seen.ContainsKey(d.Id)).ToList();
            var tiles = jobs.GroupBy(j => (j.CompanyId, j.Company))
                .Select(g => Summary(g.Key.CompanyId, g.Key.Company == "" ? "No customer" : g.Key.Company, g.ToList(), seenBy.Count(d => d.CompanyId == g.Key.CompanyId)))
                .OrderByDescending(Worst).ThenBy(x => (string)x["company"]!).ToList();
            return Results.Ok(new { all = Summary(null, "All customers", jobs, seenBy.Count), tiles, restricted = scope != null });
        }));

        Read(r.MapGet("/api/backup/jobs", async (HttpContext http, ITenantContext t, ISettings settings, CancellationToken ct) =>
        {
            string Q(string n) => http.Request.Query[n].ToString().Trim();
            var status = Q("status"); if (status != "" && status != "alert" && !Statuses.Contains(status)) return BackupModule.Bad("bad_input", "status must be success, warning, failed, missed, waiting, ignored or alert");
            var tool = Q("tool"); if (tool != "" && !BackupModule.Tools.Contains(tool)) return BackupModule.Bad("bad_input", "tool");
            var age = Q("age"); if (age is not ("" or "day" or "week" or "older" or "never")) return BackupModule.Bad("bad_input", "age must be day, week, older or never");
            var company = Q("company"); if (company != "" && company != "none" && !int.TryParse(company, out _)) return BackupModule.Bad("bad_input", "company must be a customer id or none");
            long.TryParse(Q("asset"), out var asset); var q = Q("q");
            var scope = await Scope(http, Acts.Read, ct);
            var s = await BackupSettings.LoadAsync(settings, ct);
            await using var db = await BackupModule.Open(t, ct);
            var now = DateTime.UtcNow;
            var list = (await Load(db, scope, null, s.GraceHours, ct)).Where(j =>
                (status == "" || (status == "alert" ? j.Alert != "" : j.Status == status)) && (tool == "" || j.Tool == tool) &&
                (age == "" || (j.Watched && Age(j, now) == age)) &&
                (company == "" || (company == "none" ? j.CompanyId == null : j.CompanyId == int.Parse(company))) && (asset == 0 || j.AssetId == asset) &&
                (q == "" || j.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || j.AssetName.Contains(q, StringComparison.OrdinalIgnoreCase) || j.Company.Contains(q, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(j => Array.IndexOf(new[] { "failed", "missed", "warning", "waiting", "success", "ignored" }, j.Status)).ThenBy(j => j.Company).ThenBy(j => j.Name).ToList();
            return Results.Ok(new { total = list.Count, items = list.Take(1000).Select(Shape) });
        }));

        // devices where the agent found backup software in the last week, with how many jobs they report
        Read(r.MapGet("/api/backup/devices", async (HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            var scope = await Scope(http, Acts.Read, ct);
            await using var db = await BackupModule.Open(t, ct);
            var seen = await ToolsSeen(db, ct);
            var counts = new Dictionary<long, int>();
            await using (var c = new NpgsqlCommand("select asset_id, count(*)::int from backup.jobs where asset_id is not null group by asset_id", db))
            await using (var rd = await c.ExecuteReaderAsync(ct)) while (await rd.ReadAsync(ct)) counts[rd.GetInt64(0)] = rd.GetInt32(1);
            var devices = await http.RequestServices.GetRequiredService<IDeviceHealth>().SnapshotAsync(scope, null, ct);
            return Results.Ok(devices.Where(d => seen.ContainsKey(d.Id)).OrderBy(d => d.Company).ThenBy(d => d.Name).Select(d => new
            {
                assetId = d.Id, name = d.Name, tag = d.Tag, company = d.Company, companyId = d.CompanyId,
                tools = seen[d.Id].Tools.Split(',').Select(x => new { key = x, name = BackupModule.ToolNames.GetValueOrDefault(x, x) }), seenAt = seen[d.Id].Seen,
                jobs = counts.GetValueOrDefault(d.Id),
            }));
        }));

        Read(r.MapGet("/api/backup/jobs/{id:long}", async (long id, HttpContext http, ITenantContext t, ISettings settings, CancellationToken ct) =>
        {
            var scope = await Scope(http, Acts.Read, ct);
            var s = await BackupSettings.LoadAsync(settings, ct);
            await using var db = await BackupModule.Open(t, ct);
            var j = (await Load(db, scope, id, s.GraceHours, ct)).FirstOrDefault(); if (j == null) return Results.NotFound();
            var runs = new List<object>();
            await using (var c = new NpgsqlCommand("select id, started, ended, result, message, bytes from backup.runs where job_id = @j order by ended desc limit 100", db))
            {
                BackupModule.P(c, "j", id); await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    DateTime? st = rd.IsDBNull(1) ? null : DateTime.SpecifyKind(rd.GetDateTime(1), DateTimeKind.Utc); var en = DateTime.SpecifyKind(rd.GetDateTime(2), DateTimeKind.Utc);
                    runs.Add(new { id = rd.GetInt64(0), started = st, ended = en, minutes = st is DateTime s0 && en > s0 ? (int)Math.Round((en - s0).TotalMinutes) : (int?)null, result = rd.GetString(3), message = rd.GetString(4), bytes = rd.GetInt64(5) });
                }
            }
            int ok = 0, warn = 0, bad = 0;
            await using (var c = new NpgsqlCommand("select result, count(*)::int from backup.runs where job_id = @j and ended > now() - interval '30 days' group by result", db))
            {
                BackupModule.P(c, "j", id); await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) { var n = rd.GetInt32(1); switch (rd.GetString(0)) { case "success": ok = n; break; case "warning": warn = n; break; default: bad = n; break; } }
            }
            return Results.Ok(new { job = Shape(j), runs, month = new { success = ok, warning = warn, failed = bad }, graceHours = s.GraceHours });
        }));

        Modify(r.MapPut("/api/backup/jobs/{id:long}", async (long id, JsonElement b, HttpContext http, ITenantContext t, ISettings settings, IAuditLog audit, CancellationToken ct) =>
        {
            var scope = await Scope(http, Acts.Modify, ct);
            await using var db = await BackupModule.Open(t, ct);
            var j = (await Load(db, scope, id, 0, ct)).FirstOrDefault(); if (j == null) return Results.NotFound();
            var every = j.EveryHours; var watched = j.Watched; var notes = j.Notes;
            if (b.TryGetProperty("everyHours", out var ev)) { if (ev.ValueKind != JsonValueKind.Number || !ev.TryGetInt32(out every) || every is < 0 or > 744) return BackupModule.Bad("bad_input", "Expected every 1 to 744 hours, or 0 for no schedule."); }
            if (b.TryGetProperty("watched", out var wv)) { if (wv.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return BackupModule.Bad("bad_input", "watched must be true or false"); watched = wv.GetBoolean(); }
            if (b.TryGetProperty("notes", out var nv) && nv.ValueKind == JsonValueKind.String) notes = BackupModule.Clip(nv.GetString(), 2000);
            await using (var c = new NpgsqlCommand("update backup.jobs set every_hours = @e, watched = @w, notes = @n where id = @i", db))
            { BackupModule.P(c, "e", every); BackupModule.P(c, "w", watched); BackupModule.P(c, "n", notes); BackupModule.P(c, "i", id); await c.ExecuteNonQueryAsync(ct); }
            await audit.WriteAsync("backup.job_changed", j.Name, $"every {every} h, {(watched ? "watched" : "not watched")}", null, ct);
            await BackupChecker.RunAsync(http.RequestServices, ct);
            return Results.Ok(new { ok = true });
        }));

        Modify(r.MapDelete("/api/backup/jobs/{id:long}", async (long id, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var scope = await Scope(http, Acts.Modify, ct);
            await using var db = await BackupModule.Open(t, ct);
            var j = (await Load(db, scope, id, 0, ct)).FirstOrDefault(); if (j == null) return Results.NotFound();
            await using (var c = new NpgsqlCommand("delete from backup.jobs where id = @i", db)) { BackupModule.P(c, "i", id); await c.ExecuteNonQueryAsync(ct); }
            await audit.WriteAsync("backup.job_deleted", j.Name, $"{j.Company} / {j.AssetName}", null, ct);
            return Results.Ok(new { ok = true });
        }));

        Modify(r.MapPost("/api/backup/check", async (HttpContext http, CancellationToken ct) => { await BackupChecker.RunAsync(http.RequestServices, ct); return Results.Ok(new { ok = true }); }));

        // ---------------------------------------------------------------- settings
        Admin(r.MapGet("/api/backup/settings", async (ISettings s, CancellationToken ct) => Results.Ok(await BackupSettings.LoadAsync(s, ct))));
        Admin(r.MapPut("/api/backup/settings", async (JsonElement b, ISettings s, ICompanyDirectory dir, IAuditLog audit, CancellationToken ct) =>
        {
            BackupSettings? n;
            try { n = b.Deserialize<BackupSettings>(new JsonSerializerOptions(JsonSerializerDefaults.Web)); } catch (JsonException) { return BackupModule.Bad("bad_input", "Settings could not be read."); }
            if (n == null) return BackupModule.Bad("bad_input");
            if (n.FailuresBeforeAlert is < 1 or > 20) return BackupModule.Bad("bad_input", "Alert after 1 to 20 failed runs in a row.");
            if (n.GraceHours is < 0 or > 72) return BackupModule.Bad("bad_input", "Grace is 0 to 72 hours.");
            if (n.TicketPriority is not ("low" or "medium" or "high" or "critical")) return BackupModule.Bad("bad_input", "Ticket priority must be low, medium, high or critical.");
            foreach (var list in n.Companies.Values.Append(n.Recipients))
                foreach (var a in BackupSettings.Split(list))
                    if (!System.Net.Mail.MailAddress.TryCreate(a, out _)) return BackupModule.Bad("bad_input", $"\"{a}\" is not an email address.");
            foreach (var k in n.Companies.Keys.ToList())
            {
                if (!int.TryParse(k, out var ci) || await dir.GetAsync(ci, ct) == null) return BackupModule.Bad("bad_input", "That customer does not exist.");
                if (n.Companies[k].Trim() == "") n.Companies.Remove(k);
            }
            await n.SaveAsync(s, ct);
            await audit.WriteAsync("backup.settings", "", "", null, ct);
            return Results.Ok(n);
        }));

        // ---------------------------------------------------------------- report mail senders
        Admin(r.MapGet("/api/backup/senders", async (ITenantContext t, ICompanyDirectory dir, CancellationToken ct) =>
        {
            var names = (await dir.ListAsync(ct)).ToDictionary(c => c.Id, c => c.Name);
            await using var db = await BackupModule.Open(t, ct);
            await using var c = new NpgsqlCommand("select id, pattern, company_id, tool, created_at from backup.senders order by pattern", db);
            var o = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                int? co = rd.IsDBNull(2) ? null : rd.GetInt32(2);
                o.Add(new { id = rd.GetInt32(0), pattern = rd.GetString(1), companyId = co, company = co is int x ? names.GetValueOrDefault(x, "") : "", tool = rd.GetString(3), created = rd.GetDateTime(4) });
            }
            return Results.Ok(o);
        }));
        Admin(r.MapPost("/api/backup/senders", async (JsonElement b, ITenantContext t, ICompanyDirectory dir, IAuditLog audit, CancellationToken ct) =>
        {
            var pattern = Str(b, "pattern").Trim().ToLowerInvariant();
            if (pattern.StartsWith("*@")) pattern = pattern[1..];
            var okPattern = pattern.StartsWith('@') ? pattern.Length > 3 && pattern.IndexOf('.') > 1 && !pattern[1..].Contains('@') : System.Net.Mail.MailAddress.TryCreate(pattern, out _);
            if (!okPattern) return BackupModule.Bad("bad_input", "Give a sender address (backup@nas.example.com) or a whole domain (@example.com).");
            int? company = b.TryGetProperty("companyId", out var cv) && cv.ValueKind == JsonValueKind.Number ? cv.GetInt32() : null;
            if (company is int ci && await dir.GetAsync(ci, ct) == null) return BackupModule.Bad("bad_input", "That customer does not exist.");
            var tool = Str(b, "tool"); if (tool != "" && !BackupModule.Tools.Contains(tool)) return BackupModule.Bad("bad_input", "tool");
            await using var db = await BackupModule.Open(t, ct);
            await using var c = new NpgsqlCommand("insert into backup.senders (pattern, company_id, tool) values (@p, @c, @t) on conflict (pattern) do nothing returning id", db);
            BackupModule.P(c, "p", pattern); BackupModule.P(c, "c", company); BackupModule.P(c, "t", tool);
            if (await c.ExecuteScalarAsync(ct) is not int id) return BackupModule.Bad("exists", "That sender is already listed.");
            await audit.WriteAsync("backup.sender_added", pattern, "", null, ct);
            return Results.Ok(new { id });
        }));
        Admin(r.MapDelete("/api/backup/senders/{id:int}", async (int id, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await BackupModule.Open(t, ct);
            await using var c = new NpgsqlCommand("delete from backup.senders where id = @i returning pattern", db); BackupModule.P(c, "i", id);
            if (await c.ExecuteScalarAsync(ct) is not string p) return Results.NotFound();
            await audit.WriteAsync("backup.sender_removed", p, "", null, ct);
            return Results.Ok(new { ok = true });
        }));

        // ---------------------------------------------------------------- report keys (for scripts and tools that can call a web address)
        Admin(r.MapGet("/api/backup/keys", async (ITenantContext t, ICompanyDirectory dir, CancellationToken ct) =>
        {
            var names = (await dir.ListAsync(ct)).ToDictionary(c => c.Id, c => c.Name);
            await using var db = await BackupModule.Open(t, ct);
            await using var c = new NpgsqlCommand("select id, name, company_id, prefix, created_at, last_used from backup.keys order by name", db);
            var o = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                int? co = rd.IsDBNull(2) ? null : rd.GetInt32(2);
                o.Add(new { id = rd.GetInt32(0), name = rd.GetString(1), companyId = co, company = co is int x ? names.GetValueOrDefault(x, "") : "", prefix = rd.GetString(3), created = rd.GetDateTime(4), lastUsed = rd.IsDBNull(5) ? (DateTime?)null : rd.GetDateTime(5) });
            }
            return Results.Ok(o);
        }));
        Admin(r.MapPost("/api/backup/keys", async (JsonElement b, ITenantContext t, ICompanyDirectory dir, IAuditLog audit, CancellationToken ct) =>
        {
            var name = BackupModule.Clip(Str(b, "name"), 120); if (name == "") return BackupModule.Bad("bad_input", "Give the key a name.");
            int? company = b.TryGetProperty("companyId", out var cv) && cv.ValueKind == JsonValueKind.Number ? cv.GetInt32() : null;
            if (company is int ci && await dir.GetAsync(ci, ct) == null) return BackupModule.Bad("bad_input", "That customer does not exist.");
            var token = "dbk_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(30)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            await using var db = await BackupModule.Open(t, ct);
            await using var c = new NpgsqlCommand("insert into backup.keys (name, company_id, hash, prefix) values (@n, @c, @h, @p) returning id", db);
            BackupModule.P(c, "n", name); BackupModule.P(c, "c", company); BackupModule.P(c, "h", Hash(token)); BackupModule.P(c, "p", token[..10]);
            var id = (int)(await c.ExecuteScalarAsync(ct))!;
            await audit.WriteAsync("backup.key_created", name, "", null, ct);
            return Results.Ok(new { id, token });   // shown once; only its hash is kept
        }));
        Admin(r.MapDelete("/api/backup/keys/{id:int}", async (int id, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await BackupModule.Open(t, ct);
            await using var c = new NpgsqlCommand("delete from backup.keys where id = @i returning name", db); BackupModule.P(c, "i", id);
            if (await c.ExecuteScalarAsync(ct) is not string n) return Results.NotFound();
            await audit.WriteAsync("backup.key_revoked", n, "", null, ct);
            return Results.Ok(new { ok = true });
        }));

        // ---------------------------------------------------------------- the report address: POST with "Authorization: Bearer <key>"
        r.MapPost("/api/public/backup/report", async (JsonElement b, HttpContext http, ITenantContext t, ICompanyDirectory dir, CancellationToken ct) =>
        {
            var auth = http.Request.Headers.Authorization.ToString();
            var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : "";
            if (!token.StartsWith("dbk_") || token.Length > 100) return Results.Json(new { error = "key" }, statusCode: 401);
            await using var db = await BackupModule.Open(t, ct);
            int? company;
            await using (var c = new NpgsqlCommand("update backup.keys set last_used = now() where hash = @h returning company_id", db))
            {
                BackupModule.P(c, "h", Hash(token));
                var got = await c.ExecuteScalarAsync(ct);
                if (got == null) return Results.Json(new { error = "key" }, statusCode: 401);
                company = got is int ci ? ci : null;
            }
            if (b.ValueKind != JsonValueKind.Object) return BackupModule.Bad("bad_input", "Send a JSON object.");
            var job = BackupModule.Clip(Str(b, "job"), 200); if (job == "") return BackupModule.Bad("bad_input", "job is required");
            var result = Result(Str(b, "result")); if (result == null) return BackupModule.Bad("bad_input", "result must be success, warning or failed");
            var device = BackupModule.Clip(Str(b, "device"), 200);
            long? assetId = null; var assetName = device;
            if (device != "")
            {
                var devices = await http.RequestServices.GetRequiredService<IDeviceHealth>().SnapshotAsync(company is int c0 ? new[] { c0 } : null, null, ct);
                var hit = devices.FirstOrDefault(d => string.Equals(d.Name, device, StringComparison.OrdinalIgnoreCase) || string.Equals(d.Tag, device, StringComparison.OrdinalIgnoreCase));
                if (hit != null) { assetId = hit.Id; assetName = hit.Name; if (company == null) company = hit.CompanyId; }
            }
            var companyName = company is int cn ? (await dir.GetAsync(cn, ct))?.Name ?? "" : "";
            long bytes = b.TryGetProperty("bytes", out var bv) && bv.ValueKind == JsonValueKind.Number && bv.TryGetInt64(out var bl) ? Math.Max(0, bl) : 0;
            var rkey = BackupModule.Clip(Str(b, "key"), 180);
            var (jobId, isNew) = await BackupRecorder.RecordAsync(db, new RunOrigin(company, companyName, assetId, assetName, "api"),
                new BackupReport(Str(b, "tool"), job, When(b, "started"), When(b, "ended"), result, BackupModule.Clip(Str(b, "message"), 2000), bytes, rkey == "" ? "" : "api:" + rkey), ct);
            if (isNew) await BackupChecker.RunAsync(http.RequestServices, ct);
            return Results.Ok(new { job = jobId, stored = isNew });
        }).AllowAnonymous().RequireModule(key);
    }
}
