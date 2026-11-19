using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.Commerce;

/// <summary>
/// The addresses on-prem installations call (no sign-in; rate limited per address). They answer only on the vendor platform:
///   POST /api/store/activate  {key, installation, tenant, host, version} -> {token, activation, secret}
///   POST /api/store/licence   {activation, secret}                       -> {token}   the newest licence (issued again when the subscription changed)
///   POST /api/store/release   {activation, secret}                       -> frees the activation for another installation
/// A key is stored only as a hash; the activation secret too. Nothing about the customer's own data is sent or kept.
/// </summary>
public static class StoreApi
{
    static IResult No(string error, string detail, int status) => Results.Json(new { error, detail }, statusCode: status);
    static string Str(JsonElement b, string name) =>
        b.ValueKind == JsonValueKind.Object && b.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
    static readonly Regex InstallationId = new("^[a-f0-9]{16,64}$"), TenantId = new("^[a-z][a-z0-9-]{1,30}$");

    static RouteHandlerBuilder Public(RouteHandlerBuilder b) => b.AllowAnonymous().AddEndpointFilter(async (ctx, next) =>
    {
        var sp = ctx.HttpContext.RequestServices;
        return sp.GetRequiredService<CommerceOptions>().For(sp.GetRequiredService<ITenantContext>()) ? await next(ctx) : Results.NotFound(new { error = "not_found" });
    });

