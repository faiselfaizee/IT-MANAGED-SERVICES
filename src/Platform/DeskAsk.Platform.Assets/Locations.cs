using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace DeskAsk.Platform.Assets;

/// <summary>
/// A customer's locations (countries, states or regions, and sites with a site code) and the vendors assets are bought from. Administrators
/// manage every customer's; a customer administrator manages their own company's; anyone granted "modify" on Assets for a customer
/// manages that customer's. Vendors without a customer are your own suppliers, shared by every customer, and only administrators change them.
/// </summary>
public static class LocationsApi
{
    static async Task<NpgsqlConnection> Open(ITenantContext t, CancellationToken ct) { var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct); return db; }
    static void P(NpgsqlCommand c, string n, object? v) => c.Parameters.AddWithValue(n, v ?? DBNull.Value);
    static IResult Bad(string detail) => Results.BadRequest(new { error = "bad_input", detail });
    static IResult Forbid(string detail) => Results.Json(new { error = "forbidden", detail }, statusCode: 403);
    static readonly Regex Email = new(@"^[^@\s,;]+@[^@\s,;]+\.[^@\s,;]+$");

    /// <summary>The companies whose locations and vendors this person may change: null is every company.</summary>
    public static async Task<IReadOnlyList<int>?> ManageScope(HttpContext h)
    {
        var role = h.User.FindFirstValue("role") ?? "";
        if (role == "admin") return null;
        var modify = await AssetsAccess.ScopeAsync(h, Acts.Modify);
        if (modify == null) return null;
        var set = new HashSet<int>(modify);
        if (role == "cadmin" && await AssetsAccess.ScopeAsync(h, Acts.Read) is { } own) set.UnionWith(own);
        return set.ToList();
    }
    static bool May(IReadOnlyList<int>? scope, int? company) => scope == null || (company != null && scope.Contains(company.Value));

    /// <summary>Reads text fields; returns an error message, or null.</summary>
    static string? Text(JsonElement b, (string Json, int Max)[] fields, Dictionary<string, string?> into)
    {
        foreach (var (k, max) in fields)
        {
            if (!b.TryGetProperty(k, out var v)) { into[k] = null; continue; }
            if (v.ValueKind == JsonValueKind.Null) { into[k] = ""; continue; }
            if (v.ValueKind != JsonValueKind.String) return k + " must be text";
            var s = v.GetString()!.Trim(); if (s.Length > max) return $"{k} is longer than {max} characters";
            into[k] = s;
        }
        return null;
    }
    static bool? Flag(JsonElement b, string k) => b.TryGetProperty(k, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    static int? Int(JsonElement b, string k) => b.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    static string? Emails(string? list)
    {
        if (string.IsNullOrEmpty(list)) return null;
        foreach (var a in list.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)) if (!Email.IsMatch(a)) return $"\"{a}\" is not an email address";
        return null;
    }

    static readonly (string, int)[] PlaceFields = { ("name", 120), ("code", 30), ("address", 400), ("city", 80), ("postalCode", 20), ("contactName", 120), ("contactEmail", 200), ("contactPhone", 40), ("notes", 2000) };
    static readonly string[] PlaceCols = { "name", "code", "address", "city", "postal_code", "contact_name", "contact_email", "contact_phone", "notes" };

    static readonly (string, int)[] VendorFields = { ("name", 160), ("code", 30), ("address", 400), ("city", 80), ("state", 80), ("country", 80), ("postalCode", 20),
        ("contactName", 120), ("contactEmail", 200), ("contactPhone", 40), ("renewalEmail", 300), ("website", 200), ("gst", 20), ("pan", 12), ("tan", 12), ("vat", 30), ("cin", 25), ("paymentTerms", 120), ("notes", 4000) };
    static readonly string[] VendorCols = { "name", "code", "address", "city", "state", "country", "postal_code", "contact_name", "contact_email", "contact_phone", "renewal_email", "website", "gst", "pan", "tan", "vat", "cin", "payment_terms", "notes" };
    /// <summary>Indian GSTIN: 2-digit state, PAN, entity, Z, check character.</summary>
    static readonly Regex Gstin = new("^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$");
    static readonly Regex Pan = new("^[A-Z]{5}[0-9]{4}[A-Z]$");
    static readonly Regex Tan = new("^[A-Z]{4}[0-9]{5}[A-Z]$");

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        RouteHandlerBuilder Read(RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole(AssetsAccess.Readers)).RequireFoundation().RequireAccess(key, Acts.Read);
        RouteHandlerBuilder Write(RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole(AssetsAccess.Readers)).RequireFoundation();

        // ------------------------------------------------------------ locations
        Read(r.MapGet("/api/assets/places", async (int? company, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read); var manage = await ManageScope(http);
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand(@"select p.id, p.company_id, co.name, p.parent_id, p.level, p.name, p.code, p.address, p.city, p.postal_code, p.contact_name, p.contact_email, p.contact_phone, p.notes, p.active,
                    (select count(*) from assets.assets a where a.site_id = p.id)
                from assets.places p join assets.companies co on co.id = p.company_id
                where (not @sc or p.company_id = any(@scope)) and (@co = 0 or p.company_id = @co) order by co.name, p.level, lower(p.name)", db).Scoped(scope);
            P(c, "co", company ?? 0);
            var rows = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                rows.Add(new
                {
                    id = rd.GetInt32(0), companyId = rd.GetInt32(1), company = rd.GetString(2), parentId = rd.IsDBNull(3) ? (int?)null : rd.GetInt32(3), level = rd.GetString(4), name = rd.GetString(5), code = rd.GetString(6),
                    address = rd.GetString(7), city = rd.GetString(8), postalCode = rd.GetString(9), contactName = rd.GetString(10), contactEmail = rd.GetString(11), contactPhone = rd.GetString(12), notes = rd.GetString(13),
                    active = rd.GetBoolean(14), assets = rd.GetInt64(15), canEdit = May(manage, rd.GetInt32(1)),
                });
            return Results.Ok(new { items = rows, canManage = manage == null || manage.Count > 0, manageAll = manage == null, manageCompanies = manage });
        }));

        async Task<IResult> SavePlace(int? id, JsonElement b, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct)
        {
            if (b.ValueKind != JsonValueKind.Object) return Bad("Send the location as a JSON object.");
            var v = new Dictionary<string, string?>(); if (Text(b, PlaceFields, v) is { } err) return Bad(err);
            if (Emails(v["contactEmail"]) is { } ee) return Bad(ee);
            var scope = await ManageScope(http);
            await using var db = await Open(t, ct);
            int company; string level; int? parent;
            if (id == null)
            {
                company = Int(b, "companyId") ?? 0; level = b.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.String ? lv.GetString()! : "";
                parent = Int(b, "parentId");
                if (level is not ("country" or "state" or "site")) return Bad("level must be country, state or site");
                if (string.IsNullOrEmpty(v["name"])) return Bad("Give the location a name.");
                await using (var q = new NpgsqlCommand("select 1 from assets.companies where id = @i", db)) { P(q, "i", company); if (await q.ExecuteScalarAsync(ct) == null) return Bad("Choose the customer."); }
            }
            else
            {
                await using var q = new NpgsqlCommand("select company_id, level, parent_id from assets.places where id = @i", db); P(q, "i", id);
                await using var rd = await q.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return Results.NotFound();
                company = rd.GetInt32(0); level = rd.GetString(1); parent = rd.IsDBNull(2) ? null : rd.GetInt32(2);
                if (b.TryGetProperty("parentId", out _)) parent = Int(b, "parentId");
                if (v["name"] == "") return Bad("The name cannot be empty.");
            }
            if (!May(scope, company)) return Forbid("You may not change this customer's locations.");
            // a country has no parent; a state sits in a country; a site sits in a state or straight in a country
            if (level == "country" && parent != null) return Bad("A country is the top level.");
            if (level != "country")
            {
                if (parent == null) return Bad(level == "state" ? "Choose the country the state is in." : "Choose the country or state the site is in.");
                await using var q = new NpgsqlCommand("select level from assets.places where id = @p and company_id = @c", db); P(q, "p", parent); P(q, "c", company);
                var pl = await q.ExecuteScalarAsync(ct) as string;
                if (pl == null) return Bad("That parent location is not one of this customer's.");
                if (level == "state" && pl != "country") return Bad("A state sits in a country.");
                if (level == "site" && pl == "site") return Bad("A site sits in a country or a state, not in another site.");
            }
            var active = Flag(b, "active");
            try
            {
                if (id == null)
                {
                    await using var c = new NpgsqlCommand($"insert into assets.places (company_id, parent_id, level, {string.Join(", ", PlaceCols)}) values (@co, @pa, @lv, {string.Join(", ", PlaceCols.Select((_, i) => "@v" + i))}) returning id", db);
                    P(c, "co", company); P(c, "pa", parent); P(c, "lv", level);
                    for (var i = 0; i < PlaceCols.Length; i++) P(c, "v" + i, v[PlaceFields[i].Item1] ?? "");
                    var nid = (int)(await c.ExecuteScalarAsync(ct))!;
                    await audit.WriteAsync("assets.place_created", nid.ToString(), $"{level} {v["name"]} (customer {company})", ct: ct);
                    return Results.Ok(new { id = nid });
                }
                await using var u = new NpgsqlCommand($"update assets.places set parent_id = @pa, active = coalesce(@ac, active), {string.Join(", ", PlaceCols.Select((col, i) => $"{col} = coalesce(@v{i}, {col})"))} where id = @id", db);
                P(u, "pa", parent); P(u, "ac", active); P(u, "id", id);
                for (var i = 0; i < PlaceCols.Length; i++) P(u, "v" + i, v[PlaceFields[i].Item1]);
                await u.ExecuteNonQueryAsync(ct);
                await audit.WriteAsync("assets.place_changed", id.ToString(), v["name"] ?? "", ct: ct);
                return Results.Ok(new { id });
            }
            catch (PostgresException e) when (e.SqlState == "23505")
            {
                return Results.Conflict(new { error = "exists", detail = e.ConstraintName == "places_code" ? "Another location of this customer has that code." : "This customer already has a location with that name there." });
            }
        }
        Write(r.MapPost("/api/assets/places", (JsonElement b, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) => SavePlace(null, b, http, t, audit, ct)));
        Write(r.MapPut("/api/assets/places/{id:int}", (int id, JsonElement b, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) => SavePlace(id, b, http, t, audit, ct)));
        Write(r.MapDelete("/api/assets/places/{id:int}", async (int id, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            int company; string name; long kids, assets;
            await using (var q = new NpgsqlCommand("select company_id, name, (select count(*) from assets.places c where c.parent_id = p.id), (select count(*) from assets.assets a where a.site_id = p.id) from assets.places p where id = @i", db))
            {
                P(q, "i", id); await using var rd = await q.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return Results.NotFound();
                company = rd.GetInt32(0); name = rd.GetString(1); kids = rd.GetInt64(2); assets = rd.GetInt64(3);
            }
            if (!May(await ManageScope(http), company)) return Forbid("You may not change this customer's locations.");
            if (kids > 0) return Results.Conflict(new { error = "in_use", detail = $"{name} still has {kids} location{(kids == 1 ? "" : "s")} inside it. Move or delete those first." });
            if (assets > 0) return Results.Conflict(new { error = "in_use", detail = $"{assets} asset{(assets == 1 ? " is" : "s are")} at {name}. Move them to another site first, or mark the site inactive." });
            await using (var c = new NpgsqlCommand("delete from assets.places where id = @i", db)) { P(c, "i", id); await c.ExecuteNonQueryAsync(ct); }
            await audit.WriteAsync("assets.place_deleted", id.ToString(), name, ct: ct);
            return Results.Ok(new { ok = true });
        }));

        // move every asset of one site to another (closing an office)
        Write(r.MapPost("/api/assets/places/{id:int}/move", async (int id, JsonElement b, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var to = Int(b, "toSiteId"); await using var db = await Open(t, ct);
            await using var q = new NpgsqlCommand("select (select company_id from assets.places where id = @f and level = 'site'), (select company_id from assets.places where id = @t and level = 'site')", db); P(q, "f", id); P(q, "t", to);
            int? fc, tc; await using (var rd = await q.ExecuteReaderAsync(ct)) { await rd.ReadAsync(ct); fc = rd.IsDBNull(0) ? null : rd.GetInt32(0); tc = rd.IsDBNull(1) ? null : rd.GetInt32(1); }
            if (fc == null || tc == null || fc != tc) return Bad("Both must be sites of the same customer.");
            if (!May(await ManageScope(http), fc)) return Forbid("You may not change this customer's locations.");
            await using var c = new NpgsqlCommand("update assets.assets set site_id = @t, updated_at = now() where site_id = @f", db); P(c, "t", to); P(c, "f", id);
            var n = await c.ExecuteNonQueryAsync(ct);
            await audit.WriteAsync("assets.place_assets_moved", id.ToString(), $"{n} assets to site {to}", ct: ct);
            return Results.Ok(new { moved = n });
        }));

        // ------------------------------------------------------------ vendors
        Read(r.MapGet("/api/assets/vendors", async (int? company, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read); var manage = await ManageScope(http);
            await using var db = await Open(t, ct);
            await using var c = new NpgsqlCommand($@"select v.id, v.company_id, coalesce(co.name, ''), {string.Join(", ", VendorCols.Select(x => "v." + x))}, v.other_ids, v.remind, v.active,
                    (select count(*) from assets.assets a where a.vendor_id = v.id and (not @sc or a.company_id = any(@scope))),
                    (select count(*) from assets.amc m where m.vendor_id = v.id and (not @sc or m.company_id = any(@scope))),
                    (select min(a.warranty_end) from assets.assets a where a.vendor_id = v.id and a.warranty_end >= current_date and a.status not in ('retired','lost') and (not @sc or a.company_id = any(@scope)))
                from assets.vendors v left join assets.companies co on co.id = v.company_id
                where (v.company_id is null or not @sc or v.company_id = any(@scope)) and (@co = 0 or v.company_id is null or v.company_id = @co)
                order by v.company_id is not null, lower(v.name)", db).Scoped(scope);
            P(c, "co", company ?? 0);
            var rows = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rows.Add(VendorRow(rd, manage));
            return Results.Ok(new { items = rows, canManage = manage == null || manage.Count > 0, manageAll = manage == null, manageCompanies = manage });
        }));

        Read(r.MapGet("/api/assets/vendors/{id:int}", async (int id, HttpContext http, ITenantContext t, CancellationToken ct) =>
        {
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read); var manage = await ManageScope(http);
            await using var db = await Open(t, ct);
            object? vendor = null;
            await using (var c = new NpgsqlCommand($@"select v.id, v.company_id, coalesce(co.name, ''), {string.Join(", ", VendorCols.Select(x => "v." + x))}, v.other_ids, v.remind, v.active,
                    (select count(*) from assets.assets a where a.vendor_id = v.id and (not @sc or a.company_id = any(@scope))),
                    (select count(*) from assets.amc m where m.vendor_id = v.id and (not @sc or m.company_id = any(@scope))), null::date
                from assets.vendors v left join assets.companies co on co.id = v.company_id where v.id = @i and (v.company_id is null or not @sc or v.company_id = any(@scope))", db).Scoped(scope))
            { P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct); if (await rd.ReadAsync(ct)) vendor = VendorRow(rd, manage); }
            if (vendor == null) return Results.NotFound();
            var assets = new List<object>();
            await using (var c = new NpgsqlCommand(@"select a.id, a.tag, a.name, coalesce(co.name, ''), a.status, a.purchase_date, a.purchase_cost, a.warranty_end, a.warranty_months, a.po_number, a.invoice_number, coalesce(s.name, '')
                from assets.assets a left join assets.companies co on co.id = a.company_id left join assets.places s on s.id = a.site_id
                where a.vendor_id = @i and (not @sc or a.company_id = any(@scope)) order by a.warranty_end nulls last, a.id limit 2000", db).Scoped(scope))
            {
                P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                    assets.Add(new { id = rd.GetInt64(0), tag = rd.GetString(1), name = rd.GetString(2), company = rd.GetString(3), status = rd.GetString(4),
                        purchaseDate = rd.IsDBNull(5) ? null : rd.GetFieldValue<DateOnly>(5).ToString("yyyy-MM-dd"), purchaseCost = rd.IsDBNull(6) ? (decimal?)null : rd.GetDecimal(6),
                        warrantyEnd = rd.IsDBNull(7) ? null : rd.GetFieldValue<DateOnly>(7).ToString("yyyy-MM-dd"), warrantyMonths = rd.IsDBNull(8) ? (int?)null : rd.GetInt32(8),
                        poNumber = rd.GetString(9), invoiceNumber = rd.GetString(10), site = rd.GetString(11) });
            }
            var amc = new List<object>();
            await using (var c = new NpgsqlCommand(@"select m.id, m.title, coalesce(co.name, ''), m.contract_no, m.start_date, m.end_date, m.cost, jsonb_array_length(m.asset_ids), m.previous_id
                from assets.amc m left join assets.companies co on co.id = m.company_id where m.vendor_id = @i and (not @sc or m.company_id = any(@scope)) order by m.end_date desc nulls first", db).Scoped(scope))
            {
                P(c, "i", id); await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                    amc.Add(new { id = rd.GetInt32(0), title = rd.GetString(1), company = rd.GetString(2), contractNo = rd.GetString(3), start = rd.IsDBNull(4) ? null : rd.GetFieldValue<DateOnly>(4).ToString("yyyy-MM-dd"),
                        end = rd.IsDBNull(5) ? null : rd.GetFieldValue<DateOnly>(5).ToString("yyyy-MM-dd"), cost = rd.IsDBNull(6) ? (decimal?)null : rd.GetDecimal(6), assets = rd.GetInt32(7), previousId = rd.IsDBNull(8) ? (int?)null : rd.GetInt32(8) });
            }
            return Results.Ok(new { vendor, assets, amc });
        }));

        async Task<IResult> SaveVendor(int? id, JsonElement b, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct)
        {
            if (b.ValueKind != JsonValueKind.Object) return Bad("Send the vendor as a JSON object.");
            var v = new Dictionary<string, string?>(); if (Text(b, VendorFields, v) is { } err) return Bad(err);
            foreach (var k in new[] { "gst", "pan", "tan", "cin" }) if (v[k] != null) v[k] = v[k]!.ToUpperInvariant().Replace(" ", "");
            if (!string.IsNullOrEmpty(v["gst"]) && !Gstin.IsMatch(v["gst"]!)) return Bad("The GSTIN should have 15 characters, like 29ABCDE1234F1Z5.");
            if (!string.IsNullOrEmpty(v["pan"]) && !Pan.IsMatch(v["pan"]!)) return Bad("The PAN should have 10 characters, like ABCDE1234F.");
            if (!string.IsNullOrEmpty(v["tan"]) && !Tan.IsMatch(v["tan"]!)) return Bad("The TAN should have 10 characters, like BLRA12345B.");
            if (!string.IsNullOrEmpty(v["gst"]) && !string.IsNullOrEmpty(v["pan"]) && v["gst"]![2..12] != v["pan"]) return Bad("The GSTIN does not contain this PAN (characters 3 to 12 of a GSTIN are the PAN).");
            if (Emails(v["contactEmail"]) is { } e1) return Bad(e1);
            if (Emails(v["renewalEmail"]) is { } e2) return Bad(e2);
            string? others = null;
            if (b.TryGetProperty("otherIds", out var oi))
            {
                if (oi.ValueKind != JsonValueKind.Array || oi.GetArrayLength() > 20) return Bad("otherIds must be a list of at most 20 registrations");
                var list = new List<object>();
                foreach (var x in oi.EnumerateArray())
                {
                    var label = x.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString()!.Trim() : "";
                    var value = x.TryGetProperty("value", out var vv) && vv.ValueKind == JsonValueKind.String ? vv.GetString()!.Trim() : "";
                    if (label == "" && value == "") continue;
                    if (label == "" || value == "" || label.Length > 60 || value.Length > 80) return Bad("Each other registration needs a name (up to 60 characters) and a number (up to 80).");
                    list.Add(new { label, value });
                }
                others = JsonSerializer.Serialize(list);
            }
            var scope = await ManageScope(http);
            await using var db = await Open(t, ct);
            int? company;
            if (id == null)
            {
                company = Int(b, "companyId");
                if (string.IsNullOrEmpty(v["name"])) return Bad("Give the vendor's name.");
                if (company != null) await using (var q = new NpgsqlCommand("select 1 from assets.companies where id = @i", db)) { P(q, "i", company); if (await q.ExecuteScalarAsync(ct) == null) return Bad("That customer does not exist."); }
            }
            else
            {
                await using var q = new NpgsqlCommand("select company_id from assets.vendors where id = @i", db); P(q, "i", id);
                var o = await q.ExecuteScalarAsync(ct); if (o == null) return Results.NotFound();
                company = o is int ci ? ci : null;
                if (v["name"] == "") return Bad("The name cannot be empty.");
            }
            if (!May(scope, company)) return Forbid(company == null ? "Only administrators change vendors shared by every customer." : "You may not change this customer's vendors.");
            try
            {
                if (id == null)
                {
                    await using var c = new NpgsqlCommand($"insert into assets.vendors (company_id, other_ids, remind, active, {string.Join(", ", VendorCols)}) values (@co, coalesce(@oi::jsonb, '[]'), coalesce(@rm, true), coalesce(@ac, true), {string.Join(", ", VendorCols.Select((_, i) => "@v" + i))}) returning id", db);
                    P(c, "co", company); P(c, "oi", others); P(c, "rm", Flag(b, "remind")); P(c, "ac", Flag(b, "active"));
                    for (var i = 0; i < VendorCols.Length; i++) P(c, "v" + i, v[VendorFields[i].Item1] ?? "");
                    var nid = (int)(await c.ExecuteScalarAsync(ct))!;
                    await audit.WriteAsync("assets.vendor_created", nid.ToString(), v["name"]!, ct: ct);
                    return Results.Ok(new { id = nid });
                }
                await using var u = new NpgsqlCommand($"update assets.vendors set other_ids = coalesce(@oi::jsonb, other_ids), remind = coalesce(@rm, remind), active = coalesce(@ac, active), {string.Join(", ", VendorCols.Select((col, i) => $"{col} = coalesce(@v{i}, {col})"))} where id = @id", db);
                P(u, "oi", others); P(u, "rm", Flag(b, "remind")); P(u, "ac", Flag(b, "active")); P(u, "id", id);
                for (var i = 0; i < VendorCols.Length; i++) P(u, "v" + i, v[VendorFields[i].Item1]);
                await u.ExecuteNonQueryAsync(ct);
                await audit.WriteAsync("assets.vendor_changed", id.ToString(), v["name"] ?? "", ct: ct);
                return Results.Ok(new { id });
            }
            catch (PostgresException e) when (e.SqlState == "23505") { return Results.Conflict(new { error = "exists", detail = "There is already a vendor with that name." }); }
        }
        Write(r.MapPost("/api/assets/vendors", (JsonElement b, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) => SaveVendor(null, b, http, t, audit, ct)));
        Write(r.MapPut("/api/assets/vendors/{id:int}", (int id, JsonElement b, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) => SaveVendor(id, b, http, t, audit, ct)));
        Write(r.MapDelete("/api/assets/vendors/{id:int}", async (int id, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await Open(t, ct);
            int? company; string name; long used;
            await using (var q = new NpgsqlCommand("select company_id, name, (select count(*) from assets.assets a where a.vendor_id = v.id) + (select count(*) from assets.amc m where m.vendor_id = v.id) from assets.vendors v where id = @i", db))
            { P(q, "i", id); await using var rd = await q.ExecuteReaderAsync(ct); if (!await rd.ReadAsync(ct)) return Results.NotFound(); company = rd.IsDBNull(0) ? null : rd.GetInt32(0); name = rd.GetString(1); used = rd.GetInt64(2); }
            if (!May(await ManageScope(http), company)) return Forbid("You may not delete this vendor.");
            if (used > 0) return Results.Conflict(new { error = "in_use", detail = $"{name} is linked to {used} assets or AMC contracts. Mark the vendor inactive instead." });
            await using (var c = new NpgsqlCommand("delete from assets.vendors where id = @i", db)) { P(c, "i", id); await c.ExecuteNonQueryAsync(ct); }
            await audit.WriteAsync("assets.vendor_deleted", id.ToString(), name, ct: ct);
            return Results.Ok(new { ok = true });
        }));
    }

    static object VendorRow(NpgsqlDataReader rd, IReadOnlyList<int>? manage)
    {
        var company = rd.IsDBNull(1) ? (int?)null : rd.GetInt32(1);
        var d = new Dictionary<string, object?> { ["id"] = rd.GetInt32(0), ["companyId"] = company, ["company"] = rd.GetString(2) };
        for (var i = 0; i < VendorCols.Length; i++) d[VendorFields[i].Item1] = rd.GetString(3 + i);
        var n = 3 + VendorCols.Length;
        d["otherIds"] = JsonDocument.Parse(rd.GetString(n)).RootElement.Clone(); d["remind"] = rd.GetBoolean(n + 1); d["active"] = rd.GetBoolean(n + 2);
        d["assets"] = rd.GetInt64(n + 3); d["contracts"] = rd.GetInt64(n + 4); d["nextWarrantyEnd"] = rd.IsDBNull(n + 5) ? null : rd.GetFieldValue<DateOnly>(n + 5).ToString("yyyy-MM-dd");
        d["canEdit"] = May(manage, company) && (company != null || manage == null);
        return d;
    }

    /// <summary>
    /// Checks the site and vendor given for an asset: they must exist, be visible to the person, and belong to the asset's customer
    /// (a shared vendor fits every customer). Returns an error message, or null.
    /// </summary>
    internal static async Task<string?> CheckRefs(NpgsqlConnection db, int? assetCompany, int? siteId, int? vendorId, IReadOnlyList<int>? scope, CancellationToken ct)
    {
        if (siteId != null)
        {
            await using var q = new NpgsqlCommand("select company_id, level from assets.places where id = @i", db); P(q, "i", siteId);
            await using var rd = await q.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct) || (scope != null && !scope.Contains(rd.GetInt32(0)))) return "That site does not exist.";
            if (rd.GetString(1) != "site") return "Choose a site (not a country or state).";
            if (assetCompany != null && rd.GetInt32(0) != assetCompany) return "That site belongs to another customer.";
        }
        if (vendorId != null)
        {
            await using var q = new NpgsqlCommand("select company_id from assets.vendors where id = @i", db); P(q, "i", vendorId);
            var o = await q.ExecuteScalarAsync(ct);
            if (o == null || (o is int c && scope != null && !scope.Contains(c))) return "That vendor does not exist.";
            if (o is int vc && assetCompany != null && vc != assetCompany) return "That vendor belongs to another customer.";
        }
        return null;
    }
}
