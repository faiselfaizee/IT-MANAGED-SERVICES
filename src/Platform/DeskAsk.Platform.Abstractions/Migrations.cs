using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace DeskAsk.Platform;

/// <summary>One step of a module's database. Give Sql, or Run for steps that need code. A step is never edited after it has shipped: add the next version.</summary>
public sealed record Migration(int Version, string Name, string? Sql = null, Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task>? Run = null);

/// <summary>
/// Applies a module's migrations to one database, in version order, each in its own transaction, and records them in meta.migrations.
/// One lock for the whole database, so two servers starting together cannot collide. A step that was changed after it was applied is refused.
/// Databases made before migrations existed are brought in by version 1 of each module, which only creates what is missing.
/// </summary>
public static class SchemaMigrator
{
    const long LockKey = 727000;

    public static async Task<int> RunAsync(string connectionString, string module, IReadOnlyList<Migration> migrations, CancellationToken ct)
    {
        if (migrations.Select(m => m.Version).Distinct().Count() != migrations.Count) throw new ArgumentException("Two migrations of '" + module + "' have the same version.");
        await using var db = new NpgsqlConnection(connectionString);
        await db.OpenAsync(ct);
        await Exec(db, null, "select pg_advisory_lock(" + LockKey + ")", ct);
        try
        {
            await Exec(db, null, @"create schema if not exists meta;
                create table if not exists meta.migrations (module text not null, version int not null, name text not null, checksum text not null,
                  applied_at timestamptz not null default now(), primary key (module, version))", ct);
            var applied = new Dictionary<int, string>();
            await using (var q = new NpgsqlCommand("select version, checksum from meta.migrations where module = @m", db))
            {
                q.Parameters.AddWithValue("m", module);
                await using var rd = await q.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) applied[rd.GetInt32(0)] = rd.GetString(1);
            }
            var ran = 0;
            foreach (var m in migrations.OrderBy(x => x.Version))
            {
                var sum = Checksum(m);
                if (applied.TryGetValue(m.Version, out var old))
                {
                    if (old != sum) throw new InvalidOperationException($"Migration {module} v{m.Version} ({m.Name}) was changed after it was applied. Add a new version instead.");
                    continue;
                }
                await using var tx = await db.BeginTransactionAsync(ct);
                if (m.Sql != null) await Exec(db, tx, m.Sql, ct);
                if (m.Run != null) await m.Run(db, tx, ct);
                await using (var ins = new NpgsqlCommand("insert into meta.migrations (module, version, name, checksum) values (@m, @v, @n, @c)", db, tx))
                {
                    ins.Parameters.AddWithValue("m", module); ins.Parameters.AddWithValue("v", m.Version); ins.Parameters.AddWithValue("n", m.Name); ins.Parameters.AddWithValue("c", sum);
                    await ins.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                ran++;
            }
            return ran;
        }
        finally { await Exec(db, null, "select pg_advisory_unlock(" + LockKey + ")", CancellationToken.None); }
    }

    static string Checksum(Migration m) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(m.Sql ?? "code:" + m.Name))).ToLowerInvariant();

    static async Task Exec(NpgsqlConnection db, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, db, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
