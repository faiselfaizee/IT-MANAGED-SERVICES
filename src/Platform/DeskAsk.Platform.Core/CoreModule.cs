using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace DeskAsk.Platform.Core;

/// <summary>Platform core in every customer database (schema "core"): audit log and settings.</summary>
public sealed class CoreSchema : ITenantSchema
{
    public string Name => "core";
    public async Task EnsureAsync(IServiceProvider services, TenantInfo tenant, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(tenant.ConnectionString);
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            select pg_advisory_lock(727002);
            create schema if not exists core;
            create table if not exists core.audit (
              id bigserial primary key, at timestamptz not null, actor text not null, action text not null,
              target text not null default '', detail text not null default '', ip text not null default '',
              prev_hash text not null, hash text not null);
            create index if not exists audit_action on core.audit (action);
            create table if not exists core.settings (
              key text primary key, value text not null, updated_at timestamptz not null, updated_by text not null);
            select pg_advisory_unlock(727002);", db);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public static class AuditHash
{
    public static readonly string Genesis = new('0', 64);
    /// <summary>Microsecond precision, the most PostgreSQL keeps, so a row read back hashes the same as the row written.</summary>
    public static DateTime Now() { var t = DateTime.UtcNow; return new DateTime(t.Ticks - t.Ticks % 10, DateTimeKind.Utc); }
    public static string Compute(string prev, DateTime at, string actor, string action, string target, string detail, string ip) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",
            prev, at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'"), actor, action, target, detail, ip)))).ToLowerInvariant();
}

public sealed class AuditLog : IAuditLog
{
    readonly ITenantContext tenant; readonly IHttpContextAccessor http;
    public AuditLog(ITenantContext tenant, IHttpContextAccessor http) { this.tenant = tenant; this.http = http; }

    public async Task WriteAsync(string action, string target = "", string detail = "", string? actor = null, CancellationToken ct = default)
    {
        var hc = http.HttpContext;
        actor ??= hc?.User.FindFirstValue("email") ?? hc?.User.FindFirstValue("sub") ?? "system";
        var ip = hc?.Connection.RemoteIpAddress?.ToString() ?? "";
        if (detail.Length > 4000) detail = detail[..4000];
        await using var db = new NpgsqlConnection(tenant.Require().ConnectionString);
        await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var lk = new NpgsqlCommand("select pg_advisory_xact_lock(727001)", db, tx)) await lk.ExecuteNonQueryAsync(ct);   // one writer at a time keeps the chain in order
        string prev;
        await using (var last = new NpgsqlCommand("select hash from core.audit order by id desc limit 1", db, tx))
            prev = (await last.ExecuteScalarAsync(ct)) as string ?? AuditHash.Genesis;
        var at = AuditHash.Now();
        var hash = AuditHash.Compute(prev, at, actor, action, target, detail, ip);
        await using (var ins = new NpgsqlCommand("insert into core.audit (at, actor, action, target, detail, ip, prev_hash, hash) values (@at,@actor,@action,@target,@detail,@ip,@prev,@hash)", db, tx))
        {
            ins.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = at });
            foreach (var (k, v) in new[] { ("actor", actor), ("action", action), ("target", target), ("detail", detail), ("ip", ip), ("prev", prev), ("hash", hash) })
                ins.Parameters.AddWithValue(k, v);
            await ins.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}

public sealed class Settings : ISettings
{
    readonly ITenantContext tenant;
    public Settings(ITenantContext tenant) => this.tenant = tenant;
    static readonly Regex KeyRx = new("^[a-z0-9][a-z0-9._-]{0,99}$", RegexOptions.Compiled);
    public static bool ValidKey(string key) => KeyRx.IsMatch(key);

    async Task<NpgsqlConnection> Open(CancellationToken ct) { var c = new NpgsqlConnection(tenant.Require().ConnectionString); await c.OpenAsync(ct); return c; }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var cmd = new NpgsqlCommand("select value from core.settings where key = @k", db);
        cmd.Parameters.AddWithValue("k", key);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task SetAsync(string key, string jsonValue, CancellationToken ct = default)
    {
        if (!ValidKey(key)) throw new ArgumentException("Bad settings key.", nameof(key));
        JsonDocument.Parse(jsonValue).Dispose();   // must be valid JSON
        await using var db = await Open(ct);
        await using var cmd = new NpgsqlCommand(@"insert into core.settings (key, value, updated_at, updated_by) values (@k,@v,now(),'')
            on conflict (key) do update set value = excluded.value, updated_at = now()", db);
        cmd.Parameters.AddWithValue("k", key); cmd.Parameters.AddWithValue("v", jsonValue);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var cmd = new NpgsqlCommand("delete from core.settings where key = @k", db);
        cmd.Parameters.AddWithValue("k", key);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<IReadOnlyDictionary<string, string>> ListAsync(string prefix = "", CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var cmd = new NpgsqlCommand("select key, value from core.settings where starts_with(key, @p) order by key", db);
        cmd.Parameters.AddWithValue("p", prefix);
        var d = new Dictionary<string, string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) d[r.GetString(0)] = r.GetString(1);
        return d;
    }
}

public sealed class CoreModule : IPlatformModule
{
    public string Key => "core";
    public string Schema => "core";

