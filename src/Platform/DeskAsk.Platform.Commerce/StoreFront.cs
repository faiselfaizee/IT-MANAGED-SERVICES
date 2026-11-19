using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace DeskAsk.Platform.Commerce;

/// <summary>
/// The public store (docs/store.md): the price list, quotes, checkout with Razorpay, the order page and free trials. No sign-in; only on the
/// vendor platform; limited per address. Prices and tax are always worked out here, never taken from the browser.
/// </summary>
public static class StoreFront
{
    static IResult No(string error, string detail, int status = 400) => Results.Json(new { error, detail }, statusCode: status);
    static string Str(JsonElement b, string name) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";

    static RouteHandlerBuilder Public(RouteHandlerBuilder b) => b.AllowAnonymous().AddEndpointFilter(async (ctx, next) =>
    {
        var sp = ctx.HttpContext.RequestServices;
        return sp.GetRequiredService<CommerceOptions>().For(sp.GetRequiredService<ITenantContext>()) ? await next(ctx) : Results.NotFound(new { error = "not_found" });
    });

    static Store.QuoteRequest? Request(JsonElement b)
    {
        if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty("plans", out var p) || p.ValueKind != JsonValueKind.Object) return null;
        var plans = new Dictionary<string, int>();
        foreach (var x in p.EnumerateObject()) if (x.Value.ValueKind == JsonValueKind.Number && x.Value.TryGetInt32(out var id)) plans[x.Name] = id;
        return new Store.QuoteRequest(Str(b, "kind"), Str(b, "period") is { Length: > 0 } per ? per : "year", plans, Str(b, "country"), Str(b, "stateCode"), Str(b, "gstin").ToUpperInvariant());
    }

    /// <summary>Why a SaaS name cannot be used, or null when it is free.</summary>
    static async Task<string?> NameProblemAsync(IServiceProvider sp, Business biz, string id, CancellationToken ct)
    {
        if (!Store.TenantId.IsMatch(id)) return "Use 2 to 31 lower case letters, digits and '-', starting with a letter.";
        if (Store.Reserved.Contains(id)) return "This name is reserved. Choose another.";
        return await sp.GetRequiredService<ITenantProvisioner>().ExistsAsync(id, Fulfilment.Host(biz, id), ct) ? "This name is already taken." : null;
    }

    static string Ip(HttpContext h) => h.Connection.RemoteIpAddress?.ToString() ?? "";

    public static void Map(IEndpointRouteBuilder r)
    {
        // what the store pages show: who sells, how to reach them, whether payments are on
        Public(r.MapGet("/api/store/info", async (CommerceOptions o, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            var biz = await Business.LoadAsync(o, ct);
            var rz = await Razorpay.LoadAsync(o, dp, ct);
            return Results.Ok(new
            {
                business = new { biz.LegalName, biz.TradeName, biz.Address, biz.City, biz.State, biz.Pincode, biz.Country, biz.Gstin, biz.Cin, biz.SupportEmail, biz.SupportPhone,
                    biz.GrievanceName, biz.GrievanceDesignation, biz.GrievanceEmail, biz.TermsUrl, biz.PrivacyUrl, biz.RefundUrl, biz.DeliveryUrl, biz.TenantDomain, biz.TrialDays },
                open = biz.Missing() == null && rz.Ready, trials = biz.Missing() == null, payments = rz.Ready ? rz.Mode : "off",
            });
        }));

        Public(r.MapGet("/api/store/plans", async (CommerceOptions o, CancellationToken ct) =>
        {
            await using var db = await CommerceDb.OpenAsync(o, ct);
            var plans = await Store.PlansAsync(db, ct);
            return Results.Ok(plans.Select(p => new
            {
                p.Id, p.Module, moduleName = Store.ModuleName(p.Module), p.SizeLabel, p.Limits,
                inr = new { year = Store.Price(p, "INR", "saas", "year"), month = Store.Price(p, "INR", "saas", "month"), onprem = Store.Price(p, "INR", "onprem", "year") },
                usd = new { year = Store.Price(p, "USD", "saas", "year"), month = Store.Price(p, "USD", "saas", "month"), onprem = Store.Price(p, "USD", "onprem", "year") },
            }));
        }));

        Public(r.MapPost("/api/store/quote", async (JsonElement b, CommerceOptions o, CancellationToken ct) =>
        {
            var req = Request(b);
            if (req == null) return No("plans", "Choose at least one module.");
            await using var db = await CommerceDb.OpenAsync(o, ct);
            var (quote, error) = Store.Make(req, await Store.PlansAsync(db, ct), await Business.LoadAsync(o, ct));
            return quote == null ? No("quote", error!) : Results.Ok(quote);
        }));

        Public(r.MapGet("/api/store/available", async (string? id, HttpContext http, CommerceOptions o, CancellationToken ct) =>
        {
            var name = (id ?? "").Trim().ToLowerInvariant();
            var biz = await Business.LoadAsync(o, ct);
            var problem = await NameProblemAsync(http.RequestServices, biz, name, ct);
            return Results.Ok(new { id = name, available = problem == null, reason = problem, address = problem == null ? "https://" + Fulfilment.Host(biz, name) : null });
        }));

        // checkout: the order is written, Razorpay makes its order, the browser opens Razorpay Checkout with it
        Public(r.MapPost("/api/store/checkout", async (JsonElement b, HttpContext http, CommerceOptions o, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            var req = Request(b);
            if (req == null) return No("plans", "Choose at least one module.");
            var biz = await Business.LoadAsync(o, ct);
            if (biz.Missing() is { } missing) return No("store_closed", "The store is not open yet (" + missing + " is not set).", 503);
            var keys = await Razorpay.LoadAsync(o, dp, ct);
            if (!keys.Ready) return No("store_closed", "Online payment is not open yet.", 503);
            var buyerEl = b.TryGetProperty("buyer", out var be) ? be : default;
            var buyer = new Invoices.Buyer(Str(buyerEl, "company"), Str(buyerEl, "name"), Str(buyerEl, "email").ToLowerInvariant(), Str(buyerEl, "phone"), Str(buyerEl, "address"),
                Str(buyerEl, "city"), req.Country.ToUpperInvariant() == "IN" ? (Store.GstinOk(req.Gstin) ? req.Gstin[..2] : req.StateCode) : "", Str(buyerEl, "stateName"), Str(buyerEl, "pincode"),
                req.Country.ToUpperInvariant(), req.Gstin);
            if (buyer.Company.Length is < 2 or > 200) return No("buyer", "Give your company name.");
            if (buyer.Name.Length is < 2 or > 120) return No("buyer", "Give your name.");
            if (!Regex.IsMatch(buyer.Email, "^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$") || buyer.Email.Length > 200) return No("buyer", "Give a valid email address.");
            if (buyer.Address.Length is < 5 or > 300) return No("buyer", "Give your billing address.");
            if (req.Gstin != "" && !Store.GstinOk(req.Gstin)) return No("buyer", "The GSTIN is not valid (15 characters, for example 33AAKCD8221B1Z3).");
            if (buyer.Phone.Length > 30) return No("buyer", "The phone number is too long.");

            await using var db = await CommerceDb.OpenAsync(o, ct);
            var (quote, error) = Store.Make(req, await Store.PlansAsync(db, ct), biz);
            if (quote == null) return No("quote", error!);

            var tenantId = Str(b, "tenantId").ToLowerInvariant();
            if (req.Kind == "saas")
            {
                var problem = await NameProblemAsync(http.RequestServices, biz, tenantId, ct);
                if (problem != null)
                {
                    // a name that is taken may be the buyer's own trial (same email): buying turns it into the paid plan
                    await using var q = new NpgsqlCommand("select 1 from commerce.subscriptions s join commerce.accounts a on a.id = s.account_id where s.kind = 'saas' and s.tenant_id = @t and s.state = 'trial' and a.email = @e", db);
                    q.Parameters.AddWithValue("t", tenantId); q.Parameters.AddWithValue("e", buyer.Email);
                    if (!Store.TenantId.IsMatch(tenantId) || await q.ExecuteScalarAsync(ct) == null) return No("tenant", problem);
                }
            }
            else tenantId = "";

            Guid accountId;
            await using (var acc = new NpgsqlCommand(@"insert into commerce.accounts (name, email, country, gstin, address, phone, state_code) values (@n, @e, @c, @g, @a, @p, @s) returning id", db))
            {
                acc.Parameters.AddWithValue("n", buyer.Company); acc.Parameters.AddWithValue("e", buyer.Email); acc.Parameters.AddWithValue("c", buyer.Country);
                acc.Parameters.AddWithValue("g", buyer.Gstin); acc.Parameters.AddWithValue("a", string.Join(", ", new[] { buyer.Address, buyer.City, buyer.StateName, buyer.Pincode }.Where(x => x != "")));
                acc.Parameters.AddWithValue("p", buyer.Phone); acc.Parameters.AddWithValue("s", buyer.StateCode);
                accountId = (Guid)(await acc.ExecuteScalarAsync(ct))!;
            }
            var access = Licences.Secret();
            Guid orderId;
            await using (var ins = new NpgsqlCommand(@"insert into commerce.orders (account_id, kind, status, lines, currency, total, tax, gateway, quote, buyer, tenant_id, access_hash)
                values (@a, @k, 'pending', @l, @c, @t, @tax, 'razorpay', @q, @b, @tid, @h) returning id", db))
            {
                ins.Parameters.AddWithValue("a", accountId); ins.Parameters.AddWithValue("k", req.Kind);
                ins.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(quote.Licence) });
                ins.Parameters.AddWithValue("c", quote.Currency); ins.Parameters.AddWithValue("t", quote.Total); ins.Parameters.AddWithValue("tax", quote.Tax);
                ins.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(quote, Licences.Json) });
                ins.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(buyer, Licences.Json) });
                ins.Parameters.AddWithValue("tid", tenantId); ins.Parameters.AddWithValue("h", Licences.Hash(access));
                orderId = (Guid)(await ins.ExecuteScalarAsync(ct))!;
            }
            var amount = (long)Math.Round(quote.Total * 100m);
            string rzOrder;
            try { rzOrder = await Razorpay.CreateOrderAsync(keys, amount, quote.Currency, orderId.ToString("N")[..32], new() { ["order"] = orderId.ToString(), ["kind"] = req.Kind, ["tenant"] = tenantId }, ct); }
            catch (Exception e) when (e is InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                await using var fail = new NpgsqlCommand("update commerce.orders set status = 'failed', error = @e where id = @i", db);
                fail.Parameters.AddWithValue("e", e.Message); fail.Parameters.AddWithValue("i", orderId); await fail.ExecuteNonQueryAsync(ct);
                return No("payment_unavailable", "Payment could not be started. Try again in a moment.", 502);
            }
            await using (var up = new NpgsqlCommand("update commerce.orders set gateway_ref = @g where id = @i", db))
            { up.Parameters.AddWithValue("g", rzOrder); up.Parameters.AddWithValue("i", orderId); await up.ExecuteNonQueryAsync(ct); }
            return Results.Ok(new
            {
                orderId, access, quote,
                razorpay = new { key = keys.KeyId, order_id = rzOrder, amount, currency = quote.Currency, name = biz.TradeName, description = string.Join(", ", quote.Lines.Select(l => Store.ModuleName(l.Module))),
                    prefill = new { name = buyer.Name, email = buyer.Email, contact = buyer.Phone } },
            });
        }));

        // the browser's Razorpay answer: the signature proves it came from Razorpay
        Public(r.MapPost("/api/store/checkout/confirm", async (JsonElement b, HttpContext http, CommerceOptions o, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            var keys = await Razorpay.LoadAsync(o, dp, ct);
            var rzOrder = Str(b, "razorpay_order_id"); var payment = Str(b, "razorpay_payment_id"); var sig = Str(b, "razorpay_signature");
            if (!Razorpay.PaymentSignatureOk(keys.Secret, rzOrder, payment, sig)) return No("signature", "The payment could not be confirmed. If money was taken, it is confirmed automatically within minutes.", 400);
            await using var db = await CommerceDb.OpenAsync(o, ct);
            var id = await Fulfilment.MarkPaidAsync(db, rzOrder, payment, ct);
            if (id == null) return No("not_found", "Order not found.", 404);
            await Fulfilment.RunAsync(http.RequestServices, id.Value, ct);
            return Results.Ok(new { orderId = id });
        }));

        // Razorpay's server tells us about payments (also when the browser closed before returning)
        Public(r.MapPost("/api/store/webhooks/razorpay", async (HttpContext http, CommerceOptions o, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            string body;
            using (var reader = new StreamReader(http.Request.Body, Encoding.UTF8)) body = await reader.ReadToEndAsync(ct);
            if (body.Length > 1_000_000) return No("too_large", "", 413);
            var keys = await Razorpay.LoadAsync(o, dp, ct);
            if (!Razorpay.WebhookSignatureOk(keys.WebhookSecret, body, http.Request.Headers["X-Razorpay-Signature"].ToString())) return No("signature", "", 400);
            JsonElement j;
            try { j = JsonDocument.Parse(body).RootElement.Clone(); } catch (JsonException) { return No("bad_json", "", 400); }
            var type = j.TryGetProperty("event", out var ev) ? ev.GetString() ?? "" : "";
            var eventId = http.Request.Headers["X-Razorpay-Event-Id"].ToString() is { Length: > 0 } eid ? eid : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..32];
            await using var db = await CommerceDb.OpenAsync(o, ct);
            await using (var seen = new NpgsqlCommand("insert into commerce.gateway_events (id, gateway, type, ok) values (@i, 'razorpay', @t, true) on conflict (id) do nothing", db))
            {
                seen.Parameters.AddWithValue("i", eventId); seen.Parameters.AddWithValue("t", type);
                if (await seen.ExecuteNonQueryAsync(ct) == 0) return Results.Ok(new { duplicate = true });
            }
            if (type is "payment.captured" or "order.paid")
            {
                var pay = j.TryGetProperty("payload", out var pl) && pl.TryGetProperty("payment", out var pm) && pm.TryGetProperty("entity", out var pe) ? pe : default;
                var orderRef = pay.ValueKind == JsonValueKind.Object && pay.TryGetProperty("order_id", out var oi) ? oi.GetString() ?? "" : "";
                if (orderRef == "" && pl.ValueKind == JsonValueKind.Object && pl.TryGetProperty("order", out var od) && od.TryGetProperty("entity", out var oe) && oe.TryGetProperty("id", out var oid)) orderRef = oid.GetString() ?? "";
                var paymentId = pay.ValueKind == JsonValueKind.Object && pay.TryGetProperty("id", out var pid) ? pid.GetString() ?? "" : "";
                if (orderRef != "" && await Fulfilment.MarkPaidAsync(db, orderRef, paymentId, ct) is Guid id) await Fulfilment.RunAsync(http.RequestServices, id, ct);
            }
            return Results.Ok(new { received = true });
        }));

        // the order page polls this: status, and when done the address or the licence key
        Public(r.MapGet("/api/store/orders/{id:guid}", async (Guid id, string? t, CommerceOptions o, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            await using var db = await CommerceDb.OpenAsync(o, ct);
            await using var q = new NpgsqlCommand(@"select o.status, o.kind, o.tenant_id, o.result, o.error, o.access_hash, o.total, o.currency, i.number, o.created_at
                from commerce.orders o left join commerce.invoices i on i.id = o.invoice_id where o.id = @i", db);
            q.Parameters.AddWithValue("i", id);
            await using var rd = await q.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct) || rd.IsDBNull(5) || !CryptographicOperations.FixedTimeEquals((byte[])rd[5], Licences.Hash(t ?? ""))) return No("not_found", "Order not found.", 404);
            var status = rd.GetString(0); var kind = rd.GetString(1); var result = rd.GetString(3);
            return Results.Ok(new
            {
                status, kind, tenantId = rd.GetString(2), total = rd.GetDecimal(6), currency = rd.GetString(7), invoice = rd.IsDBNull(8) ? null : rd.GetString(8),
                address = status == "fulfilled" && kind == "saas" ? result : null,
                licenceKey = status == "fulfilled" && kind == "onprem" && rd.GetDateTime(9) > DateTime.UtcNow.AddDays(-7) ? Fulfilment.OrderKey(dp, result) : null,
                error = status == "failed" ? "We could not finish your order automatically. It is paid and safe; our team has been told and will complete it shortly." : null,
            });
        }));

        Public(r.MapGet("/api/store/orders/{id:guid}/invoice", async (Guid id, string? t, CommerceOptions o, CancellationToken ct) =>
        {
            await using var db = await CommerceDb.OpenAsync(o, ct);
            await using var q = new NpgsqlCommand("select o.access_hash, i.number, i.pdf from commerce.orders o join commerce.invoices i on i.id = o.invoice_id where o.id = @i", db);
            q.Parameters.AddWithValue("i", id);
            await using var rd = await q.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct) || rd.IsDBNull(0) || !CryptographicOperations.FixedTimeEquals((byte[])rd[0], Licences.Hash(t ?? ""))) return No("not_found", "Invoice not found.", 404);
            return Results.File((byte[])rd[2], "application/pdf", "Invoice-" + rd.GetString(1).Replace('/', '-') + ".pdf");
        }));

        // a free trial: the customer is created at once; the set-password mail also proves the address
        Public(r.MapPost("/api/store/trial", async (JsonElement b, HttpContext http, CommerceOptions o, Signer signer, IMailQueue mail, IAuditLog audit, CancellationToken ct) =>
        {
            var biz = await Business.LoadAsync(o, ct);
            if (biz.Missing() is { } missing) return No("store_closed", "Trials are not open yet (" + missing + " is not set).", 503);
            var company = Str(b, "company"); var name = Str(b, "name"); var email = Str(b, "email").ToLowerInvariant(); var tenantId = Str(b, "tenantId").ToLowerInvariant();
            if (company.Length is < 2 or > 200) return No("company", "Give your company name.");
            if (name.Length is < 2 or > 120) return No("name", "Give your name.");
            if (!Regex.IsMatch(email, "^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$") || email.Length > 200) return No("email", "Give a valid work email address.");
            var domain = email[(email.IndexOf('@') + 1)..];
            if (Disposable.Contains(domain)) return No("email", "Use your work email address.");
            var modules = b.TryGetProperty("modules", out var ms) && ms.ValueKind == JsonValueKind.Array ? ms.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : new List<string>();
            if (!modules.Any(m => Modules.All.Contains(m))) return No("modules", "Choose at least one module to try.");
            if (await NameProblemAsync(http.RequestServices, biz, tenantId, ct) is { } problem) return No("tenant", problem);

            await using var db = await CommerceDb.OpenAsync(o, ct);
            var ip = Ip(http);
            await using (var q = new NpgsqlCommand("select count(*) from commerce.trials where (domain = @d or ip = @ip or email = @e) and created_at > now() - interval '1 day'", db))
            {
                q.Parameters.AddWithValue("d", domain); q.Parameters.AddWithValue("ip", ip); q.Parameters.AddWithValue("e", email);
                if ((long)(await q.ExecuteScalarAsync(ct))! >= 3) return No("too_many", "Too many trials were started from here today. Try again tomorrow or write to " + biz.SupportEmail + ".", 429);
            }
            var (key, _) = await signer.LoadAsync(ct);
            using (key)
            {
                if (key == null || !signer.Matches(key)) return No("store_closed", "Trials are not open yet.", 503);
                var lines = Fulfilment.TrialLines(await Store.PlansAsync(db, ct), modules);
                TenantInfo t;
                try { t = await http.RequestServices.GetRequiredService<ITenantProvisioner>().CreateAsync(tenantId, company, Fulfilment.Host(biz, tenantId), ct); }
                catch (ArgumentException e) { return No("tenant", e.Message); }
                var link = await Fulfilment.FirstAdminLinkAsync(http.RequestServices, t, email, name, ct);
                Guid accountId, subId;
                await using (var acc = new NpgsqlCommand("insert into commerce.accounts (name, email) values (@n, @e) returning id", db))
                { acc.Parameters.AddWithValue("n", company); acc.Parameters.AddWithValue("e", email); accountId = (Guid)(await acc.ExecuteScalarAsync(ct))!; }
                var ends = DateTime.UtcNow.AddDays(Math.Clamp(biz.TrialDays, 1, 90));
                await using (var ins = new NpgsqlCommand(@"insert into commerce.subscriptions (account_id, kind, tenant_id, plan, lines, period, starts_at, ends_at, grace_days, state, notes)
                    values (@a, 'saas', @t, 'trial', @l, 'manual', now(), @e, 0, 'trial', 'Free trial from the store') returning id", db))
                {
                    ins.Parameters.AddWithValue("a", accountId); ins.Parameters.AddWithValue("t", tenantId);
                    ins.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(lines) }); ins.Parameters.AddWithValue("e", ends);
                    subId = (Guid)(await ins.ExecuteScalarAsync(ct))!;
                }
                var sub = (await Licences.GetSubAsync(db, subId, ct))!;
                var (_, token) = await Licences.IssueAsync(db, null, key, sub, tenantId, "", "store", "free trial", ct);
                if (await Licences.InstallSaasAsync(o, tenantId, token, ct) is { } refused) return No("tenant", refused, 500);
                await using (var tr = new NpgsqlCommand("insert into commerce.trials (email, domain, ip, tenant_id) values (@e, @d, @ip, @t)", db))
                { tr.Parameters.AddWithValue("e", email); tr.Parameters.AddWithValue("d", domain); tr.Parameters.AddWithValue("ip", ip); tr.Parameters.AddWithValue("t", tenantId); await tr.ExecuteNonQueryAsync(ct); }
                var address = "https://" + t.Host;
                await mail.EnqueueTemplateAsync("store_trial", email, new Dictionary<string, string>
                {
                    ["name"] = name, ["company"] = company, ["address"] = address, ["password_link"] = address + link, ["days"] = biz.TrialDays.ToString(),
                    ["modules"] = string.Join(", ", lines.Keys.Select(Store.ModuleName)), ["ends"] = ends.ToString("dd MMM yyyy"),
                }, "trial-" + subId, ct);
                await audit.WriteAsync("commerce.trial_started", tenantId, company + " <" + email + ">; " + string.Join(",", lines.Keys), actor: "store", ct: ct);
                return Results.Ok(new { address, ends });
            }
        }));
    }

    static readonly HashSet<string> Disposable = new(StringComparer.OrdinalIgnoreCase)
    { "mailinator.com", "guerrillamail.com", "10minutemail.com", "tempmail.com", "temp-mail.org", "yopmail.com", "trashmail.com", "sharklasers.com", "getnada.com", "dispostable.com", "maildrop.cc", "throwawaymail.com" };
}
