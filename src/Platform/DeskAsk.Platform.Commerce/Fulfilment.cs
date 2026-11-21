using System.Text.Json;
using DeskAsk.Platform.Tenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace DeskAsk.Platform.Commerce;

/// <summary>
/// What happens after a payment: a SaaS order creates the customer (or turns their trial into a paid plan) and licenses it; an on-prem order
/// makes a licence key; every paid order gets an invoice. Runs in the vendor customer's scope (mail and audit go there). An order is claimed
/// before the work starts, so the checkout answer and the webhook never fulfil it twice.
/// </summary>
public static class Fulfilment
{
    const string KeyPurpose = "commerce.order-key";

    public static string Host(Business biz, string tenantId) => tenantId + "." + (biz.TenantDomain.Trim('.') is { Length: > 0 } d ? d : "deskask.in");

    /// <summary>Marks an order paid (once) by its Razorpay order id. Returns the DeskAsk order id, or null when unknown.</summary>
    public static async Task<Guid?> MarkPaidAsync(NpgsqlConnection db, string gatewayOrderId, string paymentId, CancellationToken ct)
    {
        await using var up = new NpgsqlCommand(@"update commerce.orders set status = 'paid', paid_at = now(), payment_ref = @p, updated_at = now()
            where gateway = 'razorpay' and gateway_ref = @g and status = 'pending' returning id", db);
        up.Parameters.AddWithValue("p", paymentId); up.Parameters.AddWithValue("g", gatewayOrderId);
        if (await up.ExecuteScalarAsync(ct) is Guid id) return id;
        await using var q = new NpgsqlCommand("select id from commerce.orders where gateway = 'razorpay' and gateway_ref = @g", db);
        q.Parameters.AddWithValue("g", gatewayOrderId);
        return await q.ExecuteScalarAsync(ct) as Guid?;
    }

    static string ModulesText(Dictionary<string, Dictionary<string, int>> lines) =>
        string.Join(", ", lines.Select(l => Store.ModuleName(l.Key) + (l.Value.Count == 0 ? "" : " (" + string.Join(", ", l.Value.Select(v => v.Value + " " + v.Key.Replace("max_", ""))) + ")")));

    /// <summary>Does the work for a paid order. Safe to call more than once.</summary>
    public static async Task RunAsync(IServiceProvider sp, Guid orderId, CancellationToken ct)
    {
        var o = sp.GetRequiredService<CommerceOptions>();
        await using var db = await CommerceDb.OpenAsync(o, ct);
        string kind, tenantId, quoteJson, buyerJson, paymentRef, source, websiteRef; Guid accountId;
        await using (var claim = new NpgsqlCommand(@"update commerce.orders set status = 'fulfilling', updated_at = now() where id = @i and status = 'paid'
            returning kind, tenant_id, quote::text, buyer::text, account_id, payment_ref, source, website_ref", db))
        {
            claim.Parameters.AddWithValue("i", orderId);
            await using var rd = await claim.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return;   // not paid yet, or already being done
            (kind, tenantId, quoteJson, buyerJson, accountId, paymentRef) = (rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetGuid(4), rd.GetString(5));
            (source, websiteRef) = (rd.GetString(6), rd.GetString(7));
        }
        string? invoiceNumber = null, outcome = null;
        try
        {
            var quote = JsonSerializer.Deserialize<Store.Quote>(quoteJson, Licences.Json)!;
            var buyer = JsonSerializer.Deserialize<Invoices.Buyer>(buyerJson, Licences.Json)!;
            var biz = await Business.LoadAsync(o, ct);
            var signer = sp.GetRequiredService<Signer>();
            var mail = sp.GetRequiredService<IMailQueue>();
            var (key, _) = await signer.LoadAsync(ct);
            using (key)
            {
                if (key == null || !signer.Matches(key)) throw new InvalidOperationException("The licence signing key is not set up.");
                var now = DateTime.UtcNow;
                var ends = quote.Period == "year" ? now.AddYears(1) : now.AddMonths(1);
                Guid subId; string result = "";
                if (kind == "saas")
                {
                    // a trial of the same customer becomes the paid plan; otherwise the customer is created now
                    Guid? trialId = null;
                    await using (var q = new NpgsqlCommand("select id, state, ends_at from commerce.subscriptions where kind = 'saas' and tenant_id = @t and state in ('trial', 'active', 'cancelled') order by created_at desc limit 1", db))
                    {
                        q.Parameters.AddWithValue("t", tenantId);
                        await using var rd = await q.ExecuteReaderAsync(ct);
                        if (await rd.ReadAsync(ct))
                        {
                            trialId = rd.GetGuid(0);
                            // a renewal before the paid period ends adds the new period after it, so nothing already paid for is lost
                            var paidTo = DateTime.SpecifyKind(rd.GetDateTime(2), DateTimeKind.Utc);
                            if (rd.GetString(1) == "active" && paidTo > now) ends = quote.Period == "year" ? paidTo.AddYears(1) : paidTo.AddMonths(1);
                        }
                    }
                    string? passwordLink = null;
                    if (trialId == null)
                    {
                        var t = await sp.GetRequiredService<ITenantProvisioner>().CreateAsync(tenantId, buyer.Company, Host(biz, tenantId), ct);
                        passwordLink = await FirstAdminLinkAsync(sp, t, buyer.Email, buyer.Name, ct);
                    }
                    subId = await SaveSubscriptionAsync(db, trialId, accountId, "saas", tenantId, quote, now, ends, 7, ct);
                    var sub = (await Licences.GetSubAsync(db, subId, ct))!;
                    var (_, token) = await Licences.IssueAsync(db, null, key, sub, tenantId, "", "store", "order " + orderId, ct);
                    if (await Licences.InstallSaasAsync(o, tenantId, token, ct) is { } refused) throw new InvalidOperationException(refused);
                    var address = "https://" + Host(biz, tenantId);
                    await mail.EnqueueTemplateAsync("store_welcome", buyer.Email, new Dictionary<string, string>
                    {
                        ["name"] = buyer.Name, ["company"] = buyer.Company, ["address"] = address,
                        ["password_link"] = passwordLink != null ? address + passwordLink : address + " (sign in as before)", ["modules"] = ModulesText(quote.Licence), ["ends"] = ends.ToString("dd MMM yyyy"),
                    }, "store-" + orderId, ct);
                    result = address;
                }
                else
                {
                    subId = await SaveSubscriptionAsync(db, null, accountId, "onprem", "", quote, now, ends, 14, ct);
                    var licenceKey = Licences.NewKey(); var norm = Licences.NormalizeKey(licenceKey);
                    await using (var ins = new NpgsqlCommand("insert into commerce.licence_keys (subscription_id, key_hash, hint, max_activations) values (@s, @h, @hint, 1)", db))
                    { ins.Parameters.AddWithValue("s", subId); ins.Parameters.AddWithValue("h", Licences.Hash(norm)); ins.Parameters.AddWithValue("hint", norm[^4..]); await ins.ExecuteNonQueryAsync(ct); }
                    await mail.EnqueueTemplateAsync("store_licence_key", buyer.Email, new Dictionary<string, string>
                    { ["name"] = buyer.Name, ["company"] = buyer.Company, ["key"] = licenceKey, ["modules"] = ModulesText(quote.Licence), ["ends"] = ends.ToString("dd MMM yyyy") }, "store-" + orderId, ct);
                    result = sp.GetRequiredService<IDataProtectionProvider>().CreateProtector(KeyPurpose).Protect(licenceKey);
                }

                // the invoice
                var number = await Invoices.NextNumberAsync(db, null, biz.InvoicePrefix, now, ct);
                var pdf = Invoices.Pdf(biz, number, now, buyer, quote, paymentRef, PdfStyle.Parse(await CommerceDb.GetSettingAsync(o, "pdf_style", ct)));
                long invoiceId;
                await using (var ins = new NpgsqlCommand(@"insert into commerce.invoices (number, order_id, account_id, currency, subtotal, tax, total, buyer, lines, pdf)
                    values (@n, @o, @a, @c, @s, @t, @tot, @b, @l, @p) returning id", db))
                {
                    ins.Parameters.AddWithValue("n", number); ins.Parameters.AddWithValue("o", orderId); ins.Parameters.AddWithValue("a", accountId); ins.Parameters.AddWithValue("c", quote.Currency);
                    ins.Parameters.AddWithValue("s", quote.Subtotal); ins.Parameters.AddWithValue("t", quote.Tax); ins.Parameters.AddWithValue("tot", quote.Total);
                    ins.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Jsonb) { Value = buyerJson });
                    ins.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(quote.Lines, Licences.Json) });
                    ins.Parameters.AddWithValue("p", pdf);
                    invoiceId = (long)(await ins.ExecuteScalarAsync(ct))!;
                }
                await mail.EnqueueTemplateAsync("store_invoice", buyer.Email, new Dictionary<string, string>
                { ["name"] = buyer.Name, ["company"] = buyer.Company, ["number"] = number, ["total"] = Store.Money(quote.Total, quote.Currency) },
                    new[] { new MailFile("Invoice-" + number.Replace('/', '-') + ".pdf", "application/pdf", pdf) }, "store-" + orderId, ct);

                await using (var done = new NpgsqlCommand(@"update commerce.orders set status = 'fulfilled', fulfilled_at = now(), subscription_id = @s, invoice_id = @inv, result = @r, error = '', updated_at = now() where id = @i", db))
                {
                    done.Parameters.AddWithValue("s", subId); done.Parameters.AddWithValue("inv", invoiceId); done.Parameters.AddWithValue("r", result); done.Parameters.AddWithValue("i", orderId);
                    await done.ExecuteNonQueryAsync(ct);
                }
                invoiceNumber = number; outcome = kind == "saas" ? result : "";
                await sp.GetRequiredService<IAuditLog>().WriteAsync("commerce.order_fulfilled", kind + " " + tenantId, "order " + orderId + "; invoice " + number + "; " + Store.Money(quote.Total, quote.Currency), actor: "store", ct: ct);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            sp.GetService<ILoggerFactory>()?.CreateLogger("DeskAsk.Store").LogError(e, "Order {Order} could not be completed", orderId);
            await using var fail = new NpgsqlCommand("update commerce.orders set status = 'failed', error = @e, updated_at = now() where id = @i", db);
            fail.Parameters.AddWithValue("e", e.Message.Length > 500 ? e.Message[..500] : e.Message); fail.Parameters.AddWithValue("i", orderId);
            await fail.ExecuteNonQueryAsync(CancellationToken.None);
        }
        // an order placed on the website: tell the website how it went
        if (source == "website")
        {
            await using var st = new NpgsqlCommand("select status, total, currency from commerce.orders where id = @i", db);
            st.Parameters.AddWithValue("i", orderId);
            string status = "failed"; decimal total = 0; string currency = "";
            await using (var rd = await st.ExecuteReaderAsync(CancellationToken.None)) if (await rd.ReadAsync(CancellationToken.None)) (status, total, currency) = (rd.GetString(0), rd.GetDecimal(1), rd.GetString(2));
            await LinkOut.SendAsync(sp, status == "fulfilled" ? "order.fulfilled" : "order.failed", new
            {
                orderId, @ref = websiteRef, kind, status, tenantId, address = outcome ?? "", invoice = invoiceNumber ?? "", total, currency,
            }, CancellationToken.None);
        }
    }

    /// <summary>The licence key of a fulfilled on-prem order (shown on the order page), or null.</summary>
    public static string? OrderKey(IDataProtectionProvider dp, string result)
    {
        try { return result == "" ? null : dp.CreateProtector(KeyPurpose).Unprotect(result); } catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    static async Task<Guid> SaveSubscriptionAsync(NpgsqlConnection db, Guid? existing, Guid accountId, string kind, string tenantId, Store.Quote q, DateTime starts, DateTime ends, int grace, CancellationToken ct)
    {
        await using var c = existing is Guid id
            ? new NpgsqlCommand(@"update commerce.subscriptions set account_id = @a, plan = 'store', lines = @l, period = @per, starts_at = @s, ends_at = @e, grace_days = @g, state = 'active',
                notes = 'Bought in the store', updated_at = now() where id = @i returning id", db)
            : new NpgsqlCommand(@"insert into commerce.subscriptions (account_id, kind, tenant_id, plan, lines, period, starts_at, ends_at, grace_days, notes)
                values (@a, @k, @t, 'store', @l, @per, @s, @e, @g, 'Bought in the store') returning id", db);
        c.Parameters.AddWithValue("a", accountId); c.Parameters.AddWithValue("k", kind); c.Parameters.AddWithValue("t", tenantId);
        c.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(q.Licence) });
        c.Parameters.AddWithValue("per", q.Period); c.Parameters.AddWithValue("s", starts); c.Parameters.AddWithValue("e", ends); c.Parameters.AddWithValue("g", grace);
        if (existing is Guid ex) c.Parameters.AddWithValue("i", ex);
        return (Guid)(await c.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Creates the first administrator in the new customer and returns the path of their set-password link.</summary>
    public static async Task<string> FirstAdminLinkAsync(IServiceProvider sp, TenantInfo t, string email, string name, CancellationToken ct)
    {
        await using var scope = sp.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Current = t;
        return await scope.ServiceProvider.GetRequiredService<IFirstAdmin>().CreateAsync(email, name, ct);
    }

    /// <summary>The smallest size of each chosen module, for a trial.</summary>
    public static Dictionary<string, Dictionary<string, int>> TrialLines(List<Store.Plan> plans, IEnumerable<string> modules)
    {
        var want = modules.Where(m => Modules.All.Contains(m)).ToHashSet();
        var lines = new Dictionary<string, Dictionary<string, int>>();
        foreach (var m in want)
        {
            var p = plans.Where(x => x.Module == m && x.Limits.Count > 0).OrderBy(x => x.Limits.Values.First()).FirstOrDefault();
            lines[m] = p == null ? new Dictionary<string, int>() : new Dictionary<string, int>(p.Limits);
        }
        return lines;
    }
}

/// <summary>Every 6 hours on the vendor platform: warns 7 days before an unconverted trial is deleted, then deletes it (Business.TrialKeepDays after it ended).</summary>
public sealed class TrialsJob : IJobHandler
{
    public const string JobType = "commerce.trials";
    public string Type => JobType;

    public async Task RunAsync(JobContext job, CancellationToken ct)
    {
        var o = job.Services.GetRequiredService<CommerceOptions>();
        if (!o.ForId(job.Tenant.Id)) return;
        var biz = await Business.LoadAsync(o, ct);
        var keep = Math.Clamp(biz.TrialKeepDays, 8, 365);
        await using var db = await CommerceDb.OpenAsync(o, ct);
        var mail = job.Services.GetRequiredService<IMailQueue>();

        var warn = new List<(Guid Id, string Tenant, string Name, string Email, DateTime Ends)>();
        await using (var q = new NpgsqlCommand(@"select s.id, s.tenant_id, a.name, a.email, s.ends_at from commerce.subscriptions s join commerce.accounts a on a.id = s.account_id
            where s.state = 'trial' and s.trial_warned_at is null and s.ends_at < now() - make_interval(days => @d)", db))
        {
            q.Parameters.AddWithValue("d", keep - 7);
            await using var rd = await q.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) warn.Add((rd.GetGuid(0), rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetDateTime(4)));
        }
        foreach (var w in warn)
        {
            await mail.EnqueueTemplateAsync("store_trial_ending", w.Email, new Dictionary<string, string>
            { ["name"] = w.Name, ["company"] = w.Name, ["address"] = "https://" + Fulfilment.Host(biz, w.Tenant), ["delete_date"] = w.Ends.AddDays(keep).ToString("dd MMM yyyy") }, "trial-" + w.Id, ct);
            await using var up = new NpgsqlCommand("update commerce.subscriptions set trial_warned_at = now() where id = @i", db);
            up.Parameters.AddWithValue("i", w.Id); await up.ExecuteNonQueryAsync(ct);
        }

        var gone = new List<(Guid Id, string Tenant)>();
        await using (var q = new NpgsqlCommand("select id, tenant_id from commerce.subscriptions where state = 'trial' and trial_warned_at is not null and ends_at < now() - make_interval(days => @d)", db))
        {
            q.Parameters.AddWithValue("d", keep);
            await using var rd = await q.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) gone.Add((rd.GetGuid(0), rd.GetString(1)));
        }
        foreach (var g in gone)
        {
            await job.Services.GetRequiredService<ITenantProvisioner>().DeleteAsync(g.Tenant, ct);
            await using var up = new NpgsqlCommand("update commerce.subscriptions set state = 'ended', notes = notes || ' Trial data deleted ' || to_char(now(), 'YYYY-MM-DD') || '.', updated_at = now() where id = @i", db);
            up.Parameters.AddWithValue("i", g.Id); await up.ExecuteNonQueryAsync(ct);
            await job.Services.GetRequiredService<IAuditLog>().WriteAsync("commerce.trial_deleted", g.Tenant, "unconverted trial, " + keep + " days after it ended", actor: "store", ct: ct);
        }
    }
}
