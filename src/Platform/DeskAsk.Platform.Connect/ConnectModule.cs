using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.Connect;

/// <summary>Schema "connect" in the customer database: the agents of this customer's sites.</summary>
public sealed class ConnectSchema : ITenantSchema
{
    public string Name => "connect";
    public async Task EnsureAsync(IServiceProvider services, TenantInfo tenant, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(tenant.ConnectionString);
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            select pg_advisory_lock(727003);
            create schema if not exists connect;
            create table if not exists connect.agents (
              id text primary key, name text not null, token_hash text not null unique, created_at timestamptz not null default now(),
              last_seen timestamptz, version text not null default '', plugins text not null default '[]', last_ip text not null default '',
              reported_name text not null default '', revoked boolean not null default false);
            select pg_advisory_unlock(727003);", db);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// The agent gateway (ADR 0005). An administrator creates an agent and gets its token once; only the SHA-256 of the token is kept.
/// The agent calls POST /api/connect/heartbeat with "Authorization: Bearer dka_...". The customer is the address it calls, so a token
/// of one customer is simply unknown at another customer.
/// </summary>
public sealed class ConnectModule : IPlatformModule
{
    public string Key => "connect";
    public string Schema => "connect";
    public const string TokenPrefix = "dka_";

    public void AddServices(IServiceCollection s, IConfiguration cfg) => s.AddSingleton<ITenantSchema, ConnectSchema>();

    public static string NewToken() => TokenPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    sealed record HeartbeatIn(string? AgentId, string? Version, string[]? Plugins);

    public void MapEndpoints(IEndpointRouteBuilder r)
    {
        r.MapPost("/api/connect/agents", async (HttpRequest req, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            JsonElement body;
            try { body = await req.ReadFromJsonAsync<JsonElement>(ct); } catch (JsonException) { return Results.BadRequest(new { error = "bad_json" }); }
            var name = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("name", out var n) ? (n.GetString() ?? "").Trim() : "";
            if (name.Length is < 1 or > 100) return Results.BadRequest(new { error = "name_needed" });
            var id = Guid.NewGuid().ToString("N"); var token = NewToken();
            await using var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("insert into connect.agents (id, name, token_hash) values (@i,@n,@h)", db);
            cmd.Parameters.AddWithValue("i", id); cmd.Parameters.AddWithValue("n", name); cmd.Parameters.AddWithValue("h", Hash(token));
            await cmd.ExecuteNonQueryAsync(ct);
            await audit.WriteAsync("connect.agent_created", id, name, ct: ct);
            return Results.Ok(new { id, name, token, note = "Copy the token now. It is not shown again." });
        }).RequireAuthorization(p => p.RequireRole("admin"));

        r.MapGet("/api/connect/agents", async (ITenantContext t, CancellationToken ct) =>
        {
            await using var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(@"select id, name, version, plugins, last_seen, created_at, revoked, last_ip,
                coalesce(last_seen > now() - interval '2 minutes', false) from connect.agents order by created_at", db);
            var rows = new List<object>();
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                rows.Add(new
                {
                    id = rd.GetString(0), name = rd.GetString(1), version = rd.GetString(2),
                    plugins = JsonDocument.Parse(rd.GetString(3)).RootElement.Clone(),
                    lastSeen = rd.IsDBNull(4) ? (DateTime?)null : rd.GetDateTime(4), createdAt = rd.GetDateTime(5),
                    revoked = rd.GetBoolean(6), lastIp = rd.GetString(7), online = !rd.GetBoolean(6) && rd.GetBoolean(8),
                });
            return Results.Ok(rows);
        }).RequireAuthorization(p => p.RequireRole("admin"));

        r.MapDelete("/api/connect/agents/{id}", async (string id, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("update connect.agents set revoked = true where id = @i and not revoked", db);
            cmd.Parameters.AddWithValue("i", id);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await audit.WriteAsync("connect.agent_revoked", id, ct: ct);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(p => p.RequireRole("admin"));

        r.MapPost("/api/connect/heartbeat", async (HttpRequest req, ITenantContext t, ISettings settings, CancellationToken ct) =>
        {
            var auth = req.Headers.Authorization.ToString();
            if (!auth.StartsWith("Bearer " + TokenPrefix, StringComparison.Ordinal)) return Results.Unauthorized();
            if ((req.ContentLength ?? 0) > 16 * 1024) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            HeartbeatIn? hb = null;
            try { hb = await req.ReadFromJsonAsync<HeartbeatIn>(new JsonSerializerOptions(JsonSerializerDefaults.Web), ct); } catch (JsonException) { return Results.BadRequest(new { error = "bad_json" }); }
            var plugins = JsonSerializer.Serialize((hb?.Plugins ?? Array.Empty<string>()).Take(50).Select(x => x.Length > 100 ? x[..100] : x));
            await using var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(@"update connect.agents set last_seen = now(), version = @v, plugins = @p, last_ip = @ip, reported_name = @rn
                where token_hash = @h and not revoked returning id", db);
            cmd.Parameters.AddWithValue("v", Cut(hb?.Version, 50)); cmd.Parameters.AddWithValue("p", plugins);
            cmd.Parameters.AddWithValue("ip", req.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "");
            cmd.Parameters.AddWithValue("rn", Cut(hb?.AgentId, 100)); cmd.Parameters.AddWithValue("h", Hash(auth["Bearer ".Length..]));
            if (await cmd.ExecuteScalarAsync(ct) == null) return Results.Unauthorized();
            var interval = 30;
            var raw = await settings.GetAsync("connect.interval", ct);
            if (raw != null && int.TryParse(raw, out var i)) interval = Math.Clamp(i, 10, 600);
            return Results.Ok(new { ok = true, intervalSeconds = interval, serverTime = DateTime.UtcNow, commands = Array.Empty<object>() });
        }).AllowAnonymous();
    }

    static string Cut(string? s, int max) => s == null ? "" : s.Length > max ? s[..max] : s;
}
