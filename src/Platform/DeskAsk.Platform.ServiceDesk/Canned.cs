using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using static DeskAsk.Platform.ServiceDesk.ServiceDeskModule;

namespace DeskAsk.Platform.ServiceDesk;

/// <summary>
/// Canned replies: ready answers the team picks in the reply box. The text may hold {requester}, {agent} and {number}, which the screen fills in from the ticket.
/// Staff read them; administrators add, change and delete them.
/// </summary>
public static class CannedApi
{
    public static void Map(IEndpointRouteBuilder r, string key)
    {
        var anyone = (RouteHandlerBuilder b) => b.RequireAuthorization().RequireModule(key);
        var admin = (RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);

        anyone(r.MapGet("/api/servicedesk/canned", async (System.Security.Claims.ClaimsPrincipal u, ITenantContext t, string? all, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            var who = await WhoAsync(u, db, ct);
            if (!who.Staff) return Results.Json(new { error = "forbidden" }, statusCode: 403);
            var everything = (all == "1" || string.Equals(all, "true", StringComparison.OrdinalIgnoreCase)) && who.Admin;
            await using var c = new NpgsqlCommand("select id, title, body, active from servicedesk.canned" + (everything ? "" : " where active") + " order by lower(title)", db);
            var list = new List<object>();
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) list.Add(new { id = rd.GetInt32(0), title = rd.GetString(1), body = rd.GetString(2), active = rd.GetBoolean(3) });
            return Results.Ok(list);
        }));

        static (string? Title, string? Body, bool Active, string? Error) Read(JsonElement b)
        {
            if (b.ValueKind != JsonValueKind.Object) return (null, null, true, "The body must be a JSON object.");
            var title = b.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()!.Trim() : "";
            var body = b.TryGetProperty("body", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString()!.Trim() : "";
            if (title.Length is 0 or > 80) return (null, null, true, "Give the reply a title of 1 to 80 characters.");
            if (body.Length is 0 or > 5000) return (null, null, true, "The reply text is 1 to 5000 characters.");
            var active = !(b.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.False);
            return (title, body, active, null);
        }

        admin(r.MapPost("/api/servicedesk/canned", async (JsonElement b, System.Security.Claims.ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var (title, body, active, err) = Read(b);
            if (err != null) return Bad("bad_canned", err);
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand("insert into servicedesk.canned (title, body, active, created_by) values (@t, @b, @a, @u) on conflict do nothing returning id", db);
            P(c, "t", title); P(c, "b", body); P(c, "a", active); P(c, "u", u.FindFirst("email")?.Value ?? "");
            var id = await c.ExecuteScalarAsync(ct);
            if (id == null) return Bad("duplicate", "A canned reply with that title already exists.");
            await audit.WriteAsync("servicedesk.canned_created", title, ct: ct);
            return Results.Ok(new { id = (int)id });
        }));

        admin(r.MapPut("/api/servicedesk/canned/{id:int}", async (int id, JsonElement b, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var (title, body, active, err) = Read(b);
            if (err != null) return Bad("bad_canned", err);
            await using var db = await Open(t, ct);
            await using var dup = new NpgsqlCommand("select 1 from servicedesk.canned where lower(title) = lower(@t) and id <> @i", db); P(dup, "t", title); P(dup, "i", id);
            if (await dup.ExecuteScalarAsync(ct) != null) return Bad("duplicate", "A canned reply with that title already exists.");
            await using var c = new NpgsqlCommand("update servicedesk.canned set title = @t, body = @b, active = @a, updated_at = now() where id = @i", db);
            P(c, "t", title); P(c, "b", body); P(c, "a", active); P(c, "i", id);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await audit.WriteAsync("servicedesk.canned_saved", title, ct: ct);
            return Results.Ok(new { ok = true });
        }));

        admin(r.MapDelete("/api/servicedesk/canned/{id:int}", async (int id, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand("delete from servicedesk.canned where id = @i returning title", db); P(c, "i", id);
            var title = await c.ExecuteScalarAsync(ct) as string;
            if (title == null) return Results.NotFound();
            await audit.WriteAsync("servicedesk.canned_deleted", title, ct: ct);
            return Results.Ok(new { ok = true });
        }));
    }
}
