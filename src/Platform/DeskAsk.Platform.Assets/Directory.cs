using Npgsql;

namespace DeskAsk.Platform.Assets;

/// <summary>What the Assets module shows other modules: its companies and assets, read only.</summary>
public sealed class AssetsDirectory : ICompanyDirectory, IAssetDirectory
{
    readonly ITenantContext tenant;
    readonly IEntitlements entitlements;
    public AssetsDirectory(ITenantContext t, IEntitlements ent) { tenant = t; entitlements = ent; }

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
        // single-company mode: the customer's own name is the one company, made the first time anything asks for it
        if ((await entitlements.CurrentAsync(ct)).SingleCompany)
        {
            await using var one = new NpgsqlCommand("insert into assets.companies (name) select @n where not exists (select 1 from assets.companies) on conflict do nothing", db);
            one.Parameters.AddWithValue("n", tenant.Require().Name.Length > 120 ? tenant.Require().Name[..120] : tenant.Require().Name);
            await one.ExecuteNonQueryAsync(ct);
        }
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

    public async Task<IReadOnlyList<AssetRef>> ListForPersonAsync(int companyId, string name, string email, bool wholeCompany, CancellationToken ct = default)
    {
        var l = new List<AssetRef>();
        try
        {
            await using var db = await Open(ct);
            await using var c = new NpgsqlCommand(@"select id, tag, name, company_id from assets.assets
                where company_id = @co and status not in ('retired','lost')
                  and (@all or (assigned_to <> '' and (lower(assigned_to) = lower(@n) or lower(assigned_to) = lower(@e))))
                order by lower(name) limit 200", db);
            c.Parameters.AddWithValue("co", companyId); c.Parameters.AddWithValue("all", wholeCompany); c.Parameters.AddWithValue("n", name ?? ""); c.Parameters.AddWithValue("e", email ?? "");
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) l.Add(new AssetRef(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetInt32(3)));
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable) { /* Asset management is not set up for this customer */ }
        return l;
    }
}
