using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskAsk.Platform.Licensing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace DeskAsk.Platform.Commerce;

/// <summary>
/// The vendor console (docs/store.md): customers, subscriptions, licences, licence keys and the price list. Only administrators of the vendor's own
/// customer on the vendor platform reach it; everywhere else these addresses answer 404. It replaces the licence tool for daily work: the signing
/// key stays on the server and every licence issued is recorded.
/// </summary>
public static class VendorConsole
{
    static IResult Bad(string error, string detail, int status = 400) => Results.Json(new { error, detail }, statusCode: status);
    static string Who(ClaimsPrincipal u) => u.FindFirstValue("email") ?? u.FindFirstValue(ClaimTypes.Email) ?? u.Identity?.Name ?? "vendor";

    static RouteHandlerBuilder Console(RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).AddEndpointFilter(async (ctx, next) =>
    {
        var sp = ctx.HttpContext.RequestServices;
        return sp.GetRequiredService<CommerceOptions>().For(sp.GetRequiredService<ITenantContext>()) ? await next(ctx) : Results.NotFound(new { error = "not_found" });
    });

    static string Str(JsonElement b, string name, string fallback = "") =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : fallback;
    static int? Int(JsonElement b, string name) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    static DateTime? Date(JsonElement b, string name) =>
        Str(b, name) is { Length: > 0 } s && DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    static Dictionary<string, Dictionary<string, int>>? Lines(JsonElement b)
    {
        if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty("lines", out var l) || l.ValueKind != JsonValueKind.Object) return null;
        try { return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(l.GetRawText(), Licences.Json); } catch (JsonException) { return null; }
    }

    static object SubView(Sub s, string account) => new
    {
        id = s.Id, accountId = s.AccountId, account, kind = s.Kind, tenantId = s.TenantId, plan = s.Plan, lines = s.Lines, startsAt = s.StartsAt, endsAt = s.EndsAt,
        graceDays = s.GraceDays, state = s.State, period = s.Period, notes = s.Notes, updatedAt = s.UpdatedAt,
    };

    static readonly Regex TenantId = new("^[a-z][a-z0-9-]{1,30}$");

    public static void Map(IEndpointRouteBuilder r)
    {
        // what the menu needs: is this the vendor console, and can it sign?
        r.MapGet("/api/commerce/status", async (ClaimsPrincipal u, CommerceOptions o, ITenantContext t, Signer signer, VendorKey vendor, CancellationToken ct) =>
        {
            if (!o.For(t) || !u.IsInRole("admin")) return Results.Ok(new { enabled = false });
            var (key, source) = await signer.LoadAsync(ct);
            using (key)
                return Results.Ok(new
                {
                    enabled = true, vendorTenant = o.VendorTenant, catalog = o.Catalog != "", vendorKey = vendor.Public != null, vendorKeySource = vendor.Source, vendorKeyProblem = vendor.Problem,
                    signing = new { ready = key != null && signer.Matches(key), source, kid = key == null ? null : LicenseCodec.KeyId(key), matches = key != null && signer.Matches(key) },
                });
        }).RequireAuthorization();

        Console(r.MapPut("/api/commerce/signing-key", async (JsonElement b, Signer signer, IAuditLog audit, CancellationToken ct) =>
        {
            var pem = Str(b, "pem");
            if (pem.Length is < 50 or > 10000) return Bad("pem_missing", "Paste the whole private key file, from -----BEGIN to -----END.");
            if (await signer.StoreAsync(pem, ct) is { } refused) return Bad("key_refused", refused);
            await audit.WriteAsync("commerce.signing_key", "", "the licence signing key was imported", ct: ct);
            return Results.Ok(new { stored = true });
        }));

        // ------------------------------------------------------------------ price list
        Console(r.MapGet("/api/commerce/plans", async (CommerceOptions o, CancellationToken ct) =>
        {
            await using var db = await CommerceDb.OpenAsync(o, ct);
            await using var q = new NpgsqlCommand("select id, module, size_label, limits::text, prices::text, sort, active from commerce.plans order by sort, id", db);
            await using var rd = await q.ExecuteReaderAsync(ct);
            var list = new List<object>();
            while (await rd.ReadAsync(ct))
                list.Add(new { id = rd.GetInt32(0), module = rd.GetString(1), sizeLabel = rd.GetString(2), limits = JsonDocument.Parse(rd.GetString(3)).RootElement.Clone(),
                    prices = JsonDocument.Parse(rd.GetString(4)).RootElement.Clone(), sort = rd.GetInt32(5), active = rd.GetBoolean(6) });
            return Results.Ok(list);
        }));

        Console(r.MapPut("/api/commerce/plans/{id:int}", async (int id, JsonElement b, CommerceOptions o, IAuditLog audit, CancellationToken ct) =>
        {
            var label = Str(b, "sizeLabel");
            if (label.Length is < 1 or > 60) return Bad("label_missing", "Give the size a name, for example \"10 agents\".");
            if (!b.TryGetProperty("prices", out var prices) || prices.ValueKind != JsonValueKind.Object) return Bad("prices_missing", "Prices are missing.");
            if (!b.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Object) return Bad("limits_missing", "Limits are missing.");
            await using var db = await CommerceDb.OpenAsync(o, ct);
            await using var up = new NpgsqlCommand("update commerce.plans set size_label = @l, limits = @lim, prices = @p, active = @a where id = @i", db);
            up.Parameters.AddWithValue("l", label);
            up.Parameters.Add(new NpgsqlParameter("lim", NpgsqlDbType.Jsonb) { Value = limits.GetRawText() });
            up.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Jsonb) { Value = prices.GetRawText() });
            up.Parameters.AddWithValue("a", !b.TryGetProperty("active", out var act) || act.ValueKind != JsonValueKind.False);
            up.Parameters.AddWithValue("i", id);
            try { if (await up.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound(new { error = "not_found" }); }
            catch (PostgresException e) when (e.SqlState == "23505") { return Bad("label_taken", "This module already has a size with that name."); }
            await audit.WriteAsync("commerce.price_changed", label, prices.GetRawText(), ct: ct);
            return Results.Ok(new { saved = true });
        }));

        // ------------------------------------------------------------------ the look of invoices (and the closing note)
        Console(r.MapGet("/api/commerce/invoice-style", async (CommerceOptions o, CancellationToken ct) => Results.Ok(PdfStyle.Parse(await CommerceDb.GetSettingAsync(o, "pdf_style", ct)))));
        Console(r.MapPut("/api/commerce/invoice-style", async (PdfStyle b, CommerceOptions o, IAuditLog audit, CancellationToken ct) =>
        {
            foreach (var c in new[] { b.HeaderColor, b.HeaderText, b.Border, b.Text, b.Accent })
                if (!PdfStyle.IsHex(c)) return Bad("bad_colour", "Colours look like #207178.");
            var next = b.Clean();
            await CommerceDb.SetSettingAsync(o, "pdf_style", next.ToJson(), ct);
            await audit.WriteAsync("commerce.invoice_style_changed", detail: $"header={next.HeaderColor}; text={next.Text}; border={next.Border}", ct: ct);
            return Results.Ok(next);
        }));
        Console(r.MapGet("/api/commerce/invoice-style/preview.pdf", async (CommerceOptions o, CancellationToken ct) =>
        {
            var biz = await Business.LoadAsync(o, ct);
            var buyer = new Invoices.Buyer("Sample Customer Pvt Ltd", "Priya Sharma", "priya@example.com", "", "1 Sample Street", "Chennai", "33", "Tamil Nadu", "600001", "India", "");
            var lines = new List<Store.QuoteLine> { new("servicedesk", 1, "Service Desk, 10 agents, 1 year", 12000m), new("monitoring", 2, "Monitoring, 100 devices, 1 year", 9000m) };
            var q = new Store.Quote("INR", "saas", "year", lines, 0m, 21000m, new List<Store.TaxLine> { new("CGST", 9m, 1890m), new("SGST", 9m, 1890m) }, 3780m, 24780m, "", new Dictionary<string, Dictionary<string, int>>());
            var style = PdfStyle.Parse(await CommerceDb.GetSettingAsync(o, "pdf_style", ct));
            return Results.File(Invoices.Pdf(biz, "SAMPLE-0001", DateTime.UtcNow, buyer, q, "pay_sample", style), "application/pdf", "sample-invoice.pdf");
        }));

        // ------------------------------------------------------------------ products on sale (the website sees the same list)
        Console(r.MapGet("/api/commerce/products", async (CommerceOptions o, CancellationToken ct) => Results.Ok(await Products.ListViewAsync(o, ct))));
        Console(r.MapPut("/api/commerce/products/{key}", async (string key, JsonElement b, ClaimsPrincipal u, IServiceProvider sp, CancellationToken ct) =>
        {
            bool? sale = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("onSale", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
            return Products.Result(await Products.SetAsync(sp, key, sale, b.ValueKind == JsonValueKind.Object && b.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
                b.ValueKind == JsonValueKind.Object && b.TryGetProperty("tagline", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null, Who(u), ct), new { saved = true });
        }));
        Console(r.MapDelete("/api/commerce/products/{key}", async (string key, ClaimsPrincipal u, IServiceProvider sp, CancellationToken ct) =>
            Products.Result(await Products.DeleteAsync(sp, key, Who(u), ct), new { deleted = true })));
        Console(r.MapPost("/api/commerce/plans", async (JsonElement b, ClaimsPrincipal u, IServiceProvider sp, CancellationToken ct) =>
            Products.Result(await Products.AddSizeAsync(sp, b, Who(u), ct), new { saved = true })));
        Console(r.MapPost("/api/commerce/products", async (JsonElement b, ClaimsPrincipal u, IServiceProvider sp, CancellationToken ct) =>
            Products.Result(await Products.AddAsync(sp, b, Who(u), ct), new { saved = true })));

        // ------------------------------------------------------------------ customers and subscriptions
        Console(r.MapGet("/api/commerce/accounts", async (CommerceOptions o, CancellationToken ct) =>
        {
            await using var db = await CommerceDb.OpenAsync(o, ct);
            await using var q = new NpgsqlCommand("select id, name, email, country, gstin from commerce.accounts order by name", db);
            await using var rd = await q.ExecuteReaderAsync(ct);
            var list = new List<object>();
            while (await rd.ReadAsync(ct)) list.Add(new { id = rd.GetGuid(0), name = rd.GetString(1), email = rd.GetString(2), country = rd.GetString(3), gstin = rd.GetString(4) });
            return Results.Ok(list);
        }));

        Console(r.MapGet("/api/commerce/subscriptions", async (string? q, CommerceOptions o, CancellationToken ct) =>
        {
            await using var db = await CommerceDb.OpenAsync(o, ct);
            await using var cmd = new NpgsqlCommand("select s." + Licences.SubColumns.Replace(", ", ", s.") + @", a.name, a.email
                from commerce.subscriptions s join commerce.accounts a on a.id = s.account_id
                where @q = '' or a.name ilike '%' || @q || '%' or a.email ilike '%' || @q || '%' or s.tenant_id ilike '%' || @q || '%'
                order by s.ends_at", db);
            cmd.Parameters.AddWithValue("q", (q ?? "").Trim());
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            var list = new List<object>();
            while (await rd.ReadAsync(ct)) list.Add(new { sub = SubView(Licences.ReadSub(rd), rd.GetString(13)), email = rd.GetString(14) });
            return Results.Ok(list);
        }));

        Console(r.MapPost("/api/commerce/subscriptions", async (JsonElement b, ClaimsPrincipal u, CommerceOptions o, Signer signer, IAuditLog audit, CancellationToken ct) =>
        {
            var kind = Str(b, "kind");
            if (kind is not ("saas" or "onprem")) return Bad("kind", "Choose SaaS or on-prem.");
            var tenantId = Str(b, "tenantId").ToLowerInvariant();
            if (kind == "saas" && !TenantId.IsMatch(tenantId)) return Bad("tenant", "A SaaS subscription needs the customer id on this platform, for example acme.");
            if (tenantId != "" && !TenantId.IsMatch(tenantId)) return Bad("tenant", "The customer id has 2 to 31 lower case letters, digits and '-', starting with a letter.");
            var lines = Lines(b);
            if (lines == null) return Bad("lines", "Choose at least one module.");
            if (Licences.CheckLines(lines) is { } badLines) return Bad("lines", badLines);
            var starts = Date(b, "startsAt") ?? DateTime.UtcNow.Date;
            var ends = Date(b, "endsAt");
            if (ends == null || ends <= starts) return Bad("ends", "Choose an end date after the start date.");
            var grace = Int(b, "graceDays") ?? (kind == "saas" ? 7 : 14);
            if (grace is < 0 or > 90) return Bad("grace", "Grace days are between 0 and 90.");
            var plan = Str(b, "plan", "custom"); if (plan.Length is < 1 or > 40) return Bad("plan", "The plan name has 1 to 40 characters.");
            var period = Str(b, "period", "year"); if (period is not ("year" or "month" or "manual")) return Bad("period", "The billing period is year, month or manual.");
            var notes = Str(b, "notes"); if (notes.Length > 2000) return Bad("notes", "Notes are at most 2,000 characters.");

            var (key, _) = await signer.LoadAsync(ct);
            using (key)
            {
                if (key == null || !signer.Matches(key)) return Bad("no_signing_key", "Import the licence signing key first (Vendor console › Signing key).", 409);
                await using var db = await CommerceDb.OpenAsync(o, ct);
                await using var tx = await db.BeginTransactionAsync(ct);
                Guid accountId;
                if (Guid.TryParse(Str(b, "accountId"), out var existing)) accountId = existing;
                else
                {
                    var acc = b.TryGetProperty("account", out var a) ? a : default;
                    var name = Str(acc, "name"); var email = Str(acc, "email").ToLowerInvariant();
                    if (name.Length is < 2 or > 200) return Bad("account", "Give the customer's company name.");
                    if (!Regex.IsMatch(email, "^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$")) return Bad("account", "Give the customer's email address.");
                    await using var ia = new NpgsqlCommand("insert into commerce.accounts (name, email, country, gstin, address) values (@n, @e, @c, @g, @a) returning id", db, tx);
                    ia.Parameters.AddWithValue("n", name); ia.Parameters.AddWithValue("e", email); ia.Parameters.AddWithValue("c", Str(acc, "country", "IN").ToUpperInvariant());
                    ia.Parameters.AddWithValue("g", Str(acc, "gstin").ToUpperInvariant()); ia.Parameters.AddWithValue("a", Str(acc, "address"));
                    accountId = (Guid)(await ia.ExecuteScalarAsync(ct))!;
                }
                await using var ins = new NpgsqlCommand(@"insert into commerce.subscriptions (account_id, kind, tenant_id, plan, lines, period, starts_at, ends_at, grace_days, notes)
                    values (@a, @k, @t, @p, @l, @per, @s, @e, @g, @n) returning id", db, tx);
                ins.Parameters.AddWithValue("a", accountId); ins.Parameters.AddWithValue("k", kind); ins.Parameters.AddWithValue("t", tenantId); ins.Parameters.AddWithValue("p", plan);
                ins.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(lines) });
                ins.Parameters.AddWithValue("per", period); ins.Parameters.AddWithValue("s", starts); ins.Parameters.AddWithValue("e", ends.Value);
                ins.Parameters.AddWithValue("g", grace); ins.Parameters.AddWithValue("n", notes);
                Guid id;
                try { id = (Guid)(await ins.ExecuteScalarAsync(ct))!; }
                catch (PostgresException e) when (e.SqlState == "23503") { return Bad("account", "That customer account does not exist."); }
                var sub = (await Licences.GetSubAsync(db, id, ct, tx))!;
                string? installed = null;
                if (kind == "saas")
                {
                    var (_, token) = await Licences.IssueAsync(db, tx, key, sub, tenantId, "", Who(u), "subscription created", ct);
                    if (await Licences.InstallSaasAsync(o, tenantId, token, ct) is { } refused) return Bad("tenant", refused);
                    installed = tenantId;
                }
                await tx.CommitAsync(ct);
                await audit.WriteAsync("commerce.subscription_created", kind + " " + (tenantId == "" ? "(customer id at activation)" : tenantId), plan + "; " + string.Join(",", lines.Keys) + "; ends " + ends.Value.ToString("yyyy-MM-dd"), ct: ct);
                return Results.Ok(new { id, installed });
            }
        }));

        Console(r.MapGet("/api/commerce/subscriptions/{id:guid}", async (Guid id, CommerceOptions o, CancellationToken ct) =>
        {
            await using var db = await CommerceDb.OpenAsync(o, ct);
            var s = await Licences.GetSubAsync(db, id, ct);
            if (s == null) return Results.NotFound(new { error = "not_found" });
            object? account = null;
            await using (var q = new NpgsqlCommand("select name, email, country, gstin, address from commerce.accounts where id = @i", db))
            {
                q.Parameters.AddWithValue("i", s.AccountId);
                await using var rd = await q.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct)) account = new { name = rd.GetString(0), email = rd.GetString(1), country = rd.GetString(2), gstin = rd.GetString(3), address = rd.GetString(4) };
            }
            var licences = new List<object>();
            await using (var q = new NpgsqlCommand("select id, tenant_id, installation, plan, issued_at, expires_at, issued_by, reason from commerce.licences where subscription_id = @i order by issued_at desc limit 50", db))
            {
                q.Parameters.AddWithValue("i", id);
                await using var rd = await q.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) licences.Add(new { id = rd.GetInt64(0), tenantId = rd.GetString(1), installation = rd.GetString(2), plan = rd.GetString(3),
                    issuedAt = rd.GetDateTime(4), expiresAt = rd.GetDateTime(5), issuedBy = rd.GetString(6), reason = rd.GetString(7) });
            }
            var keys = new List<object>();
            await using (var q = new NpgsqlCommand(@"select k.id, k.hint, k.max_activations, k.revoked, k.created_at,
                    coalesce((select json_agg(json_build_object('id', a.id, 'installation', a.installation, 'tenantId', a.tenant_id, 'host', a.host, 'version', a.version,
                        'activatedAt', a.activated_at, 'lastCheck', a.last_check, 'releasedAt', a.released_at) order by a.activated_at)
                      from commerce.activations a where a.key_id = k.id), '[]')::text
                from commerce.licence_keys k where k.subscription_id = @i order by k.created_at", db))
            {
                q.Parameters.AddWithValue("i", id);
                await using var rd = await q.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) keys.Add(new { id = rd.GetGuid(0), hint = rd.GetString(1), maxActivations = rd.GetInt32(2), revoked = rd.GetBoolean(3), createdAt = rd.GetDateTime(4),
                    activations = JsonDocument.Parse(rd.GetString(5)).RootElement.Clone() });
            }
            return Results.Ok(new { sub = SubView(s, ""), account, licences, keys });
        }));

        // change modules, sizes, dates or plan; SaaS customers get the new licence at once, activated installations at their next daily check
        Console(r.MapPut("/api/commerce/subscriptions/{id:guid}", async (Guid id, JsonElement b, ClaimsPrincipal u, CommerceOptions o, Signer signer, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await CommerceDb.OpenAsync(o, ct);
            var s = await Licences.GetSubAsync(db, id, ct);
            if (s == null) return Results.NotFound(new { error = "not_found" });
            var lines = Lines(b) ?? s.Lines;
            if (Licences.CheckLines(lines) is { } badLines) return Bad("lines", badLines);
            var ends = Date(b, "endsAt") ?? s.EndsAt;
            var starts = Date(b, "startsAt") ?? s.StartsAt;
            if (ends <= starts) return Bad("ends", "Choose an end date after the start date.");
            var grace = Int(b, "graceDays") ?? s.GraceDays;
            if (grace is < 0 or > 90) return Bad("grace", "Grace days are between 0 and 90.");
            var plan = Str(b, "plan", s.Plan); if (plan.Length is < 1 or > 40) return Bad("plan", "The plan name has 1 to 40 characters.");
            var state = Str(b, "state", s.State); if (state is not ("trial" or "active" or "cancelled" or "ended")) return Bad("state", "The state is trial, active, cancelled or ended.");
            var notes = Str(b, "notes", s.Notes); if (notes.Length > 2000) return Bad("notes", "Notes are at most 2,000 characters.");
            var (key, _) = await signer.LoadAsync(ct);
            using (key)
            {
                if (key == null || !signer.Matches(key)) return Bad("no_signing_key", "Import the licence signing key first (Vendor console › Signing key).", 409);
                await using var tx = await db.BeginTransactionAsync(ct);
                await using (var up = new NpgsqlCommand(@"update commerce.subscriptions set lines = @l, starts_at = @s, ends_at = @e, grace_days = @g, plan = @p, state = @st, notes = @n, updated_at = now()
                    where id = @i", db, tx))
                {
                    up.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(lines) });
                    up.Parameters.AddWithValue("s", starts); up.Parameters.AddWithValue("e", ends); up.Parameters.AddWithValue("g", grace);
                    up.Parameters.AddWithValue("p", plan); up.Parameters.AddWithValue("st", state); up.Parameters.AddWithValue("n", notes); up.Parameters.AddWithValue("i", id);
                    await up.ExecuteNonQueryAsync(ct);
                }
                var fresh = (await Licences.GetSubAsync(db, id, ct, tx))!;
                if (fresh.Kind == "saas")
                {
                    var (_, token) = await Licences.IssueAsync(db, tx, key, fresh, fresh.TenantId, "", Who(u), "subscription changed", ct);
                    if (await Licences.InstallSaasAsync(o, fresh.TenantId, token, ct) is { } refused) return Bad("tenant", refused);
                }
                await tx.CommitAsync(ct);
                await audit.WriteAsync("commerce.subscription_changed", fresh.Kind + " " + fresh.TenantId, plan + "; " + string.Join(",", lines.Keys) + "; ends " + ends.ToString("yyyy-MM-dd") + "; " + state, ct: ct);
                return Results.Ok(new { sub = SubView(fresh, "") });
            }
        }));

        // end now: the licence ends today (SaaS at once; installations when they next check, or when their current licence runs out)
        Console(r.MapPost("/api/commerce/subscriptions/{id:guid}/end", async (Guid id, ClaimsPrincipal u, CommerceOptions o, Signer signer, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await CommerceDb.OpenAsync(o, ct);
            var s = await Licences.GetSubAsync(db, id, ct);
            if (s == null) return Results.NotFound(new { error = "not_found" });
            var (key, _) = await signer.LoadAsync(ct);
            using (key)
            {
                if (key == null || !signer.Matches(key)) return Bad("no_signing_key", "Import the licence signing key first (Vendor console › Signing key).", 409);
                await using var tx = await db.BeginTransactionAsync(ct);
                await using (var up = new NpgsqlCommand("update commerce.subscriptions set state = 'ended', ends_at = least(ends_at, now() - interval '1 minute'), grace_days = 0, updated_at = now() where id = @i", db, tx))
                { up.Parameters.AddWithValue("i", id); await up.ExecuteNonQueryAsync(ct); }
                var fresh = (await Licences.GetSubAsync(db, id, ct, tx))!;
                if (fresh.Kind == "saas")
                {
                    var (_, token) = await Licences.IssueAsync(db, tx, key, fresh, fresh.TenantId, "", Who(u), "subscription ended", ct);
                    if (await Licences.InstallSaasAsync(o, fresh.TenantId, token, ct) is { } refused) return Bad("tenant", refused);
                }
                await tx.CommitAsync(ct);
                await audit.WriteAsync("commerce.subscription_ended", fresh.Kind + " " + fresh.TenantId, "", ct: ct);
                return Results.Ok(new { ended = true });
            }
        }));

        // a licence for a given customer id (and installation): for an offline site, or to issue a SaaS licence again
        Console(r.MapPost("/api/commerce/subscriptions/{id:guid}/licence", async (Guid id, JsonElement b, ClaimsPrincipal u, CommerceOptions o, Signer signer, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await CommerceDb.OpenAsync(o, ct);
            var s = await Licences.GetSubAsync(db, id, ct);
            if (s == null) return Results.NotFound(new { error = "not_found" });
            var tenantId = Str(b, "tenantId", s.TenantId).ToLowerInvariant();
            if (!TenantId.IsMatch(tenantId)) return Bad("tenant", "Give the customer id shown on the customer's Licence page.");
            var installation = Str(b, "installation").ToLowerInvariant();
            if (installation != "" && !Regex.IsMatch(installation, "^[a-f0-9]{16,64}$")) return Bad("installation", "The installation id is shown on the customer's Licence page (32 letters and digits).");
            if (s.Kind == "saas" && tenantId != s.TenantId) return Bad("tenant", "A SaaS subscription is for customer '" + s.TenantId + "' only.");
            var (key, _) = await signer.LoadAsync(ct);
            using (key)
            {
                if (key == null || !signer.Matches(key)) return Bad("no_signing_key", "Import the licence signing key first (Vendor console › Signing key).", 409);
                var (licId, token) = await Licences.IssueAsync(db, null, key, s, tenantId, installation, Who(u), s.Kind == "saas" ? "issued again" : "licence file", ct);
                if (s.Kind == "saas" && await Licences.InstallSaasAsync(o, tenantId, token, ct) is { } refused) return Bad("tenant", refused);
                await audit.WriteAsync("commerce.licence_issued", tenantId, (installation == "" ? "" : "installation " + installation + "; ") + "subscription " + id, ct: ct);
                return Results.Ok(new { id = licId, token });
            }
        }));

        Console(r.MapGet("/api/commerce/licences/{id:long}", async (long id, CommerceOptions o, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await CommerceDb.OpenAsync(o, ct);
            await using var q = new NpgsqlCommand("select token, tenant_id from commerce.licences where id = @i", db);
            q.Parameters.AddWithValue("i", id);
            await using var rd = await q.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return Results.NotFound(new { error = "not_found" });
            var (token, tenant) = (rd.GetString(0), rd.GetString(1));
            await rd.CloseAsync();
            await audit.WriteAsync("commerce.licence_downloaded", tenant, "licence " + id, ct: ct);
            return Results.Ok(new { token });
        }));

        // ------------------------------------------------------------------ licence keys (on-prem online activation)
        Console(r.MapPost("/api/commerce/subscriptions/{id:guid}/keys", async (Guid id, JsonElement b, CommerceOptions o, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await CommerceDb.OpenAsync(o, ct);
            var s = await Licences.GetSubAsync(db, id, ct);
            if (s == null) return Results.NotFound(new { error = "not_found" });
            if (s.Kind != "onprem") return Bad("kind", "Licence keys are for on-prem subscriptions. SaaS customers are licensed directly.");
            var max = Int(b, "maxActivations") ?? 1;
            if (max is < 1 or > 50) return Bad("max", "A key can be active on 1 to 50 installations.");
            var key = Licences.NewKey(); var norm = Licences.NormalizeKey(key);
            await using var ins = new NpgsqlCommand("insert into commerce.licence_keys (subscription_id, key_hash, hint, max_activations) values (@s, @h, @hint, @m) returning id", db);
            ins.Parameters.AddWithValue("s", id); ins.Parameters.AddWithValue("h", Licences.Hash(norm)); ins.Parameters.AddWithValue("hint", norm[^4..]); ins.Parameters.AddWithValue("m", max);
            var keyId = (Guid)(await ins.ExecuteScalarAsync(ct))!;
            await audit.WriteAsync("commerce.key_created", "key ending " + norm[^4..], "subscription " + id + "; " + max + " installation(s)", ct: ct);
            return Results.Ok(new { id = keyId, key, hint = norm[^4..] });   // the only time the whole key is shown
        }));

        Console(r.MapPost("/api/commerce/keys/{id:guid}/revoke", async (Guid id, CommerceOptions o, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await CommerceDb.OpenAsync(o, ct);
            await using var up = new NpgsqlCommand("update commerce.licence_keys set revoked = true where id = @i returning hint", db);
            up.Parameters.AddWithValue("i", id);
            if (await up.ExecuteScalarAsync(ct) is not string hint) return Results.NotFound(new { error = "not_found" });
            await audit.WriteAsync("commerce.key_revoked", "key ending " + hint, "", ct: ct);
            return Results.Ok(new { revoked = true });
        }));

        Console(r.MapPost("/api/commerce/activations/{id:guid}/release", async (Guid id, CommerceOptions o, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await CommerceDb.OpenAsync(o, ct);
            await using var up = new NpgsqlCommand("update commerce.activations set released_at = now() where id = @i and released_at is null returning installation", db);
            up.Parameters.AddWithValue("i", id);
            if (await up.ExecuteScalarAsync(ct) is not string inst) return Results.NotFound(new { error = "not_found" });
            await audit.WriteAsync("commerce.activation_released", "installation " + inst, "by the vendor", ct: ct);
            return Results.Ok(new { released = true });
        }));

        // ------------------------------------------------------------------ customers licensed before the store existed
        Console(r.MapGet("/api/commerce/unlinked", async (CommerceOptions o, VendorKey vendor, CancellationToken ct) =>
        {
            if (o.Catalog == "") return Results.Ok(Array.Empty<object>());
            var linked = new HashSet<string>();
            await using (var db = await CommerceDb.OpenAsync(o, ct))
            await using (var q = new NpgsqlCommand("select distinct tenant_id from commerce.subscriptions where kind = 'saas' and state <> 'ended'", db))
            await using (var rd = await q.ExecuteReaderAsync(ct))
                while (await rd.ReadAsync(ct)) linked.Add(rd.GetString(0));
            var list = new List<object>();
            await using (var cat = new NpgsqlConnection(o.Catalog))
            {
                await cat.OpenAsync(ct);
                await using var q = new NpgsqlCommand("select t.id, t.name, t.host, t.active, l.token from platform.tenants t left join platform.licenses l on l.tenant_id = t.id order by t.id", cat);
                await using var rd = await q.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                {
                    var id = rd.GetString(0);
                    if (linked.Contains(id) || id == o.VendorTenant) continue;
                    var lic = rd.IsDBNull(4) || vendor.Public == null ? null : LicenseCodec.Verify(rd.GetString(4), vendor.Public, id, DateTimeOffset.UtcNow);
                    list.Add(new
                    {
                        tenantId = id, name = rd.GetString(1), host = rd.GetString(2), active = rd.GetBoolean(3),
                        licence = lic?.Payload == null ? null : new { state = lic.State.ToString().ToLowerInvariant(), plan = lic.Payload.Plan, modules = lic.Payload.Modules.Keys,
                            expires = DateTimeOffset.FromUnixTimeSeconds(Math.Min(lic.Payload.Expires, 253370764800L)).UtcDateTime },
                    });
                }
            }
            return Results.Ok(list);
        }));

        // link a customer to a subscription made from the licence they already have: nothing changes for them
        Console(r.MapPost("/api/commerce/link", async (JsonElement b, ClaimsPrincipal u, CommerceOptions o, VendorKey vendor, IAuditLog audit, CancellationToken ct) =>
        {
            var token = Str(b, "token");
            var kind = token == "" ? "saas" : "onprem";
            string tenantId = Str(b, "tenantId").ToLowerInvariant();
            if (kind == "saas")
            {
                if (!TenantId.IsMatch(tenantId)) return Bad("tenant", "Choose the customer to link.");
                if (o.Catalog == "") return Bad("no_catalog", "This installation has no platform catalog.");
                await using var cat = new NpgsqlConnection(o.Catalog); await cat.OpenAsync(ct);
                await using var q = new NpgsqlCommand("select token from platform.licenses where tenant_id = @t", cat);
                q.Parameters.AddWithValue("t", tenantId);
                token = await q.ExecuteScalarAsync(ct) as string ?? "";
                if (token == "") return Bad("no_licence", "This customer has no licence yet. Create a new SaaS subscription for them instead.");
            }
            if (vendor.Public == null) return Bad("no_vendor_key", "This installation has no vendor public key.");
            var parts = token.Split('.');
            var claimed = "";
            try { claimed = parts.Length == 3 ? JsonDocument.Parse(Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/').PadRight((parts[1].Length + 3) / 4 * 4, '='))).RootElement.GetProperty("tenant").GetString() ?? "" : ""; }
            catch (Exception) { }
            var lic = LicenseCodec.Verify(token, vendor.Public, kind == "saas" ? tenantId : claimed == "*" ? "*" : claimed, DateTimeOffset.UtcNow);
            if (lic.Payload == null || lic.State == LicenseState.Invalid) return Bad("licence", "This is not a licence signed with the vendor key. " + lic.Reason);
            if (kind == "onprem") tenantId = lic.Payload.Tenant == "*" ? "" : lic.Payload.Tenant;
            var name = Str(b, "accountName"); var email = Str(b, "email").ToLowerInvariant();
            if (name.Length is < 2 or > 200) return Bad("account", "Give the customer's company name.");
            if (!Regex.IsMatch(email, "^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$")) return Bad("account", "Give the customer's email address.");
            var p = lic.Payload;
            var lines = p.Modules.ToDictionary(m => m.Key, m => m.Value.Limits ?? new Dictionary<string, int>());
            await using var db = await CommerceDb.OpenAsync(o, ct);
            await using var tx = await db.BeginTransactionAsync(ct);
            await using var ia = new NpgsqlCommand("insert into commerce.accounts (name, email) values (@n, @e) returning id", db, tx);
            ia.Parameters.AddWithValue("n", name); ia.Parameters.AddWithValue("e", email);
            var accountId = (Guid)(await ia.ExecuteScalarAsync(ct))!;
            await using var ins = new NpgsqlCommand(@"insert into commerce.subscriptions (account_id, kind, tenant_id, plan, lines, period, starts_at, ends_at, grace_days, notes)
                values (@a, @k, @t, @p, @l, 'manual', @s, @e, @g, 'Linked from an existing licence') returning id", db, tx);
            ins.Parameters.AddWithValue("a", accountId); ins.Parameters.AddWithValue("k", kind); ins.Parameters.AddWithValue("t", tenantId);
            ins.Parameters.AddWithValue("p", p.Plan.Length is > 0 and <= 40 ? p.Plan : "custom");
            ins.Parameters.Add(new NpgsqlParameter("l", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(lines) });
            ins.Parameters.AddWithValue("s", DateTimeOffset.FromUnixTimeSeconds(p.NotBefore).UtcDateTime);
            ins.Parameters.AddWithValue("e", DateTimeOffset.FromUnixTimeSeconds(Math.Min(p.Expires, 253370764800L)).UtcDateTime);
            ins.Parameters.AddWithValue("g", Math.Clamp(p.GraceDays, 0, 90));
            var id = (Guid)(await ins.ExecuteScalarAsync(ct))!;
            await using var rec = new NpgsqlCommand(@"insert into commerce.licences (subscription_id, tenant_id, installation, token, plan, issued_at, expires_at, issued_by, reason)
                values (@s, @t, @i, @k, @p, @at, @e, @b, 'linked existing licence')", db, tx);
            rec.Parameters.AddWithValue("s", id); rec.Parameters.AddWithValue("t", p.Tenant); rec.Parameters.AddWithValue("i", p.Installation ?? ""); rec.Parameters.AddWithValue("k", token);
            rec.Parameters.AddWithValue("p", p.Plan); rec.Parameters.AddWithValue("at", DateTimeOffset.FromUnixTimeSeconds(p.Issued).UtcDateTime);
            rec.Parameters.AddWithValue("e", DateTimeOffset.FromUnixTimeSeconds(Math.Min(p.Expires, 253370764800L)).UtcDateTime); rec.Parameters.AddWithValue("b", Who(u));
            await rec.ExecuteNonQueryAsync(ct);
            await tx.CommitAsync(ct);
            await audit.WriteAsync("commerce.subscription_linked", kind + " " + tenantId, name, ct: ct);
            return Results.Ok(new { id });
        }));
    }
}
