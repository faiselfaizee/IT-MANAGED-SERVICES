using System.Collections.Concurrent;
using DeskAsk.Platform.Tenancy;
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
public sealed class TenantInitializer
{
    readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new();
    readonly ConcurrentDictionary<string, bool> done = new();

    public async Task EnsureAsync(IServiceProvider sp, TenantInfo tenant, CancellationToken ct)
    {
        if (done.ContainsKey(tenant.Id)) return;
        var gate = locks.GetOrAdd(tenant.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (done.ContainsKey(tenant.Id)) return;
            var db = sp.GetRequiredService<IdentityDb>();
            var exists = await db.Database.SqlQueryRaw<bool>("select to_regclass('identity.users') is not null as \"Value\"").SingleAsync(ct);
            if (!exists) await db.Database.ExecuteSqlRawAsync(db.Database.GenerateCreateScript(), ct);
            var cfg = sp.GetRequiredService<IConfiguration>();
            await SeedAdminAsync(db, cfg, sp.GetRequiredService<IPasswordHasher<UserAccount>>(), ct);
            await SeedClientsAsync(sp.GetRequiredService<IOpenIddictApplicationManager>(), cfg, ct);
            done[tenant.Id] = true;
        }
        finally { gate.Release(); }
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
