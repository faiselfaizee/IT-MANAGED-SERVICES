using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace DeskAsk.Platform.Assets;

/// <summary>
/// Required and prohibited software (6.22.0). A repository program can be marked Required (every computer of its system should have it) or Prohibited
/// (none should). The program's "name in the inventory" is matched against the installed programs the agent last reported, ignoring upper and lower case.
/// Computers that never reported a program list are left out, and so are retired and lost ones.
/// </summary>
public static class SoftwareRulesApi
{
    public static void Map(IEndpointRouteBuilder r, string key)
    {
        r.MapGet("/api/assets/packages/violations", async (HttpContext http, ITenantContext t, int? company, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read);
            await using var c = new NpgsqlCommand(@"
                with rules as (select p.id, p.name, p.version, p.os, p.rule, coalesce(nullif(p.match_name, ''), p.name) as match from assets.packages p where p.enabled and p.rule in ('required', 'prohibited')),
                reporting as (select a.id, a.tag, a.name, a.company_id, a.os_family, case when jsonb_typeof(a.installed_apps) = 'array' then a.installed_apps else '[]'::jsonb end as apps from assets.assets a
                               where a.status not in ('retired', 'lost') and jsonb_array_length(case when jsonb_typeof(a.installed_apps) = 'array' then a.installed_apps else '[]'::jsonb end) > 0
                                 and (not @sc or a.company_id = any(@scope)) and (@co = 0 or a.company_id = @co))
                select r.id, r.name, r.rule, a.id, a.tag, a.name, coalesce(co.name, ''),
                       coalesce((select e->>'version' from jsonb_array_elements(a.apps) e where position(lower(r.match) in lower(coalesce(e->>'name', ''))) > 0 limit 1), '')
                  from rules r join reporting a on a.os_family = r.os left join assets.companies co on co.id = a.company_id
                 where (r.rule = 'prohibited' and exists (select 1 from jsonb_array_elements(a.apps) e where position(lower(r.match) in lower(coalesce(e->>'name', ''))) > 0))
                    or (r.rule = 'required' and not exists (select 1 from jsonb_array_elements(a.apps) e where position(lower(r.match) in lower(coalesce(e->>'name', ''))) > 0))
                 order by r.rule, lower(r.name), lower(a.name) limit 1000", db);
            AssetsAccess.Bind(c, scope); AgentsApi.P(c, "co", company ?? 0);
            var items = new List<object>();
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                items.Add(new { packageId = rd.GetInt32(0), program = rd.GetString(1), rule = rd.GetString(2), assetId = rd.GetInt64(3), tag = rd.GetString(4), asset = rd.GetString(5), company = rd.GetString(6), version = rd.GetString(7) });
            return Results.Ok(new { items, truncated = items.Count == 1000 });
        }).RequireAuthorization(p => p.RequireRole(AssetsAccess.Readers)).RequireModule(key).RequireAccess(key, Acts.Read);
    }
}
