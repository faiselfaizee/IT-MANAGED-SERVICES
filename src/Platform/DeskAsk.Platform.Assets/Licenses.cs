using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using NpgsqlTypes;

namespace DeskAsk.Platform.Assets;

/// <summary>
/// Software a customer bought: subscriptions (Microsoft 365, Adobe, AutoCAD), perpetual and volume licences. Each has a vendor, dates,
/// seats, cost and renewal; seats are given to people or computers; a name pattern counts the computers that have it installed, so
/// over-use shows. Ending licences join the warranty and AMC renewal reminders. Licence keys are stored sealed and every reveal is audited.
/// </summary>
internal static class LicensesApi
{
    static readonly string[] Kinds = { "subscription", "perpetual", "volume", "oem", "trial", "open-source" };
    static readonly string[] ModelsList = { "user", "device", "core", "site", "concurrent" };
    static readonly string[] Billings = { "monthly", "yearly", "multi-year", "one-time" };

    static async Task<NpgsqlConnection> Open(ITenantContext t, CancellationToken ct) { var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct); return db; }
    static void P(NpgsqlCommand c, string n, object? v) => c.Parameters.AddWithValue(n, v ?? DBNull.Value);
    static void PD(NpgsqlCommand c, string n, DateOnly? d) => c.Parameters.Add(new NpgsqlParameter(n, NpgsqlDbType.Date) { Value = d.HasValue ? d.Value : DBNull.Value });
    static IDataProtector Keys(IDataProtectionProvider dp, ITenantContext t) => dp.CreateProtector("assets.license-key:" + t.Require().Id);
    static string? S(JsonElement b, string n) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : null;
    static bool Has(JsonElement b, string n) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(n, out _);
    static IResult Bad(string d) => Results.BadRequest(new { error = "bad_input", detail = d });
    static IResult Forbid() => Results.Json(new { error = "forbidden", detail = "You may not change software licences of this customer." }, statusCode: 403);
    static bool May(IReadOnlyList<int>? scope, int company) => scope == null || scope.Contains(company);
    static string Like(string s) => "%" + s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    const string Installed = @"(select count(distinct a.id) from assets.assets a
        cross join lateral jsonb_array_elements(case when jsonb_typeof(a.installed_apps) = 'array' then a.installed_apps else '[]'::jsonb end) e
        where l.match <> '' and a.company_id = l.company_id and a.status not in ('retired','lost') and lower(e->>'name') like '%' || replace(replace(replace(lower(l.match), '\', '\\'), '%', '\%'), '_', '\_') || '%' escape '\')";
    const string Status = @"case when l.end_date is null then 'perpetual' when l.end_date < current_date then 'expired'
        when exists (select 1 from assets.licenses n where n.previous_id = l.id) then 'renewed' when l.end_date <= current_date + 30 then 'expiring' else 'active' end";
    const string Cols = $@"l.id, l.company_id, co.name, l.product, l.publisher, l.edition, l.kind, l.model, l.seats, l.cost, l.currency, l.billing, l.vendor_id, coalesce(v.name, ''),
        l.purchase_date, l.start_date, l.end_date, l.auto_renew, l.po_number, l.invoice_number, l.contract_no, l.portal_url, l.key_sealed <> '', l.match, l.notes, l.previous_id,
        (select count(*) from assets.license_seats s where s.license_id = l.id), {Installed}, {Status}";
    const string From = " from assets.licenses l join assets.companies co on co.id = l.company_id left join assets.vendors v on v.id = l.vendor_id ";

    static object Row(NpgsqlDataReader r)
    {
        DateOnly? D(int i) => r.IsDBNull(i) ? null : DateOnly.FromDateTime(r.GetDateTime(i));
        return new
        {
            id = r.GetInt32(0), companyId = r.GetInt32(1), company = r.GetString(2), product = r.GetString(3), publisher = r.GetString(4), edition = r.GetString(5), kind = r.GetString(6),
            model = r.GetString(7), seats = r.GetInt32(8), cost = r.IsDBNull(9) ? (decimal?)null : r.GetDecimal(9), currency = r.GetString(10), billing = r.GetString(11),
            vendorId = r.IsDBNull(12) ? (int?)null : r.GetInt32(12), vendor = r.GetString(13), purchaseDate = D(14), startDate = D(15), endDate = D(16), autoRenew = r.GetBoolean(17),
            poNumber = r.GetString(18), invoiceNumber = r.GetString(19), contractNo = r.GetString(20), portalUrl = r.GetString(21), hasKey = r.GetBoolean(22), match = r.GetString(23),
            notes = r.GetString(24), previousId = r.IsDBNull(25) ? (int?)null : r.GetInt32(25), assigned = r.GetInt64(26), installed = r.GetInt64(27), status = r.GetString(28)
        };
    }

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        RouteHandlerBuilder Read(RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole(AssetsAccess.Readers)).RequireModule(key).RequireAccess(key, Acts.Read);
        RouteHandlerBuilder Write(RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole(AssetsAccess.Readers)).RequireModule(key);

        Read(r.MapGet("/api/assets/licenses", async (int? company, string? status, string? q, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read); var manage = await LocationsApi.ManageScope(http);
            await using var db = await Open(t, ct);
            var w = "where (not @sc or l.company_id = any(@scope)) and (@co::int is null or l.company_id = @co) and (@q = '' or lower(concat_ws(' ', l.product, l.publisher, l.edition, v.name, l.contract_no)) like @q escape '\\')";
            await using var c = new NpgsqlCommand($"select {Cols} {From} {w} order by l.end_date nulls last, lower(l.product) limit 2000", db);
            AssetsAccess.Bind(c, scope); P(c, "co", company); P(c, "q", string.IsNullOrWhiteSpace(q) ? "" : Like(q.Trim().ToLowerInvariant()));
            var list = new List<object>();
            await using (var rd = await c.ExecuteReaderAsync(ct)) while (await rd.ReadAsync(ct)) list.Add(Row(rd));
            if (!string.IsNullOrEmpty(status)) list = list.Where(x => (string)x.GetType().GetProperty("status")!.GetValue(x)! == status).ToList();
            return Results.Ok(new { items = list, canEdit = manage == null || manage.Count > 0 });
        }));

        Read(r.MapGet("/api/assets/licenses/{id:int}", async (int id, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read); var manage = await LocationsApi.ManageScope(http);
            await using var db = await Open(t, ct);
            object? lic = null; int company = 0;
            await using (var c = new NpgsqlCommand($"select {Cols} {From} where l.id = @i and (not @sc or l.company_id = any(@scope))", db))
            {
                P(c, "i", id); AssetsAccess.Bind(c, scope);
                await using var rd = await c.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct)) { lic = Row(rd); company = rd.GetInt32(1); }
            }
            if (lic == null) return Results.NotFound();
            var seats = new List<object>();
            await using (var c = new NpgsqlCommand("select s.id, s.asset_id, coalesce(a.tag, ''), coalesce(a.name, ''), s.user_name, s.note, s.assigned_at from assets.license_seats s left join assets.assets a on a.id = s.asset_id where s.license_id = @i order by s.assigned_at", db))
            {
                P(c, "i", id);
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) seats.Add(new { id = rd.GetInt32(0), assetId = rd.IsDBNull(1) ? (long?)null : rd.GetInt64(1), tag = rd.GetString(2), asset = rd.GetString(3), user = rd.GetString(4), note = rd.GetString(5), assignedAt = rd.GetDateTime(6) });
            }
            var history = new List<object>();
            await using (var c = new NpgsqlCommand(@"with recursive h(id, prev, s, e, depth) as (select id, previous_id, start_date, end_date, 0 from assets.licenses where id = @i
                union all select l.id, l.previous_id, l.start_date, l.end_date, h.depth + 1 from assets.licenses l join h on l.id = h.prev where h.depth < 20)
                select id, s, e from h where id <> @i order by depth", db))
            {
                P(c, "i", id);
                await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) history.Add(new { id = rd.GetInt32(0), startDate = rd.IsDBNull(1) ? (DateOnly?)null : DateOnly.FromDateTime(rd.GetDateTime(1)), endDate = rd.IsDBNull(2) ? (DateOnly?)null : DateOnly.FromDateTime(rd.GetDateTime(2)) });
            }
            int? next = null;
            await using (var c = new NpgsqlCommand("select id from assets.licenses where previous_id = @i limit 1", db)) { P(c, "i", id); if (await c.ExecuteScalarAsync(ct) is int n) next = n; }
            return Results.Ok(new { license = lic, seats, history, renewedBy = next, canEdit = May(manage, company) });
        }));

        // the computers that have it installed (by the name pattern)
        Read(r.MapGet("/api/assets/licenses/{id:int}/installed", async (int id, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read);
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand(@"select distinct on (a.id) a.id, a.tag, a.name, a.hostname, e->>'name', coalesce(e->>'version', ''), a.assigned_to from assets.licenses l
                join assets.assets a on a.company_id = l.company_id and a.status not in ('retired','lost')
                cross join lateral jsonb_array_elements(case when jsonb_typeof(a.installed_apps) = 'array' then a.installed_apps else '[]'::jsonb end) e
                where l.id = @i and l.match <> '' and (not @sc or l.company_id = any(@scope))
                  and lower(e->>'name') like '%' || replace(replace(replace(lower(l.match), '\', '\\'), '%', '\%'), '_', '\_') || '%' escape '\'
                order by a.id limit 2000", db);
            P(c, "i", id); AssetsAccess.Bind(c, scope);
            var list = new List<object>();
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) list.Add(new { id = rd.GetInt64(0), tag = rd.GetString(1), name = rd.GetString(2), hostname = rd.GetString(3), program = rd.GetString(4), version = rd.GetString(5), user = rd.GetString(6) });
            return Results.Ok(list);
        }));

        Write(r.MapPost("/api/assets/licenses", (JsonElement b, HttpContext http, ITenantContext t, IDataProtectionProvider dp, IAuditLog audit, CancellationToken ct) => Save(null, b, http, t, dp, audit, ct)));
        Write(r.MapPut("/api/assets/licenses/{id:int}", (int id, JsonElement b, HttpContext http, ITenantContext t, IDataProtectionProvider dp, IAuditLog audit, CancellationToken ct) => Save(id, b, http, t, dp, audit, ct)));

        Write(r.MapDelete("/api/assets/licenses/{id:int}", async (int id, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var manage = await LocationsApi.ManageScope(http);
            await using var db = await Open(t, ct);
            var (co, name) = await Owner(db, id, ct); if (co == null) return Results.NotFound();
            if (!May(manage, co.Value)) return Forbid();
            await using var c = new NpgsqlCommand("delete from assets.licenses where id = @i", db); P(c, "i", id); await c.ExecuteNonQueryAsync(ct);
            await audit.WriteAsync("assets.license_deleted", id.ToString(CultureInfo.InvariantCulture), name, ct: ct);
            return Results.Ok(new { ok = true });
        }));

        // renew: the next period becomes a new licence linked to this one (seats carried over); this one stops reminding
        Write(r.MapPost("/api/assets/licenses/{id:int}/renew", async (int id, JsonElement b, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var manage = await LocationsApi.ManageScope(http);
            await using var db = await Open(t, ct);
            var (co, name) = await Owner(db, id, ct); if (co == null) return Results.NotFound();
            if (!May(manage, co.Value)) return Forbid();
            await using (var x = new NpgsqlCommand("select 1 from assets.licenses where previous_id = @i", db)) { P(x, "i", id); if (await x.ExecuteScalarAsync(ct) != null) return Results.Conflict(new { error = "renewed", detail = "This licence was already renewed." }); }
            DateOnly? end0 = null; string billing = "yearly";
            await using (var x = new NpgsqlCommand("select end_date, billing from assets.licenses where id = @i", db))
            { P(x, "i", id); await using var rd = await x.ExecuteReaderAsync(ct); if (await rd.ReadAsync(ct)) { end0 = rd.IsDBNull(0) ? null : DateOnly.FromDateTime(rd.GetDateTime(0)); billing = rd.GetString(1); } }
            if (end0 == null) return Bad("A perpetual licence does not renew.");
            var start = DateOnly.TryParse(S(b, "startDate"), CultureInfo.InvariantCulture, out var s1) ? s1 : end0.Value.AddDays(1);
            var end = DateOnly.TryParse(S(b, "endDate"), CultureInfo.InvariantCulture, out var e1) ? e1 : (billing == "monthly" ? start.AddMonths(1) : start.AddYears(1)).AddDays(-1);
            if (end < start) return Bad("The end is before the start.");
            int? seats = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("seats", out var sv) && sv.TryGetInt32(out var si) && si is >= 0 and <= 1_000_000 ? si : null;
            decimal? cost = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("cost", out var cv) && cv.TryGetDecimal(out var cd) ? cd : null;
            await using var tx = await db.BeginTransactionAsync(ct);
            int nid;
            await using (var c = new NpgsqlCommand(@"insert into assets.licenses (company_id, product, publisher, edition, kind, model, seats, cost, currency, billing, vendor_id, purchase_date, start_date, end_date,
                    auto_renew, po_number, invoice_number, contract_no, portal_url, key_sealed, match, notes, previous_id)
                select company_id, product, publisher, edition, kind, model, coalesce(@seats, seats), coalesce(@cost, cost), currency, billing, vendor_id, @pd, @s, @e,
                    auto_renew, coalesce(@po, ''), coalesce(@inv, ''), contract_no, portal_url, key_sealed, match, notes, id from assets.licenses where id = @i returning id", db, tx))
            {
                P(c, "seats", seats); c.Parameters.Add(new NpgsqlParameter("cost", NpgsqlDbType.Numeric) { Value = cost.HasValue ? cost.Value : DBNull.Value });
                PD(c, "pd", DateOnly.TryParse(S(b, "purchaseDate"), CultureInfo.InvariantCulture, out var pd) ? pd : null); PD(c, "s", start); PD(c, "e", end);
                P(c, "po", S(b, "poNumber")); P(c, "inv", S(b, "invoiceNumber")); P(c, "i", id);
                nid = (int)(await c.ExecuteScalarAsync(ct))!;
            }
            await using (var c = new NpgsqlCommand("insert into assets.license_seats (license_id, asset_id, user_name, note) select @n, asset_id, user_name, note from assets.license_seats where license_id = @i", db, tx))
            { P(c, "n", nid); P(c, "i", id); await c.ExecuteNonQueryAsync(ct); }
            await tx.CommitAsync(ct);
            await audit.WriteAsync("assets.license_renewed", id.ToString(CultureInfo.InvariantCulture), $"{name}: {start:yyyy-MM-dd} to {end:yyyy-MM-dd}", ct: ct);
            return Results.Ok(new { id = nid });
        }));

        // seats: a person (by name or email) or a computer
        Write(r.MapPost("/api/assets/licenses/{id:int}/seats", async (int id, JsonElement b, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var manage = await LocationsApi.ManageScope(http);
            await using var db = await Open(t, ct);
            var (co, name) = await Owner(db, id, ct); if (co == null) return Results.NotFound();
            if (!May(manage, co.Value)) return Forbid();
            long? asset = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("assetId", out var av) && av.TryGetInt64(out var al) ? al : null;
            var user = S(b, "user") ?? ""; var note = S(b, "note") ?? "";
            if (asset == null && user == "") return Bad("Give the seat to a person or a computer.");
            if (user.Length > 200 || note.Length > 300) return Bad("Too long.");
            if (asset != null)
            {
                await using var x = new NpgsqlCommand("select company_id from assets.assets where id = @a", db); P(x, "a", asset);
                if (await x.ExecuteScalarAsync(ct) is not int ac || ac != co) return Bad("That computer belongs to another customer.");
                await using var y = new NpgsqlCommand("select 1 from assets.license_seats where license_id = @i and asset_id = @a", db); P(y, "i", id); P(y, "a", asset);
                if (await y.ExecuteScalarAsync(ct) != null) return Results.Conflict(new { error = "exists", detail = "That computer already has a seat." });
            }
            await using var c = new NpgsqlCommand("insert into assets.license_seats (license_id, asset_id, user_name, note) values (@i, @a, @u, @n) returning id", db);
            P(c, "i", id); P(c, "a", asset); P(c, "u", user); P(c, "n", note);
            var sid = (int)(await c.ExecuteScalarAsync(ct))!;
            await audit.WriteAsync("assets.license_seat_given", id.ToString(CultureInfo.InvariantCulture), $"{name}: {(user != "" ? user : "asset " + asset)}", ct: ct);
            return Results.Ok(new { id = sid });
        }));
        Write(r.MapDelete("/api/assets/licenses/seats/{sid:int}", async (int sid, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var manage = await LocationsApi.ManageScope(http);
            await using var db = await Open(t, ct);
            int? lid = null;
            await using (var x = new NpgsqlCommand("select license_id from assets.license_seats where id = @s", db)) { P(x, "s", sid); if (await x.ExecuteScalarAsync(ct) is int l) lid = l; }
            if (lid == null) return Results.NotFound();
            var (co, name) = await Owner(db, lid.Value, ct);
            if (co == null || !May(manage, co.Value)) return Forbid();
            await using var c = new NpgsqlCommand("delete from assets.license_seats where id = @s", db); P(c, "s", sid); await c.ExecuteNonQueryAsync(ct);
            await audit.WriteAsync("assets.license_seat_taken", lid.Value.ToString(CultureInfo.InvariantCulture), name, ct: ct);
            return Results.Ok(new { ok = true });
        }));

        // the licence key: shown only to people who may change the licence, and every look is audited
        Write(r.MapGet("/api/assets/licenses/{id:int}/key", async (int id, HttpContext http, ITenantContext t, IDataProtectionProvider dp, IAuditLog audit, CancellationToken ct) =>
        {
            var manage = await LocationsApi.ManageScope(http);
            await using var db = await Open(t, ct);
            var (co, name) = await Owner(db, id, ct); if (co == null) return Results.NotFound();
            if (!May(manage, co.Value)) return Forbid();
            await using var c = new NpgsqlCommand("select key_sealed from assets.licenses where id = @i", db); P(c, "i", id);
            var sealedKey = await c.ExecuteScalarAsync(ct) as string ?? "";
            if (sealedKey == "") return Results.Ok(new { key = "" });
            string key; try { key = Keys(dp, t).Unprotect(sealedKey); } catch (Exception) { return Results.Problem("The key could not be read (the server's keys changed)."); }
            await audit.WriteAsync("assets.license_key_viewed", id.ToString(CultureInfo.InvariantCulture), name, ct: ct);
            return Results.Ok(new { key });
        }));

        Read(r.MapGet("/api/assets/licenses/export.csv", async (int? company, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read);
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand($"select {Cols} {From} where (not @sc or l.company_id = any(@scope)) and (@co::int is null or l.company_id = @co) order by co.name, lower(l.product)", db);
            AssetsAccess.Bind(c, scope); P(c, "co", company);
            var sb = new StringBuilder("customer,product,publisher,edition,kind,licensed per,seats,assigned,installed,vendor,start,end,status,billing,cost,currency,auto renew,PO,invoice,contract\r\n");
            static string Cell(object? v) { var s = Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""; if (s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s; return s.Contains('"') || s.Contains(',') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s; }
            await using (var rd = await c.ExecuteReaderAsync(ct))
                while (await rd.ReadAsync(ct))
                {
                    string D(int i) => rd.IsDBNull(i) ? "" : rd.GetDateTime(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    sb.Append(string.Join(",", new object?[] { rd.GetString(2), rd.GetString(3), rd.GetString(4), rd.GetString(5), rd.GetString(6), rd.GetString(7), rd.GetInt32(8), rd.GetInt64(26), rd.GetInt64(27),
                        rd.GetString(13), D(15), D(16), rd.GetString(28), rd.GetString(11), rd.IsDBNull(9) ? "" : rd.GetDecimal(9), rd.GetString(10), rd.GetBoolean(17) ? "yes" : "no", rd.GetString(18), rd.GetString(19), rd.GetString(20) }.Select(Cell))).Append("\r\n");
                }
            await audit.WriteAsync("assets.licenses_exported", "licenses", "", ct: ct);
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv; charset=utf-8", $"software-licences-{DateTime.UtcNow:yyyyMMdd}.csv");
        }));
    }

    static async Task<(int? Company, string Name)> Owner(NpgsqlConnection db, int id, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("select company_id, product || case when edition <> '' then ' ' || edition else '' end from assets.licenses where id = @i", db); P(c, "i", id);
        await using var rd = await c.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? (rd.GetInt32(0), rd.GetString(1)) : (null, "");
    }

    static async Task<IResult> Save(int? id, JsonElement b, HttpContext http, ITenantContext t, IDataProtectionProvider dp, IAuditLog audit, CancellationToken ct)
    {
        var manage = await LocationsApi.ManageScope(http);
        await using var db = await Open(t, ct);
        int company;
        if (id != null)
        {
            var (co, _) = await Owner(db, id.Value, ct); if (co == null) return Results.NotFound();
            if (!May(manage, co.Value)) return Forbid();
            company = co.Value;
        }
        else
        {
            if (!(b.ValueKind == JsonValueKind.Object && b.TryGetProperty("companyId", out var cv) && cv.TryGetInt32(out company))) return Bad("Choose the customer.");
            if (!May(manage, company)) return Forbid();
        }
        var product = S(b, "product") ?? "";
        if (product == "" || product.Length > 200) return Bad("Give the product name, for example Microsoft 365 Business Standard.");
        var kind = S(b, "kind") ?? "subscription"; var model = S(b, "model") ?? "user"; var billing = S(b, "billing") ?? (kind == "subscription" ? "yearly" : "one-time");
        if (!Kinds.Contains(kind) || !ModelsList.Contains(model) || !Billings.Contains(billing)) return Bad("Unknown kind, licensing or billing.");
        var seats = b.TryGetProperty("seats", out var sv) && sv.TryGetInt32(out var si) ? si : 1;
        if (seats is < 0 or > 1_000_000) return Bad("Seats are between 0 and 1,000,000.");
        decimal? cost = b.TryGetProperty("cost", out var cv2) && cv2.ValueKind == JsonValueKind.Number && cv2.TryGetDecimal(out var cd) ? cd : null;
        if (cost is < 0) return Bad("The cost cannot be negative.");
        DateOnly? Date(string n, out bool bad)
        {
            bad = false; var v = S(b, n); if (string.IsNullOrEmpty(v)) return null;
            if (DateOnly.TryParseExact(v, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
            bad = true; return null;
        }
        var pd = Date("purchaseDate", out var b1); var sd = Date("startDate", out var b2); var ed = Date("endDate", out var b3);
        if (b1 || b2 || b3) return Bad("Dates are year-month-day.");
        if (sd != null && ed != null && ed < sd) return Bad("The end is before the start.");
        if (kind == "subscription" && ed == null) return Bad("A subscription has an end date (the next renewal).");
        int? vendor = b.TryGetProperty("vendorId", out var vv) && vv.TryGetInt32(out var vi) ? vi : null;
        if (vendor != null)
        {
            await using var x = new NpgsqlCommand("select 1 from assets.vendors where id = @v and (company_id is null or company_id = @c)", db); P(x, "v", vendor); P(x, "c", company);
            if (await x.ExecuteScalarAsync(ct) == null) return Bad("That vendor is not shared or this customer's.");
        }
        var url = S(b, "portalUrl") ?? "";
        if (url != "" && !(Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http")) return Bad("The portal is a web address (https://…).");
        foreach (var n in new[] { "publisher", "edition", "currency", "poNumber", "invoiceNumber", "contractNo", "match" }) if ((S(b, n) ?? "").Length > 200) return Bad(n + " is too long.");
        if ((S(b, "notes") ?? "").Length > 4000) return Bad("The notes are too long.");
        var keyGiven = Has(b, "licenseKey"); var key = S(b, "licenseKey") ?? "";
        if (key.Length > 4000) return Bad("The licence key is too long.");
        var sealedKey = key == "" ? "" : Keys(dp, t).Protect(key);

        await using var c = new NpgsqlCommand(id == null
            ? @"insert into assets.licenses (company_id, product, publisher, edition, kind, model, seats, cost, currency, billing, vendor_id, purchase_date, start_date, end_date, auto_renew,
                    po_number, invoice_number, contract_no, portal_url, key_sealed, match, notes) values (@co,@p,@pub,@ed,@k,@m,@s,@cost,@cur,@bill,@v,@pd,@sd,@end,@ar,@po,@inv,@cn,@url,@key,@match,@notes) returning id"
            : @"update assets.licenses set product=@p, publisher=@pub, edition=@ed, kind=@k, model=@m, seats=@s, cost=@cost, currency=@cur, billing=@bill, vendor_id=@v, purchase_date=@pd, start_date=@sd,
                    end_date=@end, auto_renew=@ar, po_number=@po, invoice_number=@inv, contract_no=@cn, portal_url=@url, key_sealed = case when @kg then @key else key_sealed end, match=@match, notes=@notes,
                    updated_at = now() where id = @i returning id", db);
        P(c, "co", company); P(c, "p", product); P(c, "pub", S(b, "publisher") ?? ""); P(c, "ed", S(b, "edition") ?? ""); P(c, "k", kind); P(c, "m", model); P(c, "s", seats);
        c.Parameters.Add(new NpgsqlParameter("cost", NpgsqlDbType.Numeric) { Value = cost.HasValue ? cost.Value : DBNull.Value });
        P(c, "cur", (S(b, "currency") ?? "").ToUpperInvariant()); P(c, "bill", billing); P(c, "v", vendor); PD(c, "pd", pd); PD(c, "sd", sd); PD(c, "end", ed);
        P(c, "ar", b.TryGetProperty("autoRenew", out var ar) && ar.ValueKind == JsonValueKind.True); P(c, "po", S(b, "poNumber") ?? ""); P(c, "inv", S(b, "invoiceNumber") ?? "");
        P(c, "cn", S(b, "contractNo") ?? ""); P(c, "url", url); P(c, "key", sealedKey); P(c, "kg", keyGiven); P(c, "match", S(b, "match") ?? ""); P(c, "notes", S(b, "notes") ?? ""); P(c, "i", id ?? 0);
        var nid = (int)(await c.ExecuteScalarAsync(ct))!;
        await audit.WriteAsync(id == null ? "assets.license_created" : "assets.license_changed", nid.ToString(CultureInfo.InvariantCulture), product + (keyGiven ? " (key changed)" : ""), ct: ct);
        return Results.Ok(new { id = nid });
    }
}

/// <summary>
/// Whether deleting an asset waits for an approver. Off by default: people who may change an asset delete it themselves. On: only people
/// with the delete right delete; everyone else sends a request.
/// </summary>
internal static class DeleteApproval
{
    public const string Key = "assets.delete_approval";
    public static async Task<bool> RequiredAsync(ISettings s, CancellationToken ct) => (await s.GetAsync(Key, ct))?.Trim() == "true";

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        r.MapGet("/api/assets/delete-approval", async (ISettings s, CancellationToken ct) => Results.Ok(new { required = await RequiredAsync(s, ct) }))
            .RequireAuthorization(p => p.RequireRole(AssetsAccess.Readers)).RequireModule(key);
        r.MapPut("/api/assets/delete-approval", async (JsonElement b, ISettings s, IAuditLog audit, CancellationToken ct) =>
        {
            var on = b.ValueKind == JsonValueKind.Object && b.TryGetProperty("required", out var v) && v.ValueKind == JsonValueKind.True;
            await s.SetAsync(Key, on ? "true" : "false", ct);
            await audit.WriteAsync("assets.delete_approval_set", "settings", on ? "on" : "off", ct: ct);
            return Results.Ok(new { required = on });
        }).RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);
    }
}
