using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace DeskAsk.Platform.Commerce;

/// <summary>
/// Administration › Subscription, for every customer: the plan bought (modules, sizes, period, renewal date) and its invoices, when this
/// customer was sold on this platform; and where to renew or add modules (the store on the website, Commerce:StoreUrl). On-prem
/// installations have no store data here: the page shows the licence and the store link only.
/// </summary>
public static class MySubscription
{
    static string StoreUrl(IConfiguration cfg) => cfg["Commerce:StoreUrl"] is { Length: > 0 } u && Uri.TryCreate(u, UriKind.Absolute, out var x) && x.Scheme == "https" ? u : "https://www.deskask.in/pricing";

    public static void Map(IEndpointRouteBuilder r)
    {
        r.MapGet("/api/platform/subscription", async (ITenantContext t, CommerceOptions o, IConfiguration cfg, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            var tenant = t.Require().Id;
            var subs = new List<object>(); var invoices = new List<object>();
            if (o.Enabled)
            {
                await using var db = new NpgsqlConnection(o.Connection); await db.OpenAsync(ct);
                try
                {
                    await using (var c = new NpgsqlCommand(@"select id, kind, state, period, lines::text, starts_at, ends_at, grace_days from commerce.subscriptions where tenant_id = @t order by ends_at desc limit 20", db))
                    {
                        c.Parameters.AddWithValue("t", tenant);
                        await using var rd = await c.ExecuteReaderAsync(ct);
                        while (await rd.ReadAsync(ct))
                            subs.Add(new { id = rd.GetGuid(0), kind = rd.GetString(1), state = rd.GetString(2), period = rd.GetString(3), lines = System.Text.Json.JsonDocument.Parse(rd.GetString(4)).RootElement.Clone(),
                                startsAt = rd.GetDateTime(5), endsAt = rd.GetDateTime(6), graceDays = rd.GetInt32(7) });
                    }
                    await using (var c = new NpgsqlCommand(@"select i.id, i.number, i.issued_at, i.currency, i.total from commerce.invoices i join commerce.orders od on od.id = i.order_id
                        left join commerce.subscriptions s on s.id = od.subscription_id where od.tenant_id = @t or s.tenant_id = @t order by i.issued_at desc limit 100", db))
                    {
                        c.Parameters.AddWithValue("t", tenant);
                        await using var rd = await c.ExecuteReaderAsync(ct);
                        while (await rd.ReadAsync(ct)) invoices.Add(new { id = rd.GetInt64(0), number = rd.GetString(1), issuedAt = rd.GetDateTime(2), currency = rd.GetString(3), total = rd.GetDecimal(4) });
                    }
                }
                catch (PostgresException e) when (e.SqlState == "42P01") { }   // the store was never set up here
            }
            // with the website linked, Renew and Change open its checkout filled in for this customer (a renewal link valid 3 days)
            string storeUrl = StoreUrl(cfg), renewUrl = storeUrl, changeUrl = storeUrl;
            if (o.Enabled && await Link.LoadAsync(o, ct) is { WebsiteUrl.Length: > 0 } ws)
            {
                var token = Uri.EscapeDataString(Link.RenewToken(dp, tenant));
                storeUrl = ws.WebsiteUrl + "/pricing"; renewUrl = storeUrl + "?renew=" + token; changeUrl = renewUrl + "&intent=change";
            }
            return Results.Ok(new { store = o.Enabled, storeUrl, renewUrl, changeUrl, subscriptions = subs, invoices });
        }).RequireAuthorization(p => p.RequireRole("admin"));

        r.MapGet("/api/platform/subscription/invoices/{id:long}/pdf", async (long id, ITenantContext t, CommerceOptions o, CancellationToken ct) =>
        {
            if (!o.Enabled) return Results.NotFound();
            await using var db = new NpgsqlConnection(o.Connection); await db.OpenAsync(ct);
            await using var c = new NpgsqlCommand(@"select i.number, i.pdf from commerce.invoices i join commerce.orders od on od.id = i.order_id
                left join commerce.subscriptions s on s.id = od.subscription_id where i.id = @i and (od.tenant_id = @t or s.tenant_id = @t)", db);
            c.Parameters.AddWithValue("i", id); c.Parameters.AddWithValue("t", t.Require().Id);
            await using var rd = await c.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Results.NotFound();
            return Results.File((byte[])rd.GetValue(1), "application/pdf", rd.GetString(0).Replace('/', '-') + ".pdf");
        }).RequireAuthorization(p => p.RequireRole("admin"));
    }
}
