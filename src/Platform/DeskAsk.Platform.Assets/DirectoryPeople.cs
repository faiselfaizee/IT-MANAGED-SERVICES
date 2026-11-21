using Npgsql;

namespace DeskAsk.Platform.Assets;

/// <summary>Keeps the people read from a customer's domain controller. The AD Manager module reads them; this module stores them so Assets and Monitoring customers can use them without AD Manager.</summary>
public sealed class DirectoryPeople : IDirectoryPeople
{
    readonly ITenantContext tenant;
    public DirectoryPeople(ITenantContext t) => tenant = t;

    public async Task ReplaceAsync(int companyId, IReadOnlyList<DirectoryPerson> people, CancellationToken ct = default)
    {
        await using var db = new NpgsqlConnection(tenant.Require().ConnectionString); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var keys = new List<string>();
        foreach (var p in people.Where(x => x.Key != "" && x.Name != "").GroupBy(x => x.Key.ToLowerInvariant()).Select(g => g.First()))
        {
            keys.Add(p.Key.ToLowerInvariant());
            await using var c = new NpgsqlCommand(@"insert into assets.directory_people (company_id, account, name, email, upn, department, title, enabled, synced_at)
                values (@co, @a, @n, @e, @u, @d, @t, @en, now())
                on conflict (company_id, account) do update set name = @n, email = @e, upn = @u, department = @d, title = @t, enabled = @en, synced_at = now()", db, tx);
            c.Parameters.AddWithValue("co", companyId); c.Parameters.AddWithValue("a", p.Key.ToLowerInvariant()); c.Parameters.AddWithValue("n", Clip(p.Name, 200));
            c.Parameters.AddWithValue("e", Clip(p.Email, 200)); c.Parameters.AddWithValue("u", Clip(p.Upn, 200)); c.Parameters.AddWithValue("d", Clip(p.Department, 200));
            c.Parameters.AddWithValue("t", Clip(p.Title, 200)); c.Parameters.AddWithValue("en", p.Enabled);
            await c.ExecuteNonQueryAsync(ct);
        }
        await using (var del = new NpgsqlCommand("delete from assets.directory_people where company_id = @co and not (account = any(@keys))", db, tx))
        { del.Parameters.AddWithValue("co", companyId); del.Parameters.AddWithValue("keys", keys.ToArray()); await del.ExecuteNonQueryAsync(ct); }
        await tx.CommitAsync(ct);
    }

    static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}
