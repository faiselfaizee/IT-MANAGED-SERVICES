using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.Commerce;

/// <summary>The store's part of the vendor console: business details, payment keys, orders, invoices, trials.</summary>
public static class StoreConsole
{
    static IResult Bad(string error, string detail, int status = 400) => Results.Json(new { error, detail }, statusCode: status);
    static RouteHandlerBuilder Console(RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).AddEndpointFilter(async (ctx, next) =>
    {
        var sp = ctx.HttpContext.RequestServices;
        return sp.GetRequiredService<CommerceOptions>().For(sp.GetRequiredService<ITenantContext>()) ? await next(ctx) : Results.NotFound(new { error = "not_found" });
    });

    public static void Map(IEndpointRouteBuilder r)
    {
        Console(r.MapGet("/api/commerce/business", async (CommerceOptions o, CancellationToken ct) => Results.Ok(await Business.LoadAsync(o, ct))));

        Console(r.MapPut("/api/commerce/business", async (JsonElement b, CommerceOptions o, IAuditLog audit, CancellationToken ct) =>
        {
            Business biz;
            try { biz = JsonSerializer.Deserialize<Business>(b.GetRawText(), Licences.Json) ?? new Business(); } catch (JsonException) { return Bad("bad_json", "The details could not be read."); }
            biz = biz with
            {
                LegalName = biz.LegalName.Trim(), Address = biz.Address.Trim(), Gstin = biz.Gstin.Trim().ToUpperInvariant(), Cin = biz.Cin.Trim().ToUpperInvariant(), Pan = biz.Pan.Trim().ToUpperInvariant(),
                SupportEmail = biz.SupportEmail.Trim().ToLowerInvariant(), GrievanceEmail = biz.GrievanceEmail.Trim().ToLowerInvariant(), TenantDomain = biz.TenantDomain.Trim().Trim('.').ToLowerInvariant(),
            };
            if (biz.Gstin != "" && !Store.GstinOk(biz.Gstin)) return Bad("gstin", "The GSTIN is not valid (15 characters, for example 33AAKCD8221B1Z3).");
            if (biz.Sac != "" && !Regex.IsMatch(biz.Sac, "^[0-9]{6}$")) return Bad("sac", "The SAC code has 6 digits (998315 for software).");
            if (!Regex.IsMatch(biz.TenantDomain, "^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$")) return Bad("domain", "The domain for customer addresses is not valid (for example deskask.in).");
            if (biz.TrialDays is < 1 or > 90) return Bad("trial", "A trial lasts 1 to 90 days.");
            if (biz.TrialKeepDays is < 8 or > 365) return Bad("trial", "Unconverted trials are kept 8 to 365 days.");
            foreach (var u in new[] { biz.TermsUrl, biz.PrivacyUrl, biz.RefundUrl, biz.DeliveryUrl })
                if (u != "" && !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return Bad("url", "Links to the legal pages start with https://.");
            if ((biz.LegalName + biz.Address + biz.City + biz.State).Length > 1000) return Bad("long", "The details are too long.");
            await CommerceDb.SetSettingAsync(o, "business", JsonSerializer.Serialize(biz, Licences.Json), ct);
            await audit.WriteAsync("commerce.business_changed", biz.LegalName, biz.Address + "; GSTIN " + biz.Gstin, ct: ct);
            return Results.Ok(biz);
        }));

        Console(r.MapGet("/api/commerce/payments", async (CommerceOptions o, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            var k = await Razorpay.LoadAsync(o, dp, ct);
            return Results.Ok(new { razorpay = new { keyId = k.KeyId, hasSecret = k.Secret != "", hasWebhookSecret = k.WebhookSecret != "", mode = k.KeyId == "" ? "off" : k.Mode, webhookPath = "/api/store/webhooks/razorpay" } });
        }));

        Console(r.MapPut("/api/commerce/payments", async (JsonElement b, CommerceOptions o, IDataProtectionProvider dp, IAuditLog audit, CancellationToken ct) =>
        {
            string S(string n) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
            var keyId = S("keyId");
            if (keyId != "" && !Regex.IsMatch(keyId, "^rzp_(test|live)_[A-Za-z0-9]{8,40}$")) return Bad("key", "A Razorpay key id looks like rzp_test_XXXXXXXX or rzp_live_XXXXXXXX.");
            var secret = S("keySecret"); var hook = S("webhookSecret");
            if (secret.Length > 200 || hook.Length > 200) return Bad("secret", "The secret is too long.");
            await Razorpay.SaveAsync(o, dp, keyId, secret, hook, ct);
            await audit.WriteAsync("commerce.payments_changed", keyId, (secret != "" ? "key secret changed; " : "") + (hook != "" ? "webhook secret changed" : ""), ct: ct);
            var k = await Razorpay.LoadAsync(o, dp, ct);
            return Results.Ok(new { razorpay = new { keyId = k.KeyId, hasSecret = k.Secret != "", hasWebhookSecret = k.WebhookSecret != "", mode = k.KeyId == "" ? "off" : k.Mode } });
        }));

        Console(r.MapGet("/api/commerce/orders", async (string? status, CommerceOptions o, CancellationToken ct) =>
        {
            await using var db = await CommerceDb.OpenAsync(o, ct);
            await using var q = new NpgsqlCommand(@"select o.id, o.created_at, o.status, o.kind, o.tenant_id, o.currency, o.total, o.tax, o.error, o.payment_ref, o.subscription_id, a.name, a.email, i.id, i.number
                from commerce.orders o left join commerce.accounts a on a.id = o.account_id left join commerce.invoices i on i.id = o.invoice_id
                where @s = '' or o.status = @s order by o.created_at desc limit 200", db);
            q.Parameters.AddWithValue("s", (status ?? "").Trim());
            await using var rd = await q.ExecuteReaderAsync(ct);
            var list = new List<object>();
            while (await rd.ReadAsync(ct))
                list.Add(new
                {
                    id = rd.GetGuid(0), createdAt = rd.GetDateTime(1), status = rd.GetString(2), kind = rd.GetString(3), tenantId = rd.GetString(4), currency = rd.GetString(5), total = rd.GetDecimal(6), tax = rd.GetDecimal(7),
                    error = rd.GetString(8), paymentRef = rd.GetString(9), subscriptionId = rd.IsDBNull(10) ? (Guid?)null : rd.GetGuid(10), company = rd.IsDBNull(11) ? "" : rd.GetString(11), email = rd.IsDBNull(12) ? "" : rd.GetString(12),
                    invoiceId = rd.IsDBNull(13) ? (long?)null : rd.GetInt64(13), invoice = rd.IsDBNull(14) ? null : rd.GetString(14),
                });
            return Results.Ok(list);
        }));

        // try again after a failure (the order is paid; the work did not finish)
        Console(r.MapPost("/api/commerce/orders/{id:guid}/retry", async (Guid id, HttpContext http, CommerceOptions o, IAuditLog audit, CancellationToken ct) =>
        {
            await using (var db = await CommerceDb.OpenAsync(o, ct))
            await using (var up = new NpgsqlCommand("update commerce.orders set status = 'paid', error = '' where id = @i and status = 'failed' and paid_at is not null", db))
            {
                up.Parameters.AddWithValue("i", id);
                if (await up.ExecuteNonQueryAsync(ct) == 0) return Bad("not_failed", "Only a paid order that failed can be tried again.", 409);
            }
            await Fulfilment.RunAsync(http.RequestServices, id, ct);
            await audit.WriteAsync("commerce.order_retried", id.ToString(), "", ct: ct);
            await using var db2 = await CommerceDb.OpenAsync(o, ct);
            await using var q = new NpgsqlCommand("select status, error from commerce.orders where id = @i", db2);
            q.Parameters.AddWithValue("i", id);
            await using var rd = await q.ExecuteReaderAsync(ct);
            await rd.ReadAsync(ct);
            return Results.Ok(new { status = rd.GetString(0), error = rd.GetString(1) });
        }));

        Console(r.MapGet("/api/commerce/invoices", async (CommerceOptions o, CancellationToken ct) =>
        {
            await using var db = await CommerceDb.OpenAsync(o, ct);
            await using var q = new NpgsqlCommand(@"select id, number, issued_at, currency, subtotal, tax, total, buyer->>'company', buyer->>'gstin', order_id from commerce.invoices order by issued_at desc limit 500", db);
            await using var rd = await q.ExecuteReaderAsync(ct);
            var list = new List<object>();
            while (await rd.ReadAsync(ct))
                list.Add(new { id = rd.GetInt64(0), number = rd.GetString(1), issuedAt = rd.GetDateTime(2), currency = rd.GetString(3), subtotal = rd.GetDecimal(4), tax = rd.GetDecimal(5), total = rd.GetDecimal(6),
                    company = rd.IsDBNull(7) ? "" : rd.GetString(7), gstin = rd.IsDBNull(8) ? "" : rd.GetString(8), orderId = rd.IsDBNull(9) ? (Guid?)null : rd.GetGuid(9) });
            return Results.Ok(list);
        }));

        Console(r.MapGet("/api/commerce/invoices/{id:long}/pdf", async (long id, CommerceOptions o, CancellationToken ct) =>
        {
            await using var db = await CommerceDb.OpenAsync(o, ct);
            await using var q = new NpgsqlCommand("select number, pdf from commerce.invoices where id = @i", db);
            q.Parameters.AddWithValue("i", id);
            await using var rd = await q.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Results.NotFound(new { error = "not_found" });
            return Results.File((byte[])rd[1], "application/pdf", "Invoice-" + rd.GetString(0).Replace('/', '-') + ".pdf");
        }));

        // give a trial (or any subscription) more days; SaaS customers get the new licence at once
        Console(r.MapPost("/api/commerce/subscriptions/{id:guid}/extend", async (Guid id, JsonElement b, System.Security.Claims.ClaimsPrincipal u, CommerceOptions o, Signer signer, IAuditLog audit, CancellationToken ct) =>
        {
            var days = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("days", out var d) && d.ValueKind == JsonValueKind.Number && d.TryGetInt32(out var n) ? n : 0;
            if (days is < 1 or > 365) return Bad("days", "Extend by 1 to 365 days.");
            await using var db = await CommerceDb.OpenAsync(o, ct);
            var s = await Licences.GetSubAsync(db, id, ct);
            if (s == null) return Results.NotFound(new { error = "not_found" });
            if (s.State == "ended") return Bad("ended", "This subscription has ended.");
            var (key, _) = await signer.LoadAsync(ct);
            using (key)
            {
                if (key == null || !signer.Matches(key)) return Bad("no_signing_key", "Import the licence signing key first (Vendor console › Signing key).", 409);
                var from = s.EndsAt < DateTime.UtcNow ? DateTime.UtcNow : s.EndsAt;
                await using (var up = new NpgsqlCommand("update commerce.subscriptions set ends_at = @e, trial_warned_at = null, updated_at = now() where id = @i", db))
                { up.Parameters.AddWithValue("e", from.AddDays(days)); up.Parameters.AddWithValue("i", id); await up.ExecuteNonQueryAsync(ct); }
                var fresh = (await Licences.GetSubAsync(db, id, ct))!;
                if (fresh.Kind == "saas")
                {
                    var who = u.FindFirst("email")?.Value ?? u.Identity?.Name ?? "vendor";
                    var (_, token) = await Licences.IssueAsync(db, null, key, fresh, fresh.TenantId, "", who, "extended by " + days + " days", ct);
                    if (await Licences.InstallSaasAsync(o, fresh.TenantId, token, ct) is { } refused) return Bad("tenant", refused);
                }
                await audit.WriteAsync("commerce.subscription_extended", fresh.Kind + " " + fresh.TenantId, days + " days; now ends " + fresh.EndsAt.ToString("yyyy-MM-dd"), ct: ct);
                return Results.Ok(new { endsAt = fresh.EndsAt });
            }
        }));

        // remove a trial customer (or an ended SaaS customer) and its database; cannot be undone
        Console(r.MapPost("/api/commerce/subscriptions/{id:guid}/delete-customer", async (Guid id, JsonElement b, HttpContext http, CommerceOptions o, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await CommerceDb.OpenAsync(o, ct);
            var s = await Licences.GetSubAsync(db, id, ct);
            if (s == null) return Results.NotFound(new { error = "not_found" });
            if (s.Kind != "saas" || s.TenantId == "" || s.TenantId == o.VendorTenant) return Bad("kind", "Only SaaS customers on this platform can be deleted here.");
            if (s.State is not ("trial" or "ended")) return Bad("state", "End the subscription first. Only trials and ended subscriptions can be deleted.");
            var confirm = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("confirm", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : "";
            if (confirm != s.TenantId) return Bad("confirm", "Type the customer id to confirm.");
            await http.RequestServices.GetRequiredService<ITenantProvisioner>().DeleteAsync(s.TenantId, ct);
            await using (var up = new NpgsqlCommand("update commerce.subscriptions set state = 'ended', notes = notes || ' Customer and data deleted ' || to_char(now(), 'YYYY-MM-DD') || '.', updated_at = now() where id = @i", db))
            { up.Parameters.AddWithValue("i", id); await up.ExecuteNonQueryAsync(ct); }
            await audit.WriteAsync("commerce.customer_deleted", s.TenantId, "database dropped", ct: ct);
            return Results.Ok(new { deleted = true });
        }));
    }
}
