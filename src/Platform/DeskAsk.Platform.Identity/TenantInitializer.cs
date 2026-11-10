using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace DeskAsk.Platform.Identity;

/// <summary>
/// Makes sure a customer database has the identity schema, a first administrator and the configured sign-in clients.
/// Runs once per customer per process, on the customer's first request. (Real migrations replace the create script later.)
/// </summary>
public sealed class IdentitySchema : ITenantSchema
{
    public string Name => "identity";

    static Migration[] Migrations(IdentityDb db) => new Migration[]
    {
        // v1 is the schema as the first release made it (users + OpenIddict tables), generated from the model that shipped then.
        // From v2 on, every change is plain SQL here.
        new(1, "users and OpenIddict tables", Run: async (conn, tx, ct) =>
        {
            await using var q = new Npgsql.NpgsqlCommand("select to_regclass('identity.users') is not null", conn, tx);
            if ((bool)(await q.ExecuteScalarAsync(ct))!) return;
            await using var create = new Npgsql.NpgsqlCommand(db.Database.GenerateCreateScript(), conn, tx);
            await create.ExecuteNonQueryAsync(ct);
        }),
        new(2, "two-step sign-in columns", @"alter table identity.users
            add column if not exists totp_secret text, add column if not exists totp_enabled boolean not null default false,
            add column if not exists totp_last_step bigint not null default 0, add column if not exists recovery_hashes text not null default '[]'"),
        new(3, "per-customer signing keys", @"create table if not exists identity.keys (
            id bigserial primary key, tenant_id text not null, kid text not null, private_sealed text not null,
            created_at timestamptz not null default now(), retired_at timestamptz);
            create unique index if not exists keys_active on identity.keys (tenant_id) where retired_at is null"),
    };

    public async Task EnsureAsync(IServiceProvider sp, TenantInfo tenant, CancellationToken ct)
    {
        var db = sp.GetRequiredService<IdentityDb>();
        await SchemaMigrator.RunAsync(tenant.ConnectionString, Name, Migrations(db), ct);
        var cfg = sp.GetRequiredService<IConfiguration>();
        await SeedAdminAsync(db, cfg, sp.GetRequiredService<IPasswordHasher<UserAccount>>(), ct);
        var apps = sp.GetRequiredService<IOpenIddictApplicationManager>();
        await SeedClientsAsync(apps, cfg, ct);
        await EnsureWebClientAsync(apps, tenant.Host, ct);
    }

    /// <summary>
    /// The web app signs in as client "web" and comes back to https://{this customer's address}/auth/callback.
    /// Each customer has its own address, so that address is registered here, whatever Identity:Clients says.
    /// </summary>
    static async Task EnsureWebClientAsync(IOpenIddictApplicationManager apps, string host, CancellationToken ct)
    {
        var redirects = new List<Uri>(); var after = new List<Uri>();
        foreach (var scheme in new[] { "https", "http" })
            if (Uri.TryCreate(scheme + "://" + host + "/auth/callback", UriKind.Absolute, out var r))
            { redirects.Add(r); after.Add(new Uri(scheme + "://" + host + "/")); }
        if (redirects.Count == 0) return;

        var app = await apps.FindByClientIdAsync("web", ct);
        if (app == null)
        {
            var d = new OpenIddictApplicationDescriptor
            {
                ClientId = "web", DisplayName = "DeskAsk web", ClientType = ClientTypes.Public, ConsentType = ConsentTypes.Implicit,
                Permissions =
                {
                    Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.Endpoints.EndSession,
                    Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken, Permissions.ResponseTypes.Code,
                    Permissions.Scopes.Email, Permissions.Scopes.Profile, Permissions.Scopes.Roles, Permissions.Prefixes.Scope + "deskask",
                },
                Requirements = { Requirements.Features.ProofKeyForCodeExchange },
            };
            foreach (var r in redirects) d.RedirectUris.Add(r);
            foreach (var r in after) d.PostLogoutRedirectUris.Add(r);
            await apps.CreateAsync(d, ct);
            return;
        }
        var cur = new OpenIddictApplicationDescriptor();
        await apps.PopulateAsync(cur, app, ct);
        var changed = false;
        foreach (var r in redirects) if (!cur.RedirectUris.Contains(r)) { cur.RedirectUris.Add(r); changed = true; }
        foreach (var r in after) if (!cur.PostLogoutRedirectUris.Contains(r)) { cur.PostLogoutRedirectUris.Add(r); changed = true; }
        if (changed) await apps.UpdateAsync(app, cur, ct);
    }

    static async Task SeedAdminAsync(IdentityDb db, IConfiguration cfg, IPasswordHasher<UserAccount> hasher, CancellationToken ct)
    {
        var email = (cfg["Platform:BootstrapAdmin:Email"] ?? "").Trim().ToLowerInvariant();
        var pw = cfg["Platform:BootstrapAdmin:Password"] ?? "";
        if (email == "" || pw == "" || await db.Users.AnyAsync(ct)) return;
        var u = new UserAccount { Email = email, Name = cfg["Platform:BootstrapAdmin:Name"] ?? "Administrator", Role = "admin" };
        u.PasswordHash = hasher.HashPassword(u, pw);
        db.Users.Add(u);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Identity:Clients:N:{Id, Secret (empty = public client), Name, RedirectUris:[], PostLogoutRedirectUris:[]}</summary>
    static async Task SeedClientsAsync(IOpenIddictApplicationManager apps, IConfiguration cfg, CancellationToken ct)
    {
        foreach (var c in cfg.GetSection("Identity:Clients").GetChildren())
        {
            var id = c["Id"]; if (string.IsNullOrWhiteSpace(id) || await apps.FindByClientIdAsync(id, ct) != null) continue;
            var secret = c["Secret"];
            var d = new OpenIddictApplicationDescriptor
            {
                ClientId = id,
                DisplayName = c["Name"] ?? id,
                ClientType = string.IsNullOrEmpty(secret) ? ClientTypes.Public : ClientTypes.Confidential,
                ClientSecret = string.IsNullOrEmpty(secret) ? null : secret,
                ConsentType = ConsentTypes.Implicit,
                Permissions =
                {
                    Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.Endpoints.EndSession,
                    Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken, Permissions.ResponseTypes.Code,
                    Permissions.Scopes.Email, Permissions.Scopes.Profile, Permissions.Scopes.Roles, Permissions.Prefixes.Scope + "deskask",
                },
                Requirements = { Requirements.Features.ProofKeyForCodeExchange },
            };
            foreach (var r in c.GetSection("RedirectUris").GetChildren()) if (!string.IsNullOrWhiteSpace(r.Value)) d.RedirectUris.Add(new Uri(r.Value!));
            foreach (var r in c.GetSection("PostLogoutRedirectUris").GetChildren()) if (!string.IsNullOrWhiteSpace(r.Value)) d.PostLogoutRedirectUris.Add(new Uri(r.Value!));
            await apps.CreateAsync(d, ct);
        }
    }
}
