using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using static DeskAsk.Platform.ServiceDesk.ServiceDeskModule;

namespace DeskAsk.Platform.ServiceDesk;

/// <summary>Categories and service levels, and the people (customer users and the company they belong to).</summary>
public static class SettingsApi
{
    public static void Map(IEndpointRouteBuilder r, string key)
    {
        var anyone = (RouteHandlerBuilder b) => b.RequireAuthorization().RequireModule(key);
        var admin = (RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);

        anyone(r.MapGet("/api/servicedesk/settings", async (ISettings s, CancellationToken ct) =>
            Results.Ok(new { categories = await SlaConfig.CategoriesAsync(s, ct), sla = (await SlaConfig.LoadAsync(s, ct)).ToJson() })));

        admin(r.MapPut("/api/servicedesk/settings", async (JsonElement b, ISettings s, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            if (b.ValueKind != JsonValueKind.Object) return Bad("bad_request", "The body must be a JSON object.");
            if (b.TryGetProperty("categories", out var cats))
            {
                if (cats.ValueKind != JsonValueKind.Array || cats.GetArrayLength() is 0 or > 50) return Bad("bad_categories", "Give 1 to 50 categories.");
                var list = new List<string>();
                foreach (var x in cats.EnumerateArray())
                {
                    var n = x.ValueKind == JsonValueKind.String ? x.GetString()!.Trim() : "";
                    if (n.Length is 0 or > 60) return Bad("bad_categories", "A category is 1 to 60 characters.");
                    if (list.Contains(n, StringComparer.OrdinalIgnoreCase)) return Bad("bad_categories", "'" + n + "' is listed twice.");
                    list.Add(n);
                }
                await s.SetAsync(SlaConfig.CategoriesKey, JsonSerializer.Serialize(list), ct);
                await audit.WriteAsync("servicedesk.categories_saved", ct: ct);
            }
            if (b.TryGetProperty("sla", out var sla))
            {
                var cfg = SlaConfig.Parse(sla, out var err);
                if (cfg == null) return Bad("bad_sla", err);
                await s.SetAsync(SlaConfig.Key, JsonSerializer.Serialize(cfg.ToJson()), ct);
                await using var db = await Open(t, ct);
                await using var c = new NpgsqlCommand($"update servicedesk.tickets set respond_due = {SlaConfig.RespondDue}, resolve_due = {SlaConfig.ResolveDue} where status not in ('resolved','closed')", db);
                cfg.Bind(c); await c.ExecuteNonQueryAsync(ct);
                await audit.WriteAsync("servicedesk.sla_saved", ct: ct);
            }
            return Results.Ok(new { categories = await SlaConfig.CategoriesAsync(s, ct), sla = (await SlaConfig.LoadAsync(s, ct)).ToJson() });
        }));

        // ---- what the signed-in person may do here
        anyone(r.MapGet("/api/servicedesk/me", async (System.Security.Claims.ClaimsPrincipal u, ITenantContext t, ICompanyDirectory companies, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var w = await WhoAsync(u, db, ct);
            var company = w.CompanyId is int id ? await companies.GetAsync(id, ct) : null;
            return Results.Ok(new
            {
                id = w.Id, name = w.Name, role = w.Role, staff = w.Staff, customer = w.Customer, companyId = w.CompanyId, company = company?.Name,
                canRaise = w.Staff || (w.Customer && w.CompanyId != null && w.MayRaise), canModify = w.Staff || (w.Customer && w.CompanyId != null && w.MayModify), readOnly = w.Role == "viewer",
            });
        }));

        // ---- people who may raise tickets for a company
        admin(r.MapGet("/api/servicedesk/people", async (ITenantContext t, IUserDirectory users, ICompanyDirectory companies, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var rows = new Dictionary<Guid, (int? Company, bool Raise, bool Modify)>();
            await using (var c = new NpgsqlCommand("select user_id, company_id, may_raise, may_modify from servicedesk.people", db))
            await using (var rd = await c.ExecuteReaderAsync(ct))
                while (await rd.ReadAsync(ct)) rows[rd.GetGuid(0)] = (rd.IsDBNull(1) ? null : rd.GetInt32(1), rd.GetBoolean(2), rd.GetBoolean(3));
            var names = (await companies.ListAsync(ct)).ToDictionary(x => x.Id, x => x.Name);
            return Results.Ok((await users.ListAsync(ct)).Where(x => x.Role is "customer" or "cadmin").Select(x =>
            {
                rows.TryGetValue(x.Id, out var p);
                return new { id = x.Id, name = x.Name, email = x.Email, role = x.Role, active = x.Active, companyId = p.Company, company = p.Company is int i && names.TryGetValue(i, out var n) ? n : null, mayRaise = rows.ContainsKey(x.Id) ? p.Raise : true, mayModify = rows.ContainsKey(x.Id) ? p.Modify : true };
            }));
        }));

        admin(r.MapPut("/api/servicedesk/people/{userId:guid}", async (Guid userId, JsonElement b, ITenantContext t, IUserDirectory users, ICompanyDirectory companies, IAccess access, IAuditLog audit, CancellationToken ct) =>
        {
            var u = await users.GetAsync(userId, ct);
            if (u == null) return Results.NotFound();
            if (b.ValueKind != JsonValueKind.Object) return Bad("bad_request", "The body must be a JSON object.");
            int? company = null;
            if (b.TryGetProperty("companyId", out var cv) && cv.ValueKind != JsonValueKind.Null)
            {
                if (cv.ValueKind != JsonValueKind.Number || !cv.TryGetInt32(out var cid)) return Bad("bad_company", "companyId must be a number.");
                if (await companies.GetAsync(cid, ct) == null) return Bad("bad_company", "That company does not exist.");
                company = cid;
            }
            bool Flag(string n) => !b.TryGetProperty(n, out var v) || v.ValueKind != JsonValueKind.False;
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand(@"insert into servicedesk.people (user_id, company_id, may_raise, may_modify) values (@u, @c, @r, @m)
                on conflict (user_id) do update set company_id = @c, may_raise = @r, may_modify = @m, updated_at = now()", db);
            P(c, "u", userId); P(c, "c", company); P(c, "r", Flag("mayRaise")); P(c, "m", Flag("mayModify"));
            await c.ExecuteNonQueryAsync(ct);
            await access.SetCompanyAsync(userId, company, ct);   // the same company decides what a customer administrator sees in Assets
            await audit.WriteAsync("servicedesk.person_saved", u.Email, "company=" + (company?.ToString() ?? "none"), ct: ct);
            return Results.Ok(new { ok = true });
        }));

        // ---- staff to pick as assignee
        anyone(r.MapGet("/api/servicedesk/staff", async (System.Security.Claims.ClaimsPrincipal u, ITenantContext t, IUserDirectory users, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            if (!(await WhoAsync(u, db, ct)).Staff) return Results.Json(new { error = "forbidden" }, statusCode: 403);
            return Results.Ok((await users.ListAsync(ct)).Where(x => x.Active && x.Role is "admin" or "engineer").Select(x => new { id = x.Id, name = x.Name, email = x.Email, role = x.Role }));
        }));
    }
}
