using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace DeskAsk.Platform.ServiceDesk;

/// <summary>
/// The knowledge base: articles written by our staff that answer common questions. An article is shown to everyone, to one company's people,
/// or to staff only, and only while it is published. People find them by search, and the New ticket page suggests matches while the subject is typed.
/// </summary>
internal static class KnowledgeApi
{
    static IResult Forbidden(string d) => Results.Json(new { error = "forbidden", detail = d }, statusCode: 403);
    static void P(NpgsqlCommand c, string n, object? v) => ServiceDeskModule.P(c, n, v);
    static string S(JsonElement b, string n) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
    static readonly string[] Visibilities = { "everyone", "company", "staff" };

    /// <summary>Which articles this person may read: staff read all (drafts too); everyone else reads published ones for everyone or for their own company.</summary>
    const string Visible = "(@staff or (published and (visibility = 'everyone' or (visibility = 'company' and company_id is not null and company_id = @company))))";

    static string Snippet(string body) { var s = body.Replace('\n', ' ').Trim(); return s.Length > 200 ? s[..200] + "…" : s; }

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        RouteHandlerBuilder Any(RouteHandlerBuilder b) => b.RequireAuthorization().RequireModule(key);

        // search and list; `limit` is used by the suggestions on New ticket
        Any(r.MapGet("/api/servicedesk/kb", async (string? q, int? limit, ClaimsPrincipal u, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            var term = (q ?? "").Trim();
            if (term.Length > 100) term = term[..100];
            await using var c = new NpgsqlCommand($@"select id, title, body, tags, visibility, company_id, published, helpful, not_helpful, updated_at from servicedesk.articles
                where {Visible} and (@q = '' or title ilike @like or tags ilike @like or body ilike @like)
                order by (case when @q <> '' and title ilike @like then 0 else 1 end), helpful - not_helpful desc, title limit @n", db);
            P(c, "staff", w.Staff); P(c, "company", w.CompanyId); P(c, "q", term);
            P(c, "like", "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"); P(c, "n", Math.Clamp(limit ?? 50, 1, 100));
            var list = new List<object>();
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                list.Add(new { id = rd.GetInt32(0), title = rd.GetString(1), snippet = Snippet(rd.GetString(2)), tags = rd.GetString(3), visibility = rd.GetString(4), companyId = rd.IsDBNull(5) ? (int?)null : rd.GetInt32(5), published = rd.GetBoolean(6), helpful = rd.GetInt32(7), notHelpful = rd.GetInt32(8), updatedAt = rd.GetDateTime(9) });
            return Results.Ok(list);
        }));

        Any(r.MapGet("/api/servicedesk/kb/{id:int}", async (int id, ClaimsPrincipal u, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            await using var c = new NpgsqlCommand($"select id, title, body, tags, visibility, company_id, published, helpful, not_helpful, views, updated_at, created_by from servicedesk.articles where id = @i and {Visible}", db);
            P(c, "i", id); P(c, "staff", w.Staff); P(c, "company", w.CompanyId);
            object? a;
            await using (var rd = await c.ExecuteReaderAsync(ct))
            {
                if (!await rd.ReadAsync(ct)) return Results.NotFound();
                a = new { id = rd.GetInt32(0), title = rd.GetString(1), body = rd.GetString(2), tags = rd.GetString(3), visibility = rd.GetString(4), companyId = rd.IsDBNull(5) ? (int?)null : rd.GetInt32(5), published = rd.GetBoolean(6), helpful = rd.GetInt32(7), notHelpful = rd.GetInt32(8), views = rd.GetInt32(9), updatedAt = rd.GetDateTime(10), by = w.Staff ? rd.GetString(11) : "" };
            }
            if (!w.Staff) { await using var v = new NpgsqlCommand("update servicedesk.articles set views = views + 1 where id = @i", db); P(v, "i", id); await v.ExecuteNonQueryAsync(ct); }
            return Results.Ok(a);
        }));

        Any(r.MapPost("/api/servicedesk/kb", (JsonElement b, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) => Save(null, b, u, t, audit, ct)));
        Any(r.MapPut("/api/servicedesk/kb/{id:int}", (int id, JsonElement b, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) => Save(id, b, u, t, audit, ct)));
        Any(r.MapDelete("/api/servicedesk/kb/{id:int}", async (int id, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            if (!(await ServiceDeskModule.WhoAsync(u, db, ct)).Staff) return Forbidden("Only our staff manage articles.");
            await using var c = new NpgsqlCommand("delete from servicedesk.articles where id = @i returning title", db); P(c, "i", id);
            if (await c.ExecuteScalarAsync(ct) is not string title) return Results.NotFound();
            await audit.WriteAsync("servicedesk.article_deleted", title, ct: ct);
            return Results.Ok(new { ok = true });
        }));

        // "was this helpful?": one vote per person, which they can change
        Any(r.MapPost("/api/servicedesk/kb/{id:int}/vote", async (int id, JsonElement b, ClaimsPrincipal u, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (w.Id == Guid.Empty) return Forbidden("Sign in to vote.");
            if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty("helpful", out var hv) || hv.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return ServiceDeskModule.Bad("bad_request", "helpful must be true or false");
            await using (var chk = new NpgsqlCommand($"select 1 from servicedesk.articles where id = @i and published and {Visible}", db)) { P(chk, "i", id); P(chk, "staff", w.Staff); P(chk, "company", w.CompanyId); if (await chk.ExecuteScalarAsync(ct) == null) return Results.NotFound(); }
            await using var tx = await db.BeginTransactionAsync(ct);
            await using (var c = new NpgsqlCommand("insert into servicedesk.article_votes (article_id, user_id, helpful) values (@a, @u, @h) on conflict (article_id, user_id) do update set helpful = excluded.helpful, at = now()", db, tx)) { P(c, "a", id); P(c, "u", w.Id); P(c, "h", hv.GetBoolean()); await c.ExecuteNonQueryAsync(ct); }
            await using (var c = new NpgsqlCommand("update servicedesk.articles set helpful = (select count(*) from servicedesk.article_votes where article_id = @a and helpful), not_helpful = (select count(*) from servicedesk.article_votes where article_id = @a and not helpful) where id = @a", db, tx)) { P(c, "a", id); await c.ExecuteNonQueryAsync(ct); }
            await tx.CommitAsync(ct);
            return Results.Ok(new { ok = true });
        }));
    }

