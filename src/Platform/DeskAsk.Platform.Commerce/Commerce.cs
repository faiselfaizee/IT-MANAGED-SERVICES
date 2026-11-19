using System.Security.Cryptography;
using System.Text.Json;
using DeskAsk.Platform.Licensing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.Commerce;

/// <summary>
/// Where the vendor's own business lives (docs/store.md). Only the vendor's platform installation turns it on (Commerce:Enabled=true), and only
/// its own customer (Commerce:VendorTenant, default "platform", the customer behind platform.deskask.in) sees it. Tables live in the catalog
/// database (schema commerce), or in Commerce:Connection when given, because subscriptions and licences belong to the vendor, not to a customer.
/// </summary>
public sealed class CommerceOptions
{
    public bool Enabled { get; }
    public string VendorTenant { get; }
    public string Connection { get; }
    /// <summary>The platform catalog (customers and their SaaS licences). Empty when this installation has none.</summary>
    public string Catalog { get; }

    public CommerceOptions(IConfiguration c)
    {
        Catalog = c["Platform:CatalogConnection"] is { Length: > 0 } x ? x : c["Licensing:CatalogConnection"] ?? "";
        Connection = c["Commerce:Connection"] is { Length: > 0 } y ? y : Catalog;
        Enabled = c.GetValue<bool>("Commerce:Enabled") && Connection != "";
        VendorTenant = c["Commerce:VendorTenant"] is { Length: > 0 } v ? v : "platform";
    }

    /// <summary>True for requests to the vendor's own customer on an installation where the store is turned on.</summary>
    public bool For(ITenantContext t) => Enabled && t.Current?.Id == VendorTenant;
    public bool ForId(string tenantId) => Enabled && tenantId == VendorTenant;
}

/// <summary>The commerce schema. Made the first time it is needed (not per customer: there is only one).</summary>
public static class CommerceDb
{
    static readonly SemaphoreSlim Gate = new(1, 1);
    static readonly HashSet<string> Ready = new();

    public static async Task<NpgsqlConnection> OpenAsync(CommerceOptions o, CancellationToken ct)
    {
        if (!Ready.Contains(o.Connection))
        {
            await Gate.WaitAsync(ct);
            try
            {
                if (!Ready.Contains(o.Connection)) { await SchemaMigrator.RunAsync(o.Connection, "commerce", Migrations, ct); Ready.Add(o.Connection); }
            }
            finally { Gate.Release(); }
        }
        var db = new NpgsqlConnection(o.Connection);
        await db.OpenAsync(ct);
        return db;
    }

