using Npgsql;

namespace DeskAsk.Platform.Assets;

/// <summary>What the Assets module shows other modules: its companies and assets, read only.</summary>
public sealed class AssetsDirectory : ICompanyDirectory, IAssetDirectory
{
    readonly ITenantContext tenant;
    public AssetsDirectory(ITenantContext t) => tenant = t;

    async Task<NpgsqlConnection> Open(CancellationToken ct) { var db = new NpgsqlConnection(tenant.Require().ConnectionString); await db.OpenAsync(ct); return db; }

    public async Task<CompanyRef?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var c = new NpgsqlCommand("select id, name, code, status = 'active' from assets.companies where id = @i", db); c.Parameters.AddWithValue("i", id);
        await using var r = await c.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new CompanyRef(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetBoolean(3)) : null;
    }

    public async Task<IReadOnlyList<CompanyRef>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var c = new NpgsqlCommand("select id, name, code, status = 'active' from assets.companies order by lower(name)", db);
        var l = new List<CompanyRef>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) l.Add(new CompanyRef(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetBoolean(3)));
        return l;
    }

    async Task<AssetRef?> IAssetDirectory.GetAsync(long id, CancellationToken ct)
    {
        await using var db = await Open(ct);
        await using var c = new NpgsqlCommand("select id, tag, name, company_id from assets.assets where id = @i", db); c.Parameters.AddWithValue("i", id);
        await using var r = await c.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new AssetRef(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt32(3)) : null;
    }
}
