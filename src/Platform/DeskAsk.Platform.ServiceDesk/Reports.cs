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

            NpgsqlCommand Cmd(string sql)
            {
                var c = new NpgsqlCommand(sql, db);
                c.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.TimestampTz) { Value = start }); c.Parameters.Add(new NpgsqlParameter("e", NpgsqlDbType.TimestampTz) { Value = end });
                c.Parameters.Add(new NpgsqlParameter("co", NpgsqlDbType.Integer) { Value = (object?)co ?? DBNull.Value });
                return c;
            }
            const string scope = "(@co::int is null or company_id = @co)";
            const string done = "coalesce(resolved_at, closed_at)";

            object totals;
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
                totals = new { opened = rd.GetInt64(0), resolved = rd.GetInt64(1), backlogStart = rd.GetInt64(2), backlogEnd = rd.GetInt64(3), avgFirstResponseHours = D(4), avgResolutionHours = D(5), respondMetPercent = Pct(6, 7), resolveMetPercent = Pct(8, 9) };
            }

            async Task<List<object>> Group(string expr, string extraWhere = "", int take = 10)
            {
                var o = new List<object>();
                await using var c = Cmd($"select {expr} as k, count(*) from servicedesk.tickets where {scope} and created_at >= @s and created_at < @e {extraWhere} group by 1 order by 2 desc, 1 limit {take}");
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) o.Add(new { name = rd.GetString(0), count = rd.GetInt64(1) });
                return o;
            }
            var byPriority = await Group("priority"); var byCategory = await Group("case when category = '' then '(none)' else category end");
            var bySource = await Group("source"); var byRequester = await Group("case when requester_name = '' then requester_email else requester_name end", "and (requester_name <> '' or requester_email <> '')", 5);
            var byAsset = await Group("asset_name", "and asset_name <> ''", 5);
            var byCompany = co == null ? await Group("case when company_name = '' then '(no company)' else company_name end", "", 10) : new List<object>();

            var tickets = new List<object>();
            await using (var c = Cmd($"select id, number, subject, status, priority, category, requester_name, created_at, {done} from servicedesk.tickets where {scope} and created_at >= @s and created_at < @e order by id limit 300"))
            {
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) tickets.Add(new { id = rd.GetInt64(0), number = rd.GetString(1), subject = rd.GetString(2), status = rd.GetString(3), priority = rd.GetString(4), category = rd.GetString(5), requester = rd.GetString(6), createdAt = rd.GetDateTime(7), doneAt = rd.IsDBNull(8) ? (DateTime?)null : rd.GetDateTime(8) });
            }
            var cmp = new List<object>();
            if (w.Staff || w.Role == "viewer")
            {
                await using var c = Cmd($"select count(*) filter (where status = 'completed'), count(*) filter (where status = 'failed') from servicedesk.changes where {scope} and status in ('completed','failed') and updated_at >= @s and updated_at < @e");
                await using var rd = await c.ExecuteReaderAsync(ct); await rd.ReadAsync(ct); cmp.Add(new { completed = rd.GetInt64(0), failed = rd.GetInt64(1) });
            }
            return Results.Ok(new { month = m, from = start, to = end, companyId = co, company = companyName, totals, byPriority, byCategory, bySource, byRequester, byAsset, byCompany, tickets, truncated = tickets.Count >= 300, changes = cmp.FirstOrDefault() });
        }).RequireAuthorization().RequireModule(key);
    }
}
