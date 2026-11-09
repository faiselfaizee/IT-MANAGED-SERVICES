using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace DeskAsk.Platform.Identity;

/// <summary>
/// Every customer signs its tokens with its own ES256 key (ADR 0006, ADR 0011). The private key is kept in the customer's own database
/// (identity.keys), sealed with AES-GCM under a key made from Identity:EncryptionSecret. The key that protects refresh tokens and
/// authorization codes is made from the same secret and the customer id. So a token or key of one customer means nothing to another.
/// A retired key stays in the published list for two days so tokens signed just before a rotation still verify.
/// </summary>
public sealed class TenantKeys
{
    public sealed record KeySet(IReadOnlyList<SigningCredentials> Signing, EncryptingCredentials Encryption, string[] Kids);

    static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);   // other processes see a rotation after at most this long
    readonly string secret;
    readonly ConcurrentDictionary<string, (DateTime At, KeySet Set)> cache = new();
    readonly ConcurrentDictionary<string, object> locks = new();

    public TenantKeys(IConfiguration cfg)
    {
        secret = cfg["Identity:EncryptionSecret"] ?? "development-only-secret";
        Shared = !string.IsNullOrWhiteSpace(cfg["Identity:SigningKeyPem"]);
    }

    /// <summary>True when one fixed key from Identity:SigningKeyPem is used for every customer (the older setup).</summary>
    public bool Shared { get; }

    public KeySet Get(TenantInfo t)
    {
        if (cache.TryGetValue(t.Id, out var e) && DateTime.UtcNow - e.At < Ttl) return e.Set;
        lock (locks.GetOrAdd(t.Id, _ => new object()))
        {
            if (cache.TryGetValue(t.Id, out e) && DateTime.UtcNow - e.At < Ttl) return e.Set;
            var set = Load(t);
            if (e.Set != null && e.Set.Kids.SequenceEqual(set.Kids)) set = e.Set;   // unchanged: keep the same object so options are not rebuilt
            cache[t.Id] = (DateTime.UtcNow, set);
            return set;
        }
    }

    /// <summary>Makes a new key the signing key. The old one stays valid for verifying for two days.</summary>
    public void Rotate(TenantInfo t)
    {
        lock (locks.GetOrAdd(t.Id, _ => new object()))
        {
            using var db = new NpgsqlConnection(t.ConnectionString); db.Open();
            using var tx = db.BeginTransaction();
            using (var retire = new NpgsqlCommand("update identity.keys set retired_at = now() where tenant_id = @t and retired_at is null", db, tx))
            { retire.Parameters.AddWithValue("t", t.Id); retire.ExecuteNonQuery(); }
            Insert(db, tx, t.Id);
            tx.Commit();
            cache.TryRemove(t.Id, out _);
        }
    }

    KeySet Load(TenantInfo t)
    {
        using var db = new NpgsqlConnection(t.ConnectionString); db.Open();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var rows = new List<(string Kid, string Sealed)>();
            using (var cmd = new NpgsqlCommand(@"select kid, private_sealed from identity.keys
                where tenant_id = @t and (retired_at is null or retired_at > now() - interval '2 days')
                order by (retired_at is null) desc, created_at desc, id desc", db))
            {
                cmd.Parameters.AddWithValue("t", t.Id);
                using var rd = cmd.ExecuteReader();
                while (rd.Read()) rows.Add((rd.GetString(0), rd.GetString(1)));
            }
            if (rows.Count == 0) { Insert(db, null, t.Id); continue; }   // first use: make the customer's key (if two processes race, the unique index lets one win)
            var signing = new List<SigningCredentials>();
            foreach (var r in rows)
            {
                var ec = ECDsa.Create(); ec.ImportFromPem(Open(r.Sealed));
                signing.Add(new SigningCredentials(new ECDsaSecurityKey(ec) { KeyId = r.Kid }, SecurityAlgorithms.EcdsaSha256));
            }
            var encKey = HMACSHA256.HashData(SHA256.HashData(Encoding.UTF8.GetBytes(secret)), Encoding.UTF8.GetBytes("deskask-token-encryption|" + t.Id));
            var enc = new EncryptingCredentials(new SymmetricSecurityKey(encKey), SecurityAlgorithms.Aes256KW, SecurityAlgorithms.Aes256CbcHmacSha512);
            return new KeySet(signing, enc, rows.Select(r => r.Kid).ToArray());
        }
        throw new InvalidOperationException("The signing key of customer '" + t.Id + "' could not be made.");
    }

    void Insert(NpgsqlConnection db, NpgsqlTransaction? tx, string tenantId)
    {
        var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var kid = Convert.ToHexString(SHA256.HashData(ec.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant();
        using var cmd = new NpgsqlCommand("insert into identity.keys (tenant_id, kid, private_sealed) values (@t, @k, @p) on conflict do nothing", db, tx);
        cmd.Parameters.AddWithValue("t", tenantId); cmd.Parameters.AddWithValue("k", kid); cmd.Parameters.AddWithValue("p", Seal(ec.ExportPkcs8PrivateKeyPem()));
        cmd.ExecuteNonQuery();
    }

    byte[] SealKey => SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    string Seal(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12); var data = Encoding.UTF8.GetBytes(plain); var ct = new byte[data.Length]; var tag = new byte[16];
        using var g = new AesGcm(SealKey, 16); g.Encrypt(nonce, data, ct, tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(ct).ToArray());
    }
    string Open(string sealedText)
    {
        var all = Convert.FromBase64String(sealedText); var pt = new byte[all.Length - 28];
        using var g = new AesGcm(SealKey, 16); g.Decrypt(all[..12], all[28..], all[12..28], pt);
        return Encoding.UTF8.GetString(pt);
    }
}

/// <summary>
/// OpenIddict reads its options for every request. This hands it a copy per customer, with that customer's keys, so one process
/// serves many customers, each with its own signing and encryption keys. Without a customer (start-up) it gives the plain options.
/// </summary>
public sealed class TenantOptionsMonitor<T> : IOptionsMonitor<T> where T : class
{
    readonly IOptionsFactory<T> factory; readonly IHttpContextAccessor http; readonly TenantKeys keys; readonly Action<T, TenantKeys.KeySet>? apply;
    readonly object gate = new();
    T? plain;
    readonly ConcurrentDictionary<string, (TenantKeys.KeySet Keys, T Value)> perTenant = new();

    public TenantOptionsMonitor(IOptionsFactory<T> factory, IHttpContextAccessor http, TenantKeys keys, Action<T, TenantKeys.KeySet>? apply)
    { this.factory = factory; this.http = http; this.keys = keys; this.apply = apply; }

    public T CurrentValue
    {
        get
        {
            var tenant = http.HttpContext?.RequestServices.GetService<ITenantContext>()?.Current;
            if (tenant == null || tenant.ConnectionString == "") { lock (gate) return plain ??= factory.Create(Options.DefaultName); }
            var ks = keys.Get(tenant);
            if (perTenant.TryGetValue(tenant.Id, out var e) && ReferenceEquals(e.Keys, ks)) return e.Value;
            lock (gate)
            {
                if (perTenant.TryGetValue(tenant.Id, out e) && ReferenceEquals(e.Keys, ks)) return e.Value;
                var v = factory.Create(Options.DefaultName);
                apply?.Invoke(v, ks);
                perTenant[tenant.Id] = (ks, v);
                return v;
            }
        }
    }

    public T Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