    static async Task<IResult> Save(int? id, JsonElement b, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct)
    {
        await using var db = await ServiceDeskModule.Open(t, ct);
        var w = await ServiceDeskModule.WhoAsync(u, db, ct);
        if (!w.Staff) return Forbidden("Only our staff manage articles.");
        if (b.ValueKind != JsonValueKind.Object) return ServiceDeskModule.Bad("bad_request", "the body must be a JSON object");
        var title = S(b, "title"); var body = b.TryGetProperty("body", out var bv) && bv.ValueKind == JsonValueKind.String ? bv.GetString()! : ""; var tags = S(b, "tags");
        var vis = S(b, "visibility"); if (vis == "") vis = "everyone";
        if (title == "" || title.Length > 200) return ServiceDeskModule.Bad("bad_request", "Give the article a title of up to 200 characters.");
        if (body.Length > 100000) return ServiceDeskModule.Bad("bad_request", "The article is longer than 100000 characters.");
        if (tags.Length > 300) return ServiceDeskModule.Bad("bad_request", "Tags are longer than 300 characters.");
        if (!Visibilities.Contains(vis)) return ServiceDeskModule.Bad("bad_request", "visibility must be one of " + string.Join(", ", Visibilities));
        int? company = null;
        if (vis == "company")
        {
            if (!b.TryGetProperty("companyId", out var cv) || cv.ValueKind != JsonValueKind.Number || !cv.TryGetInt32(out var ci)) return ServiceDeskModule.Bad("bad_request", "Choose the company this article is for.");
            company = ci;
        }
        var published = b.TryGetProperty("published", out var pv) && pv.ValueKind == JsonValueKind.True;
        await using var c = new NpgsqlCommand(id == null
            ? "insert into servicedesk.articles (title, body, tags, visibility, company_id, published, created_by) values (@t, @b, @g, @v, @c, @p, @by) returning id"
            : "update servicedesk.articles set title = @t, body = @b, tags = @g, visibility = @v, company_id = @c, published = @p, updated_at = now() where id = @i returning id", db);
        P(c, "t", title); P(c, "b", body); P(c, "g", tags); P(c, "v", vis); P(c, "c", company); P(c, "p", published); P(c, "by", w.Name); P(c, "i", id);
        if (await c.ExecuteScalarAsync(ct) is not int newId) return Results.NotFound();
        await audit.WriteAsync(id == null ? "servicedesk.article_created" : "servicedesk.article_updated", title, published ? "published, " + vis : "draft", ct: ct);
        return Results.Ok(new { id = newId });
    }
}