    /// <summary>The activation behind an id and secret, with its key and subscription. Null with the answer to give when it does not hold.</summary>
    static async Task<((Guid Id, string Installation, string Tenant, Guid KeyId, Sub Sub)? Found, IResult? Refusal)> FindAsync(NpgsqlConnection db, JsonElement b, CancellationToken ct)
    {
        if (!Guid.TryParse(Str(b, "activation"), out var id)) return (null, No("activation_unknown", "This activation is not known. Activate again with the licence key.", 404));
        var secret = Str(b, "secret");
        Guid keyId, subId; string installation, tenant; bool released, revoked; byte[] hash;
        await using (var q = new NpgsqlCommand(@"select a.key_id, a.installation, a.tenant_id, a.secret_hash, a.released_at is not null, k.revoked, k.subscription_id
                from commerce.activations a join commerce.licence_keys k on k.id = a.key_id where a.id = @i", db))
        {
            q.Parameters.AddWithValue("i", id);
            await using var rd = await q.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return (null, No("activation_unknown", "This activation is not known. Activate again with the licence key.", 404));
            (keyId, installation, tenant, hash, released, revoked, subId) = (rd.GetGuid(0), rd.GetString(1), rd.GetString(2), (byte[])rd[3], rd.GetBoolean(4), rd.GetBoolean(5), rd.GetGuid(6));
        }
        if (!CryptographicOperations.FixedTimeEquals(hash, Licences.Hash(secret))) return (null, No("activation_unknown", "This activation is not known. Activate again with the licence key.", 404));
        if (released) return (null, No("activation_released", "This installation was released. Activate it again with the licence key.", 410));
        if (revoked) return (null, No("key_revoked", "The licence key of this installation was withdrawn. Contact your supplier.", 410));
        var sub = await Licences.GetSubAsync(db, subId, ct);
        if (sub == null) return (null, No("activation_unknown", "This activation is not known.", 404));
        return ((id, installation, tenant, keyId, sub), null);
    }

    public static void Map(IEndpointRouteBuilder r)
    {
        Public(r.MapPost("/api/store/activate", async (JsonElement b, CommerceOptions o, Signer signer, CancellationToken ct) =>
        {
            var key = Licences.NormalizeKey(Str(b, "key"));
            var installation = Str(b, "installation").ToLowerInvariant();
            var tenant = Str(b, "tenant").ToLowerInvariant();
            var host = Str(b, "host"); var version = Str(b, "version");
            if (key.Length != 25) return No("key_unknown", "This licence key is not known. Check it and try again.", 404);
            if (!InstallationId.IsMatch(installation) || !TenantId.IsMatch(tenant)) return No("bad_request", "The installation id or customer id is missing.", 400);
            if (host.Length > 255) host = host[..255];
            if (version.Length > 40) version = version[..40];

            var (signKey, _) = await signer.LoadAsync(ct);
            using (signKey)
            {
                if (signKey == null || !signer.Matches(signKey)) return No("unavailable", "Licences cannot be issued right now. Try again later or contact your supplier.", 503);
                await using var db = await CommerceDb.OpenAsync(o, ct);
                await using var tx = await db.BeginTransactionAsync(ct);
                Guid keyId, subId; int max; bool revoked;
                await using (var q = new NpgsqlCommand("select id, subscription_id, max_activations, revoked from commerce.licence_keys where key_hash = @h for update", db, tx))
                {
                    q.Parameters.AddWithValue("h", Licences.Hash(key));
                    await using var rd = await q.ExecuteReaderAsync(ct);
                    if (!await rd.ReadAsync(ct)) return No("key_unknown", "This licence key is not known. Check it and try again.", 404);
                    (keyId, subId, max, revoked) = (rd.GetGuid(0), rd.GetGuid(1), rd.GetInt32(2), rd.GetBoolean(3));
                }
                if (revoked) return No("key_revoked", "This licence key was withdrawn. Contact your supplier.", 410);
                var sub = (await Licences.GetSubAsync(db, subId, ct, tx))!;
                if (sub.Kind != "onprem") return No("key_unknown", "This licence key is not known. Check it and try again.", 404);
                if (sub.State == "ended" || sub.EndsAt.AddDays(sub.GraceDays) < DateTime.UtcNow) return No("subscription_ended", "The subscription behind this key has ended. Contact your supplier to renew it.", 402);
                if (sub.TenantId != "" && sub.TenantId != tenant)
                    return No("wrong_customer", "This key is for customer id '" + sub.TenantId + "', but this installation is '" + tenant + "'.", 409);
                if (sub.TenantId == "")
                {
                    await using var set = new NpgsqlCommand("update commerce.subscriptions set tenant_id = @t, updated_at = now() where id = @i", db, tx);
                    set.Parameters.AddWithValue("t", tenant); set.Parameters.AddWithValue("i", sub.Id);
                    await set.ExecuteNonQueryAsync(ct);
                    sub = sub with { TenantId = tenant };
                }

                var secret = Licences.Secret();
                Guid activationId;
                // the same installation activating again (settings lost, reinstalled): keep its place, give it a new secret
                await using (var again = new NpgsqlCommand("update commerce.activations set secret_hash = @h, host = @host, version = @v, tenant_id = @t, last_check = now() where key_id = @k and installation = @i and released_at is null returning id", db, tx))
                {
                    again.Parameters.AddWithValue("h", Licences.Hash(secret)); again.Parameters.AddWithValue("host", host); again.Parameters.AddWithValue("v", version);
                    again.Parameters.AddWithValue("t", tenant); again.Parameters.AddWithValue("k", keyId); again.Parameters.AddWithValue("i", installation);
                    activationId = await again.ExecuteScalarAsync(ct) is Guid g ? g : Guid.Empty;
                }
                if (activationId == Guid.Empty)
                {
                    await using (var count = new NpgsqlCommand("select count(*) from commerce.activations where key_id = @k and released_at is null", db, tx))
                    {
                        count.Parameters.AddWithValue("k", keyId);
                        if ((long)(await count.ExecuteScalarAsync(ct))! >= max)
                            return No("activations_used", "This key is already in use on " + (max == 1 ? "another installation" : max + " installations") + ". Release it there first (Licence page, Release), or ask your supplier for another key.", 409);
                    }
                    await using var ins = new NpgsqlCommand("insert into commerce.activations (key_id, installation, tenant_id, host, version, secret_hash, last_check) values (@k, @i, @t, @host, @v, @h, now()) returning id", db, tx);
                    ins.Parameters.AddWithValue("k", keyId); ins.Parameters.AddWithValue("i", installation); ins.Parameters.AddWithValue("t", tenant);
                    ins.Parameters.AddWithValue("host", host); ins.Parameters.AddWithValue("v", version); ins.Parameters.AddWithValue("h", Licences.Hash(secret));
                    activationId = (Guid)(await ins.ExecuteScalarAsync(ct))!;
                }
                var (_, token) = await Licences.IssueAsync(db, tx, signKey, sub, tenant, installation, "activation", "activated", ct);
                await tx.CommitAsync(ct);
                return Results.Ok(new { token, activation = activationId, secret });
            }
        }));

        Public(r.MapPost("/api/store/licence", async (JsonElement b, CommerceOptions o, Signer signer, CancellationToken ct) =>
        {
            await using var db = await CommerceDb.OpenAsync(o, ct);
            var (found, refusal) = await FindAsync(db, b, ct);
            if (found is not { } a) return refusal!;
            await using (var seen = new NpgsqlCommand("update commerce.activations set last_check = now() where id = @i", db))
            { seen.Parameters.AddWithValue("i", a.Id); await seen.ExecuteNonQueryAsync(ct); }
            // the last licence of this installation, unless the subscription changed after it was issued
            await using (var q = new NpgsqlCommand("select token, issued_at from commerce.licences where subscription_id = @s and installation = @i order by issued_at desc limit 1", db))
            {
                q.Parameters.AddWithValue("s", a.Sub.Id); q.Parameters.AddWithValue("i", a.Installation);
                await using var rd = await q.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct) && rd.GetDateTime(1) >= a.Sub.UpdatedAt) return Results.Ok(new { token = rd.GetString(0) });
            }
            var (signKey, _) = await signer.LoadAsync(ct);
            using (signKey)
            {
                if (signKey == null || !signer.Matches(signKey)) return No("unavailable", "Licences cannot be issued right now. Try again later.", 503);
                var (_, token) = await Licences.IssueAsync(db, null, signKey, a.Sub, a.Tenant, a.Installation, "activation", "daily check after a change", ct);
                return Results.Ok(new { token });
            }
        }));

        Public(r.MapPost("/api/store/release", async (JsonElement b, CommerceOptions o, CancellationToken ct) =>
        {
            await using var db = await CommerceDb.OpenAsync(o, ct);
            var (found, refusal) = await FindAsync(db, b, ct);
            if (found is not { } a) return refusal!;
            await using var up = new NpgsqlCommand("update commerce.activations set released_at = now() where id = @i", db);
            up.Parameters.AddWithValue("i", a.Id);
            await up.ExecuteNonQueryAsync(ct);
            return Results.Ok(new { released = true });
        }));
    }
}
