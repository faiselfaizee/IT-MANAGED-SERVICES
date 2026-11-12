using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using NpgsqlTypes;

namespace DeskAsk.Platform.ServiceDesk;

/// <summary>The monthly service report: what a company asked for, how fast it was answered and solved, and what is still open. Staff and viewers may pick any company; a customer admin sees their own.</summary>
internal static class ReportsApi
{
    public static void Map(IEndpointRouteBuilder r, string key)
    {
        r.MapGet("/api/servicedesk/reports/monthly", async (string? month, int? companyId, ClaimsPrincipal u, ITenantContext t, ICompanyDirectory companies, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            var ownCompany = w.Role == "cadmin" && w.CompanyId != null;
            if (!(w.Staff || w.Role == "viewer" || ownCompany)) return Results.Json(new { error = "forbidden" }, statusCode: 403);
            var m = month ?? DateTime.UtcNow.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            if (!DateTime.TryParseExact(m, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start)) return ServiceDeskModule.Bad("bad_month", "month must look like 2026-10");
            var end = start.AddMonths(1); var co = ownCompany ? w.CompanyId : companyId;
            string companyName = "All companies";
            if (co is int ci) { var c0 = await companies.GetAsync(ci, ct); if (c0 == null) return ServiceDeskModule.Bad("bad_company", "That company does not exist."); companyName = c0.Name; }

            var d = await Build(db, start, end, co, w.Staff || w.Role == "viewer", ct);
            var totals = new { opened = d.Opened, resolved = d.Resolved, backlogStart = d.BacklogStart, backlogEnd = d.BacklogEnd, avgFirstResponseHours = d.AvgFirst, avgResolutionHours = d.AvgSolve, respondMetPercent = d.RespondPct, resolveMetPercent = d.ResolvePct };
            return Results.Ok(new { month = m, from = start, to = end, companyId = co, company = companyName, totals, byPriority = d.ByPriority, byCategory = d.ByCategory, bySource = d.BySource, byRequester = d.ByRequester, byAsset = d.ByAsset, byCompany = d.ByCompany, tickets = d.Tickets, truncated = d.Tickets.Count >= 300, changes = d.Changes });
        }).RequireAuthorization().RequireModule(key);
    }

    internal sealed record Counted(string Name, long Count);
    internal sealed class ReportData
    {
        public long Opened, Resolved, BacklogStart, BacklogEnd; public double? AvgFirst, AvgSolve, RespondPct, ResolvePct;
        public List<Counted> ByPriority = new(), ByCategory = new(), BySource = new(), ByRequester = new(), ByAsset = new(), ByCompany = new();
        public List<object> Tickets = new(); public object? Changes;
    }

    internal static async Task<ReportData> Build(NpgsqlConnection db, DateTime start, DateTime end, int? co, bool withChanges, CancellationToken ct)
    {
        var d = new ReportData();
            NpgsqlCommand Cmd(string sql)
            {
                var c = new NpgsqlCommand(sql, db);
                c.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.TimestampTz) { Value = start }); c.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.TimestampTz) { Value = end });
                c.Parameters.Add(new NpgsqlParameter("co", NpgsqlDbType.Integer) { Value = (object?)co ?? DBNull.Value });
                return c;
            }
            const string scope = "(@co::int is null or company_id = @co)";
            const string done = "coalesce(resolved_at, closed_at)";

            await using (var c = Cmd($@"select
                count(*) filter (where created_at >= @s and created_at < @e),
                count(*) filter (where {done} >= @s and {done} < @e),
                count(*) filter (where created_at < @s and ({done} is null or {done} >= @s)),
                count(*) filter (where created_at < @e and ({done} is null or {done} >= @e)),
                avg(extract(epoch from first_response_at - created_at) / 3600.0) filter (where created_at >= @s and created_at < @e and first_response_at is not null),
                avg(greatest(extract(epoch from {done} - created_at) - paused_s, 0) / 3600.0) filter (where created_at >= @s and created_at < @e and {done} is not null),
                count(*) filter (where created_at >= @s and created_at < @e and first_response_at is not null and first_response_at <= respond_due),
                count(*) filter (where created_at >= @s and created_at < @e and respond_due is not null and (first_response_at is not null or respond_due < least(@e, now()))),
                count(*) filter (where created_at >= @s and created_at < @e and {done} is not null and {done} <= resolve_due),
                count(*) filter (where created_at >= @s and created_at < @e and resolve_due is not null and ({done} is not null or resolve_due < least(@e, now())))
                from servicedesk.tickets where {scope}"))
            {
                await using var rd = await c.ExecuteReaderAsync(ct); await rd.ReadAsync(ct);
                double? D(int i) => rd.IsDBNull(i) ? null : Math.Round(rd.GetDouble(i), 1);
                double? Pct(int met, int total) => rd.GetInt64(total) == 0 ? null : Math.Round(100.0 * rd.GetInt64(met) / rd.GetInt64(total), 0);
                d.Opened = rd.GetInt64(0); d.Resolved = rd.GetInt64(1); d.BacklogStart = rd.GetInt64(2); d.BacklogEnd = rd.GetInt64(3); d.AvgFirst = D(4); d.AvgSolve = D(5); d.RespondPct = Pct(6, 7); d.ResolvePct = Pct(8, 9);
            }

            async Task<List<Counted>> Group(string expr, string extraWhere = "", int take = 10)
            {
                var o = new List<Counted>();
                await using var c = Cmd($"select {expr} as k, count(*) from servicedesk.tickets where {scope} and created_at >= @s and created_at < @e {extraWhere} group by 1 order by 2 desc, 1 limit {take}");
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) o.Add(new Counted(rd.GetString(0), rd.GetInt64(1)));
                return o;
            }
            d.ByPriority = await Group("priority"); d.ByCategory = await Group("case when category = '' then '(none)' else category end");
            d.BySource = await Group("source"); d.ByRequester = await Group("case when requester_name = '' then requester_email else requester_name end", "and (requester_name <> '' or requester_email <> '')", 5);
            d.ByAsset = await Group("asset_name", "and asset_name <> ''", 5);
            if (co == null) d.ByCompany = await Group("case when company_name = '' then '(no company)' else company_name end", "", 10);

            var tickets = d.Tickets;
            await using (var c = Cmd($"select id, number, subject, status, priority, category, requester_name, created_at, {done} from servicedesk.tickets where {scope} and created_at >= @s and created_at < @e order by id limit 300"))
            {
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) tickets.Add(new { id = rd.GetInt64(0), number = rd.GetString(1), subject = rd.GetString(2), status = rd.GetString(3), priority = rd.GetString(4), category = rd.GetString(5), requester = rd.GetString(6), createdAt = rd.GetDateTime(7), doneAt = rd.IsDBNull(8) ? (DateTime?)null : rd.GetDateTime(8) });
            }
            if (withChanges)
            {
                await using var c = Cmd($"select count(*) filter (where status = 'completed'), count(*) filter (where status = 'failed') from servicedesk.changes where {scope} and status in ('completed','failed') and updated_at >= @s and updated_at < @e");
                await using var rd = await c.ExecuteReaderAsync(ct); await rd.ReadAsync(ct); d.Changes = new { completed = rd.GetInt64(0), failed = rd.GetInt64(1) };
            }
        return d;
    }
}