    public void AddServices(IServiceCollection s, IConfiguration cfg)
    {
        s.AddHttpContextAccessor();
        s.AddSingleton<ITenantSchema, CoreSchema>();
        s.AddScoped<IAuditLog, AuditLog>();
        s.AddScoped<ISettings, Settings>();
    }

    public void MapEndpoints(IEndpointRouteBuilder r)
    {
        // audit: newest first; ?take=50 (max 200) &before=<id> &action=<prefix>
        r.MapGet("/api/platform/audit", async (ITenantContext t, int? take, long? before, string? action, CancellationToken ct) =>
        {
            await using var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(@"select id, at, actor, action, target, detail, ip from core.audit
                where (@before = 0 or id < @before) and (@action = '' or starts_with(action, @action)) order by id desc limit @take", db);
            cmd.Parameters.AddWithValue("before", before ?? 0L); cmd.Parameters.AddWithValue("action", action ?? ""); cmd.Parameters.AddWithValue("take", Math.Clamp(take ?? 50, 1, 200));
            var rows = new List<object>();
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                rows.Add(new { id = rd.GetInt64(0), at = rd.GetDateTime(1), actor = rd.GetString(2), action = rd.GetString(3), target = rd.GetString(4), detail = rd.GetString(5), ip = rd.GetString(6) });
            return Results.Ok(rows);
        }).RequireAuthorization(p => p.RequireRole("admin"));

        // walks the whole chain: every row must carry the hash of the one before it and match its own content
        r.MapGet("/api/platform/audit/verify", async (ITenantContext t, CancellationToken ct) =>
        {
            await using var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("select id, at, actor, action, target, detail, ip, prev_hash, hash from core.audit order by id", db);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            var prev = AuditHash.Genesis; long count = 0;
            while (await rd.ReadAsync(ct))
            {
                var expected = AuditHash.Compute(prev, rd.GetDateTime(1), rd.GetString(2), rd.GetString(3), rd.GetString(4), rd.GetString(5), rd.GetString(6));
                if (rd.GetString(7) != prev || rd.GetString(8) != expected) return Results.Ok(new { ok = false, count, brokenAtId = rd.GetInt64(0) });
                prev = rd.GetString(8); count++;
            }
            return Results.Ok(new { ok = true, count, brokenAtId = (long?)null });
        }).RequireAuthorization(p => p.RequireRole("admin"));

        r.MapGet("/api/platform/settings", async (ISettings s, string? prefix, CancellationToken ct) =>
            Results.Ok((await s.ListAsync(prefix ?? "", ct)).ToDictionary(k => k.Key, k => JsonDocument.Parse(k.Value).RootElement)))
            .RequireAuthorization(p => p.RequireRole("admin"));

        r.MapGet("/api/platform/settings/{key}", async (string key, ISettings s, CancellationToken ct) =>
        {
            if (!Settings.ValidKey(key)) return Results.BadRequest(new { error = "bad_key" });
            var v = await s.GetAsync(key, ct);
            return v == null ? Results.NotFound() : Results.Content(v, "application/json");
        }).RequireAuthorization(p => p.RequireRole("admin"));

        r.MapPut("/api/platform/settings/{key}", async (string key, HttpRequest req, ISettings s, IAuditLog audit, CancellationToken ct) =>
        {
            if (!Settings.ValidKey(key)) return Results.BadRequest(new { error = "bad_key" });
            JsonDocument doc;
            try { doc = await JsonDocument.ParseAsync(req.Body, cancellationToken: ct); } catch (JsonException) { return Results.BadRequest(new { error = "bad_json" }); }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("value", out var v)) return Results.BadRequest(new { error = "value_missing" });
                var raw = v.GetRawText();
                if (raw.Length > 65536) return Results.BadRequest(new { error = "too_large" });
                await s.SetAsync(key, raw, ct);
                await audit.WriteAsync("settings.set", key, "size " + raw.Length, ct: ct);
                return Results.Ok(new { ok = true });
            }
        }).RequireAuthorization(p => p.RequireRole("admin"));

        r.MapDelete("/api/platform/settings/{key}", async (string key, ISettings s, IAuditLog audit, CancellationToken ct) =>
        {
            if (!Settings.ValidKey(key)) return Results.BadRequest(new { error = "bad_key" });
            var gone = await s.DeleteAsync(key, ct);
            if (gone) await audit.WriteAsync("settings.delete", key, ct: ct);
            return gone ? Results.Ok(new { ok = true }) : Results.NotFound();
        }).RequireAuthorization(p => p.RequireRole("admin"));
    }
}
