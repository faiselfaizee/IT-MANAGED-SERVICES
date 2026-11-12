using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace DeskAsk.Platform.ServiceDesk;

internal sealed record FieldDef(int Id, string Key, string Label, string Kind, string[] Options, bool Required, bool StaffOnly, bool Active, int Position);

/// <summary>
/// Extra fields an administrator adds to every ticket (an asset serial, a cost centre, a site). The definitions live in servicedesk.custom_fields,
/// the values in the ticket's own custom column, keyed by the field's key. A field can be required and can be for staff only.
/// </summary>
internal static class CustomFields
{
    static readonly string[] Kinds = { "text", "longtext", "number", "date", "choice", "checkbox" };

    public static async Task<List<FieldDef>> LoadAsync(NpgsqlConnection db, bool activeOnly, CancellationToken ct)
    {
        var l = new List<FieldDef>();
        await using var c = new NpgsqlCommand("select id, key, label, kind, options, required, staff_only, active, position from servicedesk.custom_fields " + (activeOnly ? "where active " : "") + "order by position, id", db);
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) l.Add(new FieldDef(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), SplitOptions(r.GetString(4)), r.GetBoolean(5), r.GetBoolean(6), r.GetBoolean(7), r.GetInt32(8)));
        return l;
    }

    static string[] SplitOptions(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static Dictionary<string, object?> Parse(string? json)
    {
        var d = new Dictionary<string, object?>();
        if (string.IsNullOrWhiteSpace(json)) return d;
        try
        {
            foreach (var p in JsonDocument.Parse(json).RootElement.EnumerateObject())
                d[p.Name] = p.Value.ValueKind switch { JsonValueKind.String => p.Value.GetString(), JsonValueKind.Number => p.Value.GetDecimal(), JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException) { }
        return d;
    }

    public static string ToJson(Dictionary<string, object?> d) => JsonSerializer.Serialize(d.Where(x => x.Value != null).ToDictionary(x => x.Key, x => x.Value));

    /// <summary>Checks the values a person sent. Returns key to value, with null meaning "clear it". Throws FormatException with a sentence that says what is wrong.</summary>
    public static Dictionary<string, object?> Check(JsonElement custom, List<FieldDef> defs, bool staff)
    {
        var o = new Dictionary<string, object?>();
        if (custom.ValueKind == JsonValueKind.Null || custom.ValueKind == JsonValueKind.Undefined) return o;
        if (custom.ValueKind != JsonValueKind.Object) throw new FormatException("custom must be an object of field values");
        foreach (var p in custom.EnumerateObject())
        {
            var f = defs.FirstOrDefault(x => x.Active && x.Key == p.Name) ?? throw new FormatException("There is no field called '" + p.Name + "'.");
            if (f.StaffOnly && !staff) throw new FormatException("'" + f.Label + "' is only filled in by the support team.");
            var v = p.Value; object? val;
            if (v.ValueKind == JsonValueKind.Null || (v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() == "")) val = null;
            else switch (f.Kind)
            {
                case "text" or "longtext":
                    if (v.ValueKind != JsonValueKind.String) throw new FormatException("'" + f.Label + "' must be text.");
                    var s = v.GetString()!.Trim(); var max = f.Kind == "text" ? 500 : 4000;
                    if (s.Length > max) throw new FormatException("'" + f.Label + "' is longer than " + max + " characters.");
                    val = s; break;
                case "number":
                    if (v.ValueKind != JsonValueKind.Number || !v.TryGetDecimal(out var n)) throw new FormatException("'" + f.Label + "' must be a number.");
                    val = n; break;
                case "date":
                    if (v.ValueKind != JsonValueKind.String || !DateTime.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) throw new FormatException("'" + f.Label + "' must be a date like 2026-11-30.");
                    val = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); break;
                case "choice":
                    var pick = v.ValueKind == JsonValueKind.String ? f.Options.FirstOrDefault(x => string.Equals(x, v.GetString()!.Trim(), StringComparison.OrdinalIgnoreCase)) : null;
                    if (pick == null) throw new FormatException("'" + f.Label + "' must be one of: " + string.Join(", ", f.Options) + ".");
                    val = pick; break;
                case "checkbox":
                    if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new FormatException("'" + f.Label + "' must be true or false.");
                    val = v.ValueKind == JsonValueKind.True; break;
                default: throw new FormatException("Unknown field type.");
            }
            if (val == null && f.Required) throw new FormatException("'" + f.Label + "' is required.");
            o[f.Key] = val;
        }
        return o;
    }

    /// <summary>Every required field the person may fill in must have a value.</summary>
    public static void RequireAll(Dictionary<string, object?> values, List<FieldDef> defs, bool staff)
    {
        foreach (var f in defs.Where(x => x.Active && x.Required && (staff || !x.StaffOnly)))
            if (!values.TryGetValue(f.Key, out var v) || v == null || (f.Kind == "checkbox" && v is false)) throw new FormatException("'" + f.Label + "' is required.");
    }

    static string Slug(string label)
    {
        var s = Regex.Replace(label.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        if (s == "" || char.IsDigit(s[0])) s = "f_" + s;
        return s.Length > 40 ? s[..40] : s;
    }

    static object Out(FieldDef f) => new { id = f.Id, key = f.Key, label = f.Label, kind = f.Kind, options = f.Options, required = f.Required, staffOnly = f.StaffOnly, active = f.Active, position = f.Position };

    static string? Str(JsonElement b, string n, int max) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? Clip(v.GetString()!, max) : null;
    static string Clip(string s, int max) { s = s.Trim(); return s.Length > max ? s[..max] : s; }
    static bool? Flag(JsonElement b, string n) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.ValueKind == JsonValueKind.True : null;

    static string? CleanOptions(string? raw, out string? error)
    {
        error = null; var l = (raw ?? "").Split(new[] { '\n', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.Length > 80 ? x[..80] : x).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (l.Count == 0) { error = "A choice field needs at least one choice."; return null; }
        if (l.Count > 50) { error = "A choice field can have at most 50 choices."; return null; }
        return string.Join("\n", l);
    }

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        RouteHandlerBuilder Admin(RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);

        r.MapGet("/api/servicedesk/fields", async (bool? all, ClaimsPrincipal u, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var w = await ServiceDeskModule.WhoAsync(u, db, ct);
            if (w.Role == "") return Results.Json(new { error = "forbidden" }, statusCode: 403);
            var defs = await LoadAsync(db, !(all == true && w.Admin), ct);
            return Results.Ok(new { items = defs.Where(f => w.Staff || w.Role == "viewer" || !f.StaffOnly).Select(Out) });
        }).RequireAuthorization().RequireModule(key);

        Admin(r.MapPost("/api/servicedesk/fields", async (JsonElement b, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var label = Str(b, "label", 80) ?? ""; if (label == "") return ServiceDeskModule.Bad("bad_input", "Give the field a name.");
            var kind = Str(b, "kind", 20) ?? "text"; if (!Kinds.Contains(kind)) return ServiceDeskModule.Bad("bad_input", "kind must be one of " + string.Join(", ", Kinds));
            string options = "";
            if (kind == "choice") { options = CleanOptions(Str(b, "options", 4000), out var err)!; if (err != null) return ServiceDeskModule.Bad("bad_input", err); }
            await using var db = await ServiceDeskModule.Open(t, ct);
            var taken = (await LoadAsync(db, false, ct)).Select(x => x.Key).ToHashSet(); var k = Slug(label); var baseKey = k; for (var i = 2; taken.Contains(k); i++) k = baseKey + "_" + i;
            await using var c = new NpgsqlCommand("insert into servicedesk.custom_fields (key, label, kind, options, required, staff_only, position) values (@k,@l,@kind,@o,@r,@s, coalesce((select max(position) + 1 from servicedesk.custom_fields), 0)) returning id", db);
            ServiceDeskModule.P(c, "k", k); ServiceDeskModule.P(c, "l", label); ServiceDeskModule.P(c, "kind", kind); ServiceDeskModule.P(c, "o", options); ServiceDeskModule.P(c, "r", Flag(b, "required") ?? false); ServiceDeskModule.P(c, "s", Flag(b, "staffOnly") ?? false);
            var id = (int)(await c.ExecuteScalarAsync(ct))!;
            await audit.WriteAsync("servicedesk.field_created", label, kind, ct: ct);
            return Results.Ok(new { id, key = k });
        }));
        Admin(r.MapPut("/api/servicedesk/fields/{id:int}", async (int id, JsonElement b, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            var cur = (await LoadAsync(db, false, ct)).FirstOrDefault(x => x.Id == id); if (cur == null) return Results.NotFound();
            var label = Str(b, "label", 80) ?? cur.Label; if (label == "") return ServiceDeskModule.Bad("bad_input", "Give the field a name.");
            var options = string.Join("\n", cur.Options);
            if (cur.Kind == "choice" && b.TryGetProperty("options", out _)) { options = CleanOptions(Str(b, "options", 4000), out var err)!; if (err != null) return ServiceDeskModule.Bad("bad_input", err); }
            await using var c = new NpgsqlCommand("update servicedesk.custom_fields set label = @l, options = @o, required = @r, staff_only = @s, active = @a, position = @p where id = @i", db);
            ServiceDeskModule.P(c, "l", label); ServiceDeskModule.P(c, "o", options); ServiceDeskModule.P(c, "r", Flag(b, "required") ?? cur.Required); ServiceDeskModule.P(c, "s", Flag(b, "staffOnly") ?? cur.StaffOnly); ServiceDeskModule.P(c, "a", Flag(b, "active") ?? cur.Active);
            ServiceDeskModule.P(c, "p", b.TryGetProperty("position", out var pv) && pv.ValueKind == JsonValueKind.Number && pv.TryGetInt32(out var pi) ? pi : cur.Position); ServiceDeskModule.P(c, "i", id);
            await c.ExecuteNonQueryAsync(ct);
            await audit.WriteAsync("servicedesk.field_changed", label, "", ct: ct);
            return Results.Ok(new { ok = true });
        }));
        Admin(r.MapDelete("/api/servicedesk/fields/{id:int}", async (int id, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await ServiceDeskModule.Open(t, ct);
            await using var c = new NpgsqlCommand("delete from servicedesk.custom_fields where id = @i", db); ServiceDeskModule.P(c, "i", id);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await audit.WriteAsync("servicedesk.field_removed", id.ToString(), "", ct: ct);
            return Results.NoContent();
        }));
    }
}
