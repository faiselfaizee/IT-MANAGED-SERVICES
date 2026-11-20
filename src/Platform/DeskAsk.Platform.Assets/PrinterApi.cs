using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace DeskAsk.Platform.Assets;

/// <summary>One period's page count. Partial means the first reading came after the period began, so the figure may be short.</summary>
public sealed record PageCount(long Pages, bool Partial);

/// <summary>
/// Printers: what a site controller read over SNMP (status, supplies, paper trays, alerts, lifetime page counter) is kept on the asset, the page counter once a
/// day for the history. People with the right role can clear a printer's own queue or restart it; the controller does it inside the customer's network.
/// </summary>
public static class PrinterApi
{
    static readonly string[] Actions = { "clear-queue", "restart" };
    static string Clip(string? s, int n) => AgentsApi.Clip(s, n);

    static string? S(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static double Num(JsonElement e, string n, double d = -1) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : d;
    static bool Flag(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
    static IEnumerable<JsonElement> Arr(JsonElement e, string n, int max) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Take(max) : Enumerable.Empty<JsonElement>();

    static readonly string[] Statuses = { "idle", "printing", "warmup", "other", "unknown" };
    static readonly string[] SupplyStates = { "ok", "low", "empty", "full", "unknown" };
    static readonly string[] Severities = { "critical", "warning", "info" };

    /// <summary>The reading as it will be stored: only the fields we know, trimmed, and at most so many of each list.</summary>
    public static object? Clean(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object) return null;
        var status = S(r, "status") ?? "unknown";
        return new
        {
            status = Statuses.Contains(status) ? status : "unknown",
            flags = r.TryGetProperty("flags", out var fl) && fl.ValueKind == JsonValueKind.Array
                ? fl.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => Clip(x.GetString(), 40)).Take(20).ToArray() : Array.Empty<string>(),
            message = Clip(S(r, "message"), 160), name = Clip(S(r, "name"), 120), model = Clip(S(r, "model"), 120), serial = Clip(S(r, "serial"), 80),
            pages = (long)Math.Max(-1, Num(r, "pages")),
            supplies = Arr(r, "supplies", 40).Select(x => new
            {
                name = Clip(S(x, "name"), 120), kind = Clip(S(x, "kind"), 20), color = Clip(S(x, "color"), 20), pct = Math.Round(Math.Clamp(Num(x, "pct"), -1, 100), 1),
                state = SupplyStates.Contains(S(x, "state") ?? "") ? S(x, "state") : "unknown", waste = Flag(x, "waste"),
            }).ToArray(),
            trays = Arr(r, "trays", 20).Select(x => new
            {
                name = Clip(S(x, "name"), 60), media = Clip(S(x, "media"), 60), pct = Math.Round(Math.Clamp(Num(x, "pct"), -1, 100), 1),
                state = SupplyStates.Contains(S(x, "state") ?? "") ? S(x, "state") : "unknown",
            }).ToArray(),
            alerts = Arr(r, "alerts", 20).Select(x => new { severity = Severities.Contains(S(x, "severity") ?? "") ? S(x, "severity") : "info", text = Clip(S(x, "text"), 200) }).ToArray(),
        };
    }

