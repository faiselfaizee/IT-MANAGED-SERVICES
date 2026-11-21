using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace DeskAsk.Platform.Commerce;

/// <summary>
/// The products on sale (commerce.products). One list for both sides: the website reads it from the platform (GET plans only shows products that are on sale)
/// and may change it with a signed request, the vendor console changes it directly; every change is sent to the website as the event product.changed.
/// A product that is switched off disappears from the store, the website and trials at once; customers who already have it keep it.
/// A product the program does not contain yet (not one of the built-in modules) can be listed and sold, but is only delivered once a release with it is installed.
/// </summary>
public static class Products
{
    static readonly Regex KeyRx = new("^[a-z][a-z0-9]{2,19}$", RegexOptions.Compiled);

    public sealed record Product(string Key, string Name, string Tagline, int Sort, bool OnSale, bool Built, int Sizes);

    public static async Task<List<Product>> ListAsync(NpgsqlConnection db, CancellationToken ct)
    {
        await using var q = new NpgsqlCommand(@"select p.key, p.name, p.tagline, p.sort, p.on_sale, (select count(*) from commerce.plans l where l.module = p.key)
            from commerce.products p order by p.sort, p.key", db);
        await using var rd = await q.ExecuteReaderAsync(ct);
        var list = new List<Product>();
        while (await rd.ReadAsync(ct))
            list.Add(new Product(rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetInt32(3), rd.GetBoolean(4), Modules.All.Contains(rd.GetString(0)), (int)rd.GetInt64(5)));
        return list;
    }

    static object View(Product p) => new { key = p.Key, name = p.Name, tagline = p.Tagline, sort = p.Sort, onSale = p.OnSale, built = p.Built, sizes = p.Sizes };

    public static async Task<object> ListViewAsync(CommerceOptions o, CancellationToken ct)
    {
        await using var db = await CommerceDb.OpenAsync(o, ct);
        return (await ListAsync(db, ct)).Select(View).ToList();
    }

    /// <summary>Changes a product's name, tagline or on-sale flag. Returns an error (and its status) or null.</summary>
    public static async Task<(string Error, string Detail, int Status)?> SetAsync(IServiceProvider sp, string key, bool? onSale, string? name, string? tagline, string by, CancellationToken ct)
    {
        if (name != null && name.Trim().Length is < 2 or > 60) return ("name_bad", "The product name must be 2 to 60 characters.", 400);
        if (tagline != null && tagline.Length > 200) return ("tagline_bad", "The description is too long (200 characters at most).", 400);
        var o = sp.GetRequiredService<CommerceOptions>();
        Product? now;
        await using (var db = await CommerceDb.OpenAsync(o, ct))
        {
            await using var up = new NpgsqlCommand(@"update commerce.products set name = coalesce(@n, name), tagline = coalesce(@t, tagline), on_sale = coalesce(@s, on_sale) where key = @k", db);
            up.Parameters.AddWithValue("k", key);
            up.Parameters.Add(new NpgsqlParameter("n", NpgsqlDbType.Text) { Value = name?.Trim() is { } nn ? nn : DBNull.Value });
            up.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Text) { Value = tagline?.Trim() is { } tt ? tt : DBNull.Value });
            up.Parameters.Add(new NpgsqlParameter("s", NpgsqlDbType.Boolean) { Value = onSale is bool b ? b : DBNull.Value });
            if (await up.ExecuteNonQueryAsync(ct) == 0) return ("not_found", "There is no product '" + key + "'.", 404);
            now = (await ListAsync(db, ct)).First(x => x.Key == key);
        }
        await sp.GetRequiredService<IAuditLog>().WriteAsync("commerce.product_changed", key, (now.OnSale ? "on sale" : "off sale") + "; " + now.Name + "; by " + by, ct: ct);
        await Announce(sp, now, ct);
        return null;
    }

    /// <summary>Adds a product with its first size and prices. The key is its module code (letters and digits).</summary>
    public static async Task<(string Error, string Detail, int Status)?> AddAsync(IServiceProvider sp, JsonElement b, string by, CancellationToken ct)
    {
        string S(string n) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        var key = S("key").ToLowerInvariant(); var name = S("name"); var tagline = S("tagline"); var size = S("sizeLabel");
        if (!KeyRx.IsMatch(key)) return ("key_bad", "The code is 3 to 20 lower-case letters or digits, starting with a letter (for example helpdesk2).", 400);
        if (name.Length is < 2 or > 60) return ("name_bad", "Give the product a name of 2 to 60 characters.", 400);
        if (tagline.Length > 200) return ("tagline_bad", "The description is too long (200 characters at most).", 400);
        if (size.Length is < 1 or > 60) return ("label_missing", "Give its first size a name, for example \"Up to 10 users\".", 400);
        if (!b.TryGetProperty("prices", out var prices) || prices.ValueKind != JsonValueKind.Object) return ("prices_missing", "Prices are missing.", 400);
        var limits = b.TryGetProperty("limits", out var l) && l.ValueKind == JsonValueKind.Object ? l.GetRawText() : "{}";
        var o = sp.GetRequiredService<CommerceOptions>();
        Product now;
        await using (var db = await CommerceDb.OpenAsync(o, ct))
        {
            await using var tx = await db.BeginTransactionAsync(ct);
            try
            {
                await using (var ins = new NpgsqlCommand("insert into commerce.products (key, name, tagline, sort) values (@k, @n, @t, coalesce((select max(sort) from commerce.products), 0) + 10)", db, tx))
                { ins.Parameters.AddWithValue("k", key); ins.Parameters.AddWithValue("n", name); ins.Parameters.AddWithValue("t", tagline); await ins.ExecuteNonQueryAsync(ct); }
                await using (var pl = new NpgsqlCommand("insert into commerce.plans (module, size_label, limits, prices, sort) values (@k, @l, @lim, @p, 10)", db, tx))
                {
                    pl.Parameters.AddWithValue("k", key); pl.Parameters.AddWithValue("l", size);
                    pl.Parameters.Add(new NpgsqlParameter("lim", NpgsqlDbType.Jsonb) { Value = limits });
                    pl.Parameters.Add(new NpgsqlParameter("p", NpgsqlDbType.Jsonb) { Value = prices.GetRawText() });
                    await pl.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }
            catch (PostgresException e) when (e.SqlState == "23505") { return ("key_taken", "A product with that code already exists.", 409); }
            now = (await ListAsync(db, ct)).First(x => x.Key == key);
        }
        await sp.GetRequiredService<IAuditLog>().WriteAsync("commerce.product_added", key, name + "; by " + by, ct: ct);
        await Announce(sp, now, ct);
        return null;
    }

    /// <summary>Tells the website. A website that cannot be reached never blocks the change: the event is kept and retried.</summary>
    static async Task Announce(IServiceProvider sp, Product p, CancellationToken ct)
    {
        try { await LinkOut.SendAsync(sp, "product.changed", View(p), ct); }
        catch (Exception e) when (e is NpgsqlException or HttpRequestException or InvalidOperationException) { /* kept for a later attempt or not set up */ }
    }

    public static IResult Result((string Error, string Detail, int Status)? r, object ok) =>
        r is { } x ? Results.Json(new { error = x.Error, detail = x.Detail }, statusCode: x.Status) : Results.Ok(ok);
}
