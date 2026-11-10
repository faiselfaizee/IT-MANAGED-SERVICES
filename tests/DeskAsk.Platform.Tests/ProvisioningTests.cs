using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using DeskAsk.Platform;
using DeskAsk.Platform.Tenancy;
using Npgsql;
using Xunit;

public class MigratorTests
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";

    [PgFact]
    public async Task AppliesInOrderOnceAndRefusesAChangedStep()
    {
        var m = "test" + Guid.NewGuid().ToString("N")[..8];
        var steps = new[] { new Migration(2, "second", "create table if not exists public." + m + "_b (x int)"), new Migration(1, "first", "create table if not exists public." + m + "_a (x int)") };
        Assert.Equal(2, await SchemaMigrator.RunAsync(Pg, m, steps, default));
        Assert.Equal(0, await SchemaMigrator.RunAsync(Pg, m, steps, default));   // nothing to do the second time
        var edited = new[] { steps[1] with { Sql = "create table if not exists public." + m + "_z (x int)" }, steps[0] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaMigrator.RunAsync(Pg, m, edited, default));
        var more = steps.Append(new Migration(3, "third", "create table if not exists public." + m + "_c (x int)")).ToArray();
        Assert.Equal(1, await SchemaMigrator.RunAsync(Pg, m, more, default));   // only the new one
        await using var db = new NpgsqlConnection(Pg); await db.OpenAsync();
        await using (var q = new NpgsqlCommand("select count(*) from meta.migrations where module = @m", db)) { q.Parameters.AddWithValue("m", m); Assert.Equal(3L, await q.ExecuteScalarAsync()); }
        await using var drop = new NpgsqlCommand($"drop table public.{m}_a, public.{m}_b, public.{m}_c; delete from meta.migrations where module = '{m}'", db);
        await drop.ExecuteNonQueryAsync();
    }

    [PgFact]
    public async Task AFailingStepLeavesNothingBehind()
    {
        var m = "test" + Guid.NewGuid().ToString("N")[..8];
        var steps = new[] { new Migration(1, "bad", "create table public." + m + "_a (x int); select 1/0") };
        await Assert.ThrowsAnyAsync<Exception>(() => SchemaMigrator.RunAsync(Pg, m, steps, default));
        await using var db = new NpgsqlConnection(Pg); await db.OpenAsync();
        await using var q = new NpgsqlCommand($"select to_regclass('public.{m}_a') is null", db);
        Assert.True((bool)(await q.ExecuteScalarAsync())!);   // the table was rolled back with the step
    }
}

public class ProvisioningTests
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";

    static async Task<(int Code, string Out)> Cli(params string[] a)
    {
        var sw = new StringWriter();
        var b = WebApplication.CreateBuilder(new[] { "--Platform:CatalogConnection=" + Pg, "--Platform:Roles=web", "--Identity:AllowInsecureHttp=true" });
        var code = await TenantCli.RunAsync(a, b, sw);
        return (code, sw.ToString());
    }

    static async Task<T> Scalar<T>(string conn, string sql)
    {
        await using var db = new NpgsqlConnection(conn); await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db); return (T)(await cmd.ExecuteScalarAsync())!;
    }

    [PgFact]
    public async Task CreateListDisableEnableMigrate()
    {
        var id = "t" + Guid.NewGuid().ToString("N")[..8]; var host = id + ".test.local"; var dbName = "deskask_" + id;
        var tenantConn = new NpgsqlConnectionStringBuilder(Pg) { Database = dbName }.ConnectionString;
        try
        {
            var c = await Cli("create", "--id", id, "--host", host, "--admin-email", "boss@" + id + ".test", "--name", "Test " + id);
            Assert.True(c.Code == 0, c.Out);
            var password = Regex.Match(c.Out, "Password: (\\S+)").Groups[1].Value;
            Assert.True(password.Length >= 16, c.Out);

            Assert.Equal(1L, await Scalar<long>(Pg, $"select count(*) from pg_database where datname = '{dbName}'"));
            Assert.True(await Scalar<bool>(Pg, $"select active from platform.tenants where id = '{id}'"));
            Assert.Equal(1L, await Scalar<long>(tenantConn, "select count(*) from identity.users where \"Role\" = 'admin'"));
            Assert.NotEqual(password, await Scalar<string>(tenantConn, "select \"PasswordHash\" from identity.users limit 1"));   // never stored as typed
            Assert.True(await Scalar<long>(tenantConn, "select count(*) from meta.migrations") >= 6);   // core, connect, jobs, identity x3
            Assert.Equal(1L, await Scalar<long>(tenantConn, "select count(*) from information_schema.tables where table_schema = 'core' and table_name = 'audit'"));

            // the web app can sign in at this customer's own address
            Assert.Equal(1L, await Scalar<long>(tenantConn, $"select count(*) from identity.\"OpenIddictApplications\" where \"ClientId\" = 'web' and \"RedirectUris\" like '%{host}/auth/callback%'"));

            var oldHash = await Scalar<string>(tenantConn, "select \"PasswordHash\" from identity.users limit 1");
            var reset = await Cli("reset-admin", "--id", id);
            Assert.True(reset.Code == 0, reset.Out);
            Assert.True(Regex.Match(reset.Out, "Password: (\\S+)").Groups[1].Value.Length >= 16, reset.Out);
            Assert.NotEqual(oldHash, await Scalar<string>(tenantConn, "select \"PasswordHash\" from identity.users limit 1"));
            Assert.NotEqual(0, (await Cli("reset-admin", "--id", "nobody" + id)).Code);

            var again = await Cli("create", "--id", id, "--host", host, "--admin-email", "boss@" + id + ".test");
            Assert.NotEqual(0, again.Code);
            Assert.Contains("already exists", again.Out);

            Assert.Contains(id, (await Cli("list")).Out);

            var catalog = new PgTenantCatalog(Pg);
            Assert.Equal(id, (await catalog.FindByHostAsync(host))?.Id);
            Assert.Equal(0, (await Cli("disable", "--id", id)).Code);
            Assert.Null(await catalog.FindByHostAsync(host));
            Assert.Equal(0, (await Cli("enable", "--id", id)).Code);
            Assert.Equal(id, (await catalog.FindByHostAsync(host))?.Id);

            var mig = await Cli("migrate");
            Assert.True(mig.Code == 0, mig.Out);
            Assert.Contains("ok      " + id, mig.Out);
        }
        finally
        {
            await using var db = new NpgsqlConnection(Pg); await db.OpenAsync();
            await using var cmd = new NpgsqlCommand($"delete from platform.tenants where id = '{id}'; drop database if exists \"{dbName}\" with (force)", db);
            try { await cmd.ExecuteNonQueryAsync(); } catch { }
        }
    }

    [PgFact]
    public async Task BadInputIsRefusedBeforeAnythingIsMade()
    {
        var r = await Cli("create", "--id", "Bad Id!", "--host", "x.test.local", "--admin-email", "a@b.co");
        Assert.Equal(2, r.Code);
        var r2 = await Cli("create", "--id", "okid1", "--host", "x.test.local");
        Assert.Equal(2, r2.Code);
        Assert.Contains("--admin-email", r2.Out);
    }
}