    /// <summary>Keeps what a controller read: the latest reading on the asset, the page counter once a day. An error keeps the last reading and says why the new one failed.</summary>
    internal static async Task StoreAsync(NpgsqlConnection db, long assetId, JsonElement item, CancellationToken ct)
    {
        if (item.TryGetProperty("reading", out var rd) && Clean(rd) is { } clean)
        {
            var json = JsonSerializer.Serialize(clean);
            await using (var c = new NpgsqlCommand("update assets.assets set m_printer = @j::jsonb, m_printer_at = now(), m_printer_error = '' where id = @a", db))
            { AgentsApi.P(c, "j", json); AgentsApi.P(c, "a", assetId); await c.ExecuteNonQueryAsync(ct); }
            var pages = (long)Math.Max(-1, Num(rd, "pages"));
            if (pages >= 0)
            {
                await using var p = new NpgsqlCommand(@"insert into assets.printer_pages (asset_id, day, pages) values (@a, (now() at time zone 'utc')::date, @p)
                    on conflict (asset_id, day) do update set pages = excluded.pages", db);
                AgentsApi.P(p, "a", assetId); AgentsApi.P(p, "p", pages); await p.ExecuteNonQueryAsync(ct);
                await using var old = new NpgsqlCommand("delete from assets.printer_pages where asset_id = @a and day < (now() at time zone 'utc')::date - 800", db);
                AgentsApi.P(old, "a", assetId); await old.ExecuteNonQueryAsync(ct);
            }
        }
        else
        {
            await using var c = new NpgsqlCommand("update assets.assets set m_printer_error = @e where id = @a", db);
            AgentsApi.P(c, "e", Clip(S(item, "error"), 300)); AgentsApi.P(c, "a", assetId); await c.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>The stored reading as Monitoring sees it; null when there is none.</summary>
    public static PrinterState? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json); var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            return new PrinterState(S(r, "status") ?? "unknown",
                r.TryGetProperty("flags", out var fl) && fl.ValueKind == JsonValueKind.Array ? fl.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : Array.Empty<string>(),
                Arr(r, "supplies", 40).Select(x => new PrinterSupply(S(x, "name") ?? "", S(x, "kind") ?? "", S(x, "color") ?? "", Num(x, "pct"), S(x, "state") ?? "unknown", Flag(x, "waste"))).ToArray(),
                Arr(r, "trays", 20).Select(x => new PrinterTray(S(x, "name") ?? "", S(x, "media") ?? "", Num(x, "pct"), S(x, "state") ?? "unknown")).ToArray(),
                Arr(r, "alerts", 20).Select(x => new PrinterAlert(S(x, "severity") ?? "info", S(x, "text") ?? "")).ToArray(),
                S(r, "message") ?? "", (long)Num(r, "pages"));
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Pages printed from the start of day <paramref name="from"/> to the end of day <paramref name="to"/>, from the daily readings of the lifetime counter (oldest first).
    /// The count starts from the last reading before <paramref name="from"/>; without one, from the first reading after it (Partial). Null when there is nothing to
    /// compare, or the counter went down (the printer was replaced or reset).
    /// </summary>
    public static PageCount? Between(IReadOnlyList<(DateOnly Day, long Pages)> rows, DateOnly from, DateOnly to)
    {
        int end = -1, begin = -1;
        for (var i = 0; i < rows.Count; i++) { if (rows[i].Day <= to) end = i; if (rows[i].Day < from) begin = i; }
        if (end < 0) return null;
        var partial = false;
        if (begin < 0)
        {
            partial = true;
            for (var i = 0; i < rows.Count; i++) if (rows[i].Day >= from) { begin = i; break; }
            if (begin < 0 || begin > end) return null;
        }
        if (end < begin) return null;
        var pages = rows[end].Pages - rows[begin].Pages;
        return pages < 0 ? null : new PageCount(pages, partial);
    }

    /// <summary>Pages per day for a chart: the rise of the counter since the previous reading, shown on the day of the later one.</summary>
    public static IReadOnlyList<(DateOnly Day, long Pages)> Daily(IReadOnlyList<(DateOnly Day, long Pages)> rows)
    {
        var o = new List<(DateOnly, long)>();
        for (var i = 1; i < rows.Count; i++) { var d = rows[i].Pages - rows[i - 1].Pages; if (d >= 0) o.Add((rows[i].Day, d)); }
        return o;
    }

    static string Fail(JsonElement? error, string fallback) =>
        error is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : fallback;

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        var readers = (RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole(AssetsAccess.Readers)).RequireModule(key).RequireAccess(key, Acts.Read);
        var staff = (RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin", "engineer")).RequireModule(key);

        readers(r.MapGet("/api/assets/{id:long}/printer", async (long id, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read);
            await using var db = await AgentsApi.Open(t, ct);
            if (!await AssetsAccess.InScope(db, id, scope, ct)) return Results.NotFound();
            string? reading = null, error = ""; DateTime? at = null; bool controller = false, printer = false;
            await using (var c = new NpgsqlCommand(@"select a.m_printer::text, a.m_printer_at, a.m_printer_error, a.collector_id <> '' and a.monitor = 'snmp', (case when coalesce(t.name, '') ~* 'print' then true else false end)
                from assets.assets a left join assets.types t on t.id = a.type_id where a.id = @i", db))
            {
                AgentsApi.P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return Results.NotFound();
                reading = rd.IsDBNull(0) ? null : rd.GetString(0); at = rd.IsDBNull(1) ? null : rd.GetDateTime(1); error = rd.GetString(2); controller = rd.GetBoolean(3); printer = rd.GetBoolean(4);
            }
            var rows = new List<(DateOnly, long)>();
            await using (var c = new NpgsqlCommand("select day, pages from assets.printer_pages where asset_id = @i order by day", db))
            {
                AgentsApi.P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) rows.Add((DateOnly.FromDateTime(rd.GetDateTime(0)), rd.GetInt64(1)));
            }
            var today = DateOnly.FromDateTime(DateTime.UtcNow); var m0 = new DateOnly(today.Year, today.Month, 1); var p0 = m0.AddMonths(-1);
            object? Pc(PageCount? c) => c == null ? null : new { pages = c.Pages, partial = c.Partial };
            var daily = Daily(rows).Where(d => d.Day >= today.AddDays(-59)).Select(d => new { day = d.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), pages = d.Pages }).ToArray();
            return Results.Ok(new
            {
                reading = reading == null ? (JsonElement?)null : JsonDocument.Parse(reading).RootElement.Clone(), at, error,
                // how it is read: a site controller must watch the printer over SNMP, otherwise nothing is read and no action can run
                controller, printer,
                pages = new { total = rows.Count > 0 ? rows[^1].Item2 : (long?)null, last7 = Pc(Between(rows, today.AddDays(-6), today)), last30 = Pc(Between(rows, today.AddDays(-29), today)),
                    thisMonth = Pc(Between(rows, m0, today)), lastMonth = Pc(Between(rows, p0, m0.AddDays(-1))) },
                daily,
            });
        }));

        staff(r.MapPost("/api/assets/{id:long}/printer/{action}", async (long id, string action, HttpContext http, ITenantContext t, IControllerRelay relay, IAuditLog audit, CancellationToken ct) =>
        {
            if (!Actions.Contains(action)) return AgentsApi.Bad("bad_action", "The action must be clear-queue or restart.");
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Modify);
            await using var db = await AgentsApi.Open(t, ct);
            if (!await AssetsAccess.InScope(db, id, scope, ct)) return Results.NotFound();
            string collector, ip, host, name, monitor;
            await using (var c = new NpgsqlCommand("select collector_id, ip_address, hostname, name, monitor from assets.assets where id = @i", db))
            {
                AgentsApi.P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return Results.NotFound();
                collector = rd.GetString(0); ip = rd.GetString(1); host = rd.GetString(2); name = rd.GetString(3); monitor = rd.GetString(4);
            }
            if (collector == "" || monitor != "snmp")
                return AgentsApi.Bad("no_controller", "A site controller must watch this printer over SNMP first: open Overview, choose a controller and Read as 'Printer or other device (SNMP)'.");
            if (ip == "" && host == "") return AgentsApi.Bad("no_address", "The printer has no IP address.");
            var reply = await relay.CallAsync(collector, "printer", new { asset = id.ToString(), action, ip = ip != "" ? ip : host }, TimeSpan.FromSeconds(45), http.RequestAborted);
            await audit.WriteAsync("assets.printer_" + action.Replace('-', '_'), id.ToString(), name + ": " + (reply.Ok ? "done" : Fail(reply.Error, "failed")), ct: ct);
            if (!reply.Ok) return Results.Json(new { ok = false, error = reply.TimedOut ? "controller_timeout" : "printer_refused", detail = Fail(reply.Error, "The printer did not do it.") }, statusCode: reply.TimedOut ? 504 : 409);
            var msg = reply.Result is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "Done.";
            return Results.Ok(new { ok = true, message = msg });
        }));
    }
}
