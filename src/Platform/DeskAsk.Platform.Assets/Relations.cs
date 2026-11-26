using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace DeskAsk.Platform.Assets;

/// <summary>
/// CMDB relationships (6.22.0): which asset runs on, depends on, is part of or is connected to which. "What is affected if this one fails?" walks the links
/// (up to four steps) and answers with the assets that would suffer. A runs on B, A depends on B and A is part of B mean A suffers when B fails;
/// connected to counts both ways. Tickets and changes show the answer for their asset.
/// </summary>
public static class RelationsApi
{
    public static readonly string[] Kinds = { "runs_on", "depends_on", "part_of", "connected_to" };

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        var read = (RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole(AssetsAccess.Readers)).RequireModule(key).RequireAccess(key, Acts.Read);
        var write = (RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin", "engineer")).RequireModule(key).RequireAccess(key, Acts.Modify);

        read(r.MapGet("/api/assets/{id:long}/relations", async (long id, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read);
            if (!await AssetsAccess.InScope(db, id, scope, ct)) return Results.NotFound();
            var outgoing = new List<object>(); var incoming = new List<object>();
            await using var c = new NpgsqlCommand(@"select r.id, r.kind, r.note, r.from_id, r.to_id, o.tag, o.name, coalesce(co.name, '')
                from assets.relations r join assets.assets o on o.id = case when r.from_id = @i then r.to_id else r.from_id end left join assets.companies co on co.id = o.company_id
                where (r.from_id = @i or r.to_id = @i) and (not @sc or o.company_id = any(@scope)) order by r.kind, lower(o.name)", db);
            AgentsApi.P(c, "i", id); AssetsAccess.Bind(c, scope);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var row = new { id = rd.GetInt64(0), kind = rd.GetString(1), note = rd.GetString(2), assetId = rd.GetInt64(3) == id ? rd.GetInt64(4) : rd.GetInt64(3), tag = rd.GetString(5), name = rd.GetString(6), company = rd.GetString(7) };
                (rd.GetInt64(3) == id ? outgoing : incoming).Add(row);
            }
            return Results.Ok(new { outgoing, incoming });
        }));

        write(r.MapPost("/api/assets/{id:long}/relations", async (long id, JsonElement b, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty("toId", out var to) || to.ValueKind != JsonValueKind.Number || !to.TryGetInt64(out var toId)) return AgentsApi.Bad("bad_input", "Choose the asset to link to.");
            var kind = b.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString()! : "";
            if (!Kinds.Contains(kind)) return AgentsApi.Bad("bad_input", "kind must be runs_on, depends_on, part_of or connected_to");
            if (toId == id) return AgentsApi.Bad("bad_input", "An asset cannot be linked to itself.");
            var note = b.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()!.Trim() : "";
            if (note.Length > 200 || note.Any(char.IsControl)) return AgentsApi.Bad("bad_input", "A note is at most 200 characters, on one line.");
            await using var db = await AgentsApi.Open(t, ct);
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Modify);
            if (!await AssetsAccess.InScope(db, id, scope, ct) || !await AssetsAccess.InScope(db, toId, scope, ct)) return Results.NotFound();
            await using var c = new NpgsqlCommand(@"insert into assets.relations (from_id, to_id, kind, note, by_name) values (@f, @t, @k, @n, @by) on conflict do nothing returning id", db);
            AgentsApi.P(c, "f", id); AgentsApi.P(c, "t", toId); AgentsApi.P(c, "k", kind); AgentsApi.P(c, "n", note); AgentsApi.P(c, "by", AgentsApi.Actor(http.User));
            var rid = await c.ExecuteScalarAsync(ct);
            if (rid == null) return AgentsApi.Bad("duplicate", "These two assets are already linked that way.");
            await audit.WriteAsync("assets.relation_added", id.ToString(), $"{kind} {toId}", ct: ct);
            return Results.Ok(new { id = (long)rid });
        }));

        write(r.MapDelete("/api/assets/relations/{rid:long}", async (long rid, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Modify);
            await using var c = new NpgsqlCommand(@"delete from assets.relations r using assets.assets a, assets.assets b where r.id = @r and a.id = r.from_id and b.id = r.to_id
                and (not @sc or (a.company_id = any(@scope) and b.company_id = any(@scope))) returning r.from_id, r.kind, r.to_id", db);
            AgentsApi.P(c, "r", rid); AssetsAccess.Bind(c, scope);
            await using var rd = await c.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Results.NotFound();
            var what = $"{rd.GetInt64(0)} {rd.GetString(1)} {rd.GetInt64(2)}";
            await rd.DisposeAsync();
            await audit.WriteAsync("assets.relation_removed", rid.ToString(), what, ct: ct);
            return Results.NoContent();
        }));

        read(r.MapGet("/api/assets/{id:long}/impact", async (long id, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read);
            if (!await AssetsAccess.InScope(db, id, scope, ct)) return Results.NotFound();
            await using var c = new NpgsqlCommand(@"
                with recursive edge(src, dst, kind) as (
                  select to_id, from_id, kind from assets.relations where kind in ('runs_on', 'depends_on', 'part_of')
                  union all select to_id, from_id, kind from assets.relations where kind = 'connected_to'
                  union all select from_id, to_id, kind from assets.relations where kind = 'connected_to'
                ), walk(id, depth, kind, path) as (
                  select e.dst, 1, e.kind, array[@i::bigint, e.dst] from edge e where e.src = @i
                  union all
                  select e.dst, w.depth + 1, e.kind, w.path || e.dst from walk w join edge e on e.src = w.id where w.depth < 4 and e.dst <> all(w.path)
                )
                select a.id, a.tag, a.name, coalesce(co.name, ''), min(w.depth), (array_agg(w.kind order by w.depth))[1],
                       (select count(*) from servicedesk.tickets k where k.asset_id = a.id and k.status not in ('resolved', 'closed'))
                  from walk w join assets.assets a on a.id = w.id left join assets.companies co on co.id = a.company_id
                 where a.id <> @i and (not @sc or a.company_id = any(@scope))
                 group by a.id, a.tag, a.name, co.name order by min(w.depth), lower(a.name) limit 200", db);
            AgentsApi.P(c, "i", id); AssetsAccess.Bind(c, scope);
            var items = new List<object>();
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) items.Add(new { id = rd.GetInt64(0), tag = rd.GetString(1), name = rd.GetString(2), company = rd.GetString(3), depth = rd.GetInt32(4), kind = rd.GetString(5), openTickets = rd.GetInt64(6) });
            return Results.Ok(new { items });
        }));
    }
}