    public static readonly Migration[] Migrations =
    {
        new(1, "accounts, subscriptions, licences, keys, activations, orders", @"
            create schema if not exists commerce;
            create table commerce.settings (key text primary key, value text not null, updated_at timestamptz not null default now());
            create table commerce.plans (
              id serial primary key, module text not null, size_label text not null, limits jsonb not null default '{}',
              prices jsonb not null default '{}', sort int not null default 0, active boolean not null default true, unique (module, size_label));
            create table commerce.accounts (
              id uuid primary key default gen_random_uuid(), name text not null, email text not null, country text not null default 'IN',
              gstin text not null default '', address text not null default '', notes text not null default '', created_at timestamptz not null default now());
            create table commerce.subscriptions (
              id uuid primary key default gen_random_uuid(), account_id uuid not null references commerce.accounts(id),
              kind text not null check (kind in ('saas', 'onprem')), tenant_id text not null default '', plan text not null default 'custom',
              lines jsonb not null default '{}', period text not null default 'year', starts_at timestamptz not null, ends_at timestamptz not null,
              grace_days int not null default 14, state text not null default 'active' check (state in ('active', 'cancelled', 'ended')),
              notes text not null default '', created_at timestamptz not null default now(), updated_at timestamptz not null default now());
            create index on commerce.subscriptions (tenant_id);
            create table commerce.licences (
              id bigserial primary key, subscription_id uuid references commerce.subscriptions(id), tenant_id text not null,
              installation text not null default '', token text not null, kid text not null default '', plan text not null default '',
              issued_at timestamptz not null default now(), expires_at timestamptz not null, issued_by text not null, reason text not null default '');
            create index on commerce.licences (subscription_id, installation, issued_at desc);
            create table commerce.licence_keys (
              id uuid primary key default gen_random_uuid(), subscription_id uuid not null references commerce.subscriptions(id),
              key_hash bytea not null unique, hint text not null, max_activations int not null default 1, revoked boolean not null default false,
              created_at timestamptz not null default now());
            create table commerce.activations (
              id uuid primary key default gen_random_uuid(), key_id uuid not null references commerce.licence_keys(id), installation text not null,
              tenant_id text not null, host text not null default '', version text not null default '', secret_hash bytea not null,
              activated_at timestamptz not null default now(), last_check timestamptz, released_at timestamptz);
            create table commerce.orders (
              id uuid primary key default gen_random_uuid(), account_id uuid references commerce.accounts(id), subscription_id uuid references commerce.subscriptions(id),
              kind text not null default 'saas', status text not null default 'pending', lines jsonb not null default '{}', currency text not null default 'INR',
              total numeric(12,2) not null default 0, tax numeric(12,2) not null default 0, gateway text not null default '', gateway_ref text not null default '',
              created_at timestamptz not null default now(), updated_at timestamptz not null default now());"),
        new(2, "price list", @"
            insert into commerce.plans (module, size_label, limits, prices, sort) values
            ('servicedesk', '3 agents',          '{""agents"":3}',          '{""saas"":{""usd"":{""year"":399,""month"":40},""inr"":{""year"":23000}},""onprem"":{""usd"":{""year"":320}}}', 10),
            ('servicedesk', '10 agents',         '{""agents"":10}',         '{""saas"":{""usd"":{""year"":1190,""month"":119},""inr"":{""year"":68000}},""onprem"":{""usd"":{""year"":955}}}', 11),
            ('servicedesk', '25 agents',         '{""agents"":25}',         '{""saas"":{""usd"":{""year"":2690,""month"":269},""inr"":{""year"":153500}},""onprem"":{""usd"":{""year"":2155}}}', 12),
            ('servicedesk', 'Unlimited agents',  '{}',                      '{""saas"":{""usd"":{""year"":5990,""month"":599},""inr"":{""year"":341500}},""onprem"":{""usd"":{""year"":4795}}}', 13),
            ('assets',      '100 assets',        '{""max_assets"":100}',    '{""saas"":{""usd"":{""year"":345,""month"":35},""inr"":{""year"":20000}},""onprem"":{""usd"":{""year"":280}}}', 20),
            ('assets',      '500 assets',        '{""max_assets"":500}',    '{""saas"":{""usd"":{""year"":1295,""month"":130},""inr"":{""year"":74000}},""onprem"":{""usd"":{""year"":1040}}}', 21),
            ('assets',      '1,000 assets',      '{""max_assets"":1000}',   '{""saas"":{""usd"":{""year"":2295,""month"":230},""inr"":{""year"":131000}},""onprem"":{""usd"":{""year"":1840}}}', 22),
            ('assets',      '5,000 assets',      '{""max_assets"":5000}',   '{""saas"":{""usd"":{""year"":8995,""month"":900},""inr"":{""year"":513000}},""onprem"":{""usd"":{""year"":7200}}}', 23),
            ('monitoring',  '50 devices',        '{""max_devices"":50}',    '{""saas"":{""usd"":{""year"":595,""month"":60},""inr"":{""year"":34000}},""onprem"":{""usd"":{""year"":480}}}', 30),
            ('monitoring',  '150 devices',       '{""max_devices"":150}',   '{""saas"":{""usd"":{""year"":1495,""month"":150},""inr"":{""year"":85500}},""onprem"":{""usd"":{""year"":1200}}}', 31),
            ('monitoring',  '500 devices',       '{""max_devices"":500}',   '{""saas"":{""usd"":{""year"":3995,""month"":400},""inr"":{""year"":228000}},""onprem"":{""usd"":{""year"":3200}}}', 32),
            ('adsuite',     '1 domain',          '{""max_domains"":1}',     '{""saas"":{""usd"":{""year"":795,""month"":80},""inr"":{""year"":45500}},""onprem"":{""usd"":{""year"":640}}}', 40),
            ('adsuite',     '3 domains',         '{""max_domains"":3}',     '{""saas"":{""usd"":{""year"":1895,""month"":190},""inr"":{""year"":108500}},""onprem"":{""usd"":{""year"":1520}}}', 41),
            ('adsuite',     '10 domains',        '{""max_domains"":10}',    '{""saas"":{""usd"":{""year"":4995,""month"":500},""inr"":{""year"":285000}},""onprem"":{""usd"":{""year"":4000}}}', 42),
            ('vault',       '10 users',          '{""max_users"":10}',      '{""saas"":{""usd"":{""year"":390,""month"":39},""inr"":{""year"":22500}},""onprem"":{""usd"":{""year"":315}}}', 50),
            ('vault',       '25 users',          '{""max_users"":25}',      '{""saas"":{""usd"":{""year"":895,""month"":90},""inr"":{""year"":51500}},""onprem"":{""usd"":{""year"":720}}}', 51),
            ('vault',       '100 users',         '{""max_users"":100}',     '{""saas"":{""usd"":{""year"":2995,""month"":300},""inr"":{""year"":171000}},""onprem"":{""usd"":{""year"":2400}}}', 52),
            ('backup',      '10 devices',        '{""max_devices"":10}',    '{""saas"":{""usd"":{""year"":290,""month"":29},""inr"":{""year"":17000}},""onprem"":{""usd"":{""year"":235}}}', 60),
            ('backup',      '50 devices',        '{""max_devices"":50}',    '{""saas"":{""usd"":{""year"":1195,""month"":120},""inr"":{""year"":68500}},""onprem"":{""usd"":{""year"":960}}}', 61),
            ('backup',      '200 devices',       '{""max_devices"":200}',   '{""saas"":{""usd"":{""year"":3995,""month"":400},""inr"":{""year"":228000}},""onprem"":{""usd"":{""year"":3200}}}', 62)
            on conflict (module, size_label) do nothing;"),
        new(3, "store: trials, orders paid online, invoices, payment events", @"
            alter table commerce.subscriptions drop constraint if exists subscriptions_state_check;
            alter table commerce.subscriptions add constraint subscriptions_state_check check (state in ('trial', 'active', 'cancelled', 'ended'));
            alter table commerce.subscriptions add column trial_warned_at timestamptz;
            alter table commerce.accounts add column phone text not null default '', add column state_code text not null default '';
            alter table commerce.orders add column access_hash bytea, add column quote jsonb not null default '{}', add column buyer jsonb not null default '{}',
              add column tenant_id text not null default '', add column paid_at timestamptz, add column fulfilled_at timestamptz, add column error text not null default '',
              add column payment_ref text not null default '', add column result text not null default '', add column invoice_id bigint;
            create unique index on commerce.orders (gateway, gateway_ref) where gateway_ref <> '';
            create table commerce.invoice_series (fy text primary key, next int not null);
            create table commerce.invoices (
              id bigserial primary key, number text not null unique, order_id uuid references commerce.orders(id), account_id uuid references commerce.accounts(id),
              issued_at timestamptz not null default now(), currency text not null, subtotal numeric(12,2) not null, tax numeric(12,2) not null, total numeric(12,2) not null,
              buyer jsonb not null default '{}', lines jsonb not null default '[]', pdf bytea not null);
            create table commerce.gateway_events (id text primary key, gateway text not null, type text not null, ok boolean not null, received_at timestamptz not null default now());
            create table commerce.trials (id uuid primary key default gen_random_uuid(), email text not null, domain text not null, ip text not null, tenant_id text not null, created_at timestamptz not null default now());
            create index on commerce.trials (created_at);"),
    };

    public static async Task<string?> GetSettingAsync(CommerceOptions o, string key, CancellationToken ct)
    {
        await using var db = await OpenAsync(o, ct);
        await using var q = new NpgsqlCommand("select value from commerce.settings where key = @k", db);
        q.Parameters.AddWithValue("k", key);
        return await q.ExecuteScalarAsync(ct) as string;
    }

    public static async Task SetSettingAsync(CommerceOptions o, string key, string value, CancellationToken ct)
    {
        await using var db = await OpenAsync(o, ct);
        await using var up = new NpgsqlCommand("insert into commerce.settings (key, value) values (@k, @v) on conflict (key) do update set value = excluded.value, updated_at = now()", db);
        up.Parameters.AddWithValue("k", key); up.Parameters.AddWithValue("v", value);
        await up.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// The vendor's private signing key. It lives in the server's protected configuration (Commerce:SigningKeyPem) or, imported once from the vendor
/// console, sealed with data protection in commerce.settings. It is never shown again. Its public half must match Licensing:VendorPublicKeyPem,
/// so every licence it signs is trusted by every installation.
/// </summary>
public sealed class Signer
{
    const string Purpose = "commerce.signing-key", SettingKey = "signing_key";
    readonly IConfiguration cfg; readonly CommerceOptions o; readonly IDataProtectionProvider dp; readonly VendorKey vendor;
    public Signer(IConfiguration cfg, CommerceOptions o, IDataProtectionProvider dp, VendorKey vendor) { this.cfg = cfg; this.o = o; this.dp = dp; this.vendor = vendor; }

    /// <summary>The key (caller disposes) and where it came from; null when none is set up.</summary>
    public async Task<(ECDsa? Key, string Source)> LoadAsync(CancellationToken ct)
    {
        string? pem = cfg["Commerce:SigningKeyPem"]; var source = "configuration";
        if (string.IsNullOrWhiteSpace(pem))
        {
            await using var db = await CommerceDb.OpenAsync(o, ct);
            await using var q = new NpgsqlCommand("select value from commerce.settings where key = @k", db);
            q.Parameters.AddWithValue("k", SettingKey);
            if (await q.ExecuteScalarAsync(ct) is string sealedPem)
                try { pem = dp.CreateProtector(Purpose).Unprotect(sealedPem); source = "stored"; } catch (CryptographicException) { return (null, "unreadable"); }
        }
        if (string.IsNullOrWhiteSpace(pem)) return (null, "none");
        var k = ECDsa.Create();
        try { k.ImportFromPem(pem); return (k, source); } catch (Exception) { k.Dispose(); return (null, "unreadable"); }
    }

    /// <summary>True when the key's public half is the vendor key every installation trusts.</summary>
    public bool Matches(ECDsa key) => vendor.Public != null && LicenseCodec.KeyId(key) == LicenseCodec.KeyId(vendor.Public);

    /// <summary>Checks and seals a pasted private key. Returns the refusal, or null when stored.</summary>
    public async Task<string?> StoreAsync(string pem, CancellationToken ct)
    {
        using var k = ECDsa.Create();
        try { k.ImportFromPem(pem); } catch (Exception) { return "This is not an EC private key in PEM form (-----BEGIN PRIVATE KEY----- or -----BEGIN EC PRIVATE KEY-----)."; }
        try { k.ExportECPrivateKey(); } catch (Exception) { return "This is a public key. Paste the private key."; }
        if (k.KeySize != 256) return "The key must be an EC P-256 key, as made by the licence tool (deskask-license keygen).";
        if (vendor.Public == null) return "This installation has no vendor public key (Licensing:VendorPublicKeyPem), so the key cannot be checked.";
        if (!Matches(k)) return "This private key does not belong to the vendor public key this installation trusts. Licences signed with it would be refused everywhere.";
        await using var db = await CommerceDb.OpenAsync(o, ct);
        await using var up = new NpgsqlCommand("insert into commerce.settings (key, value) values (@k, @v) on conflict (key) do update set value = excluded.value, updated_at = now()", db);
        up.Parameters.AddWithValue("k", SettingKey); up.Parameters.AddWithValue("v", dp.CreateProtector(Purpose).Protect(pem.Trim()));
        await up.ExecuteNonQueryAsync(ct);
        return null;
    }
}

public sealed record Sub(Guid Id, Guid AccountId, string Kind, string TenantId, string Plan, Dictionary<string, Dictionary<string, int>> Lines, DateTime StartsAt, DateTime EndsAt,
    int GraceDays, string State, string Period, string Notes, DateTime UpdatedAt);

/// <summary>Issuing licences from subscriptions, and the small helpers shared by the console and the public store addresses.</summary>
public static class Licences
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const string SubColumns = "id, account_id, kind, tenant_id, plan, lines::text, starts_at, ends_at, grace_days, state, period, notes, updated_at";
    public static Sub ReadSub(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetString(4),
        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(r.GetString(5), Json) ?? new(), r.GetDateTime(6), r.GetDateTime(7),
        r.GetInt32(8), r.GetString(9), r.GetString(10), r.GetString(11), r.GetDateTime(12));

    public static async Task<Sub?> GetSubAsync(NpgsqlConnection db, Guid id, CancellationToken ct, NpgsqlTransaction? tx = null)
    {
        await using var q = new NpgsqlCommand("select " + SubColumns + " from commerce.subscriptions where id = @i", db, tx);
        q.Parameters.AddWithValue("i", id);
        await using var r = await q.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadSub(r) : null;
    }

    /// <summary>Checks the modules and limits of a subscription. Returns the refusal or null.</summary>
    public static string? CheckLines(Dictionary<string, Dictionary<string, int>> lines)
    {
        if (lines.Count == 0) return "Choose at least one module.";
        foreach (var (m, limits) in lines)
        {
            if (!Modules.All.Contains(m)) return "Unknown module '" + m + "'.";
            foreach (var (name, v) in limits)
            {
                if (!LicenseLimits.Known.TryGetValue(m, out var ok) || !ok.Contains(name)) return "Unknown limit '" + name + "' for " + m + ".";
                if (v < 0 || v > 10_000_000) return "The limit " + m + ":" + name + " must be between 0 and 10,000,000.";
            }
        }
        if (lines.ContainsKey(Modules.Monitoring) && !lines.ContainsKey(Modules.Assets)) return "Monitoring needs Asset management in the same licence.";
        return null;
    }

    /// <summary>Signs a licence for a subscription and records it. The licence ends when the subscription ends (sooner if it was ended early).</summary>
    public static async Task<(long Id, string Token)> IssueAsync(NpgsqlConnection db, NpgsqlTransaction? tx, ECDsa key, Sub s, string tenantId, string installation,
        string by, string reason, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = new DateTimeOffset(DateTime.SpecifyKind(s.EndsAt, DateTimeKind.Utc));
        var payload = new LicensePayload(1, tenantId, s.Plan, now.ToUnixTimeSeconds(), new DateTimeOffset(DateTime.SpecifyKind(s.StartsAt, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            expires.ToUnixTimeSeconds(), s.GraceDays, s.Lines.ToDictionary(kv => kv.Key, kv => new ModuleGrant(kv.Value.Count == 0 ? null : new Dictionary<string, int>(kv.Value))),
            installation == "" ? null : installation);
        var token = LicenseCodec.Issue(key, payload);
        await using var ins = new NpgsqlCommand(@"insert into commerce.licences (subscription_id, tenant_id, installation, token, kid, plan, expires_at, issued_by, reason)
            values (@s, @t, @i, @k, @kid, @p, @e, @b, @r) returning id", db, tx);
        ins.Parameters.AddWithValue("s", s.Id); ins.Parameters.AddWithValue("t", tenantId); ins.Parameters.AddWithValue("i", installation);
        ins.Parameters.AddWithValue("k", token); ins.Parameters.AddWithValue("kid", LicenseCodec.KeyId(key)); ins.Parameters.AddWithValue("p", s.Plan);
        ins.Parameters.AddWithValue("e", expires.UtcDateTime); ins.Parameters.AddWithValue("b", by); ins.Parameters.AddWithValue("r", reason);
        var id = (long)(await ins.ExecuteScalarAsync(ct))!;
        return (id, token);
    }

    /// <summary>Puts a SaaS customer's licence into the catalog, where their platform reads it. Returns the refusal or null.</summary>
    public static async Task<string?> InstallSaasAsync(CommerceOptions o, string tenantId, string token, CancellationToken ct)
    {
        if (o.Catalog == "") return "This installation has no platform catalog, so SaaS customers cannot be licensed from here.";
        await using var db = new NpgsqlConnection(o.Catalog); await db.OpenAsync(ct);
        await using (var q = new NpgsqlCommand("select 1 from platform.tenants where id = @t", db))
        {
            q.Parameters.AddWithValue("t", tenantId);
            if (await q.ExecuteScalarAsync(ct) == null) return "There is no customer '" + tenantId + "' on this platform.";
        }
        await using var up = new NpgsqlCommand(@"insert into platform.licenses (tenant_id, token, updated_at) values (@t, @k, now())
            on conflict (tenant_id) do update set token = excluded.token, updated_at = now()", db);
        up.Parameters.AddWithValue("t", tenantId); up.Parameters.AddWithValue("k", token);
        await up.ExecuteNonQueryAsync(ct);
        return null;
    }

    // ------------------------------------------------------------- licence keys
    const string KeyChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // no I, O, 0 or 1: easy to read out and type

    /// <summary>A new licence key: DKA- and five groups of five characters (125 random bits).</summary>
    public static string NewKey()
    {
        var c = Enumerable.Range(0, 25).Select(_ => KeyChars[RandomNumberGenerator.GetInt32(KeyChars.Length)]).ToArray();
        return "DKA-" + string.Join("-", Enumerable.Range(0, 5).Select(i => new string(c, i * 5, 5)));
    }

    /// <summary>The characters of a key that count: no "DKA-" prefix, dashes or spaces, upper case.</summary>
    public static string NormalizeKey(string key)
    {
        var k = new string(key.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        return k.StartsWith("DKA") && k.Length == 28 ? k[3..] : k;
    }

    public static byte[] Hash(string s) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s));
    public static string Secret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class CommerceModule : IPlatformModule
{
    public string Key => "commerce";
    public string Schema => "commerce";

    public void AddServices(IServiceCollection s, IConfiguration cfg)
    {
        s.AddSingleton<CommerceOptions>();
        s.AddSingleton<Signer>();
        s.AddSingleton<IJobHandler, TrialsJob>();
        s.AddSingleton(new RecurringJob("commerce.trials", TrialsJob.JobType, TimeSpan.FromHours(6)));
    }

    public void MapEndpoints(IEndpointRouteBuilder r)
    {
        VendorConsole.Map(r);
        StoreApi.Map(r);
        StoreFront.Map(r);
        StoreConsole.Map(r);
        Pages.Map(r);
    }
}
