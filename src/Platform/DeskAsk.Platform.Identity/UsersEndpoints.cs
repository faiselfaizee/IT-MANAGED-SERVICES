using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace DeskAsk.Platform.Identity;

/// <summary>Administrators manage the people who can sign in: staff (admin, engineer, viewer) and customer users (customer, cadmin).</summary>
public static class UsersEndpoints
{
    public static readonly string[] Roles = { "admin", "engineer", "viewer", "cadmin", "customer" };
    const int MinPassword = 10;

    sealed record NewUser(string? Email, string? Name, string? Role, string? Password);
    sealed record EditUser(string? Name, string? Role, bool? Active);
    sealed record Reset(string? Password);

    static object Row(UserAccount u) => new { id = u.Id, email = u.Email, name = u.Name, role = u.Role, active = u.Active, twoStep = u.TotpEnabled, createdAt = u.CreatedAt, signIn = SignInKind(u.ExternalId) };
    /// <summary>How the person signs in: password, windows (their company's domain controller), ad (this installation's AD), microsoft or google.</summary>
    static string SignInKind(string? ext) => ext == null ? "password" : ext.StartsWith(CompanyAccount.Prefix, StringComparison.Ordinal) ? "windows" : ext.StartsWith("ad:", StringComparison.Ordinal) ? "ad" : ext.StartsWith("google:", StringComparison.Ordinal) ? "google" : "microsoft";

    static string? PasswordProblem(string? p) => string.IsNullOrEmpty(p) || p.Length < MinPassword ? $"The password needs at least {MinPassword} characters." : null;

    public static void Map(IEndpointRouteBuilder r)
    {
        var g = r.MapGroup("/api/platform/users").RequireAuthorization(p => p.RequireRole("admin"));

        g.MapGet("", async (IdentityDb db, CancellationToken ct) =>
            Results.Ok((await db.Users.AsNoTracking().OrderBy(x => x.Email).ToListAsync(ct)).Select(Row)));

        g.MapGet("/roles", () => Results.Ok(Roles));

        g.MapPost("", async (NewUser b, IServiceProvider sp, IdentityDb db, IPasswordHasher<UserAccount> hasher, IAuditLog audit, IEntitlements ent, CancellationToken ct) =>
        {
            var email = (b.Email ?? "").Trim().ToLowerInvariant();
            if (email.Length > 320 || !System.Net.Mail.MailAddress.TryCreate(email, out _) || !email.Contains('@')) return Results.BadRequest(new { error = "Enter a valid email address." });
            var name = (b.Name ?? "").Trim();
            if (name.Length == 0 || name.Length > 200) return Results.BadRequest(new { error = "Enter the person's name." });
            var role = (b.Role ?? "").Trim().ToLowerInvariant();
            if (!Roles.Contains(role)) return Results.BadRequest(new { error = "Choose a role: " + string.Join(", ", Roles) + "." });
            if (PasswordProblem(b.Password) is { } pp) return Results.BadRequest(new { error = pp });
            if (await db.Users.AnyAsync(x => x.Email == email, ct)) return Results.Conflict(new { error = "Someone with this email already exists." });
            if (StaffLimit.IsStaff(role) && await StaffLimit.RefuseAsync(ent, db, 1, ct) is { } over) return over;
            var u = new UserAccount { Email = email, Name = name, Role = role };
            u.PasswordHash = hasher.HashPassword(u, b.Password!);
            db.Users.Add(u);
            await db.SaveChangesAsync(ct);
            await SingleCompanyLink.TryAsync(sp, new[] { u }, ct);
            await audit.WriteAsync("user.created", u.Email, "role=" + role, ct: ct);
            return Results.Created("/api/platform/users/" + u.Id, Row(u));
        });

        g.MapPatch("/{id:guid}", async (Guid id, EditUser b, ClaimsPrincipal me, IdentityDb db, IAuditLog audit, IEntitlements ent, CancellationToken ct) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (u == null) return Results.NotFound();
            var self = me.FindFirstValue(Claims.Subject) == id.ToString();
            var wasStaff = u.Active && StaffLimit.IsStaff(u.Role);
            var changes = new List<string>();
            if (b.Name != null)
            {
                var n = b.Name.Trim();
                if (n.Length == 0 || n.Length > 200) return Results.BadRequest(new { error = "Enter the person's name." });
                if (n != u.Name) { u.Name = n; changes.Add("name"); }
            }
            if (b.Role != null)
            {
                var role = b.Role.Trim().ToLowerInvariant();
                if (!Roles.Contains(role)) return Results.BadRequest(new { error = "Choose a role: " + string.Join(", ", Roles) + "." });
                if (role != u.Role)
                {
                    if (self) return Results.BadRequest(new { error = "You cannot change your own role." });
                    if (u.Role == "admin" && u.Active && await LastAdmin(db, u, ct)) return Results.BadRequest(new { error = "There must be at least one active administrator." });
                    changes.Add("role " + u.Role + "→" + role); u.Role = role;
                }
            }
            if (b.Active is bool active && active != u.Active)
            {
                if (!active && self) return Results.BadRequest(new { error = "You cannot disable your own account." });
                if (!active && u.Role == "admin" && await LastAdmin(db, u, ct)) return Results.BadRequest(new { error = "There must be at least one active administrator." });
                u.Active = active; changes.Add(active ? "enabled" : "disabled");
            }
            // becoming an active administrator or engineer takes a staff account of the licence
            if (!wasStaff && u.Active && StaffLimit.IsStaff(u.Role) && await StaffLimit.RefuseAsync(ent, db, 1, ct) is { } over) return over;
            if (changes.Count > 0)
            {
                await db.SaveChangesAsync(ct);
                await audit.WriteAsync("user.updated", u.Email, string.Join(", ", changes), ct: ct);
            }
            return Results.Ok(Row(u));
        });

        g.MapPost("/{id:guid}/password", async (Guid id, Reset b, IdentityDb db, IPasswordHasher<UserAccount> hasher, IAuditLog audit, CancellationToken ct) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (u == null) return Results.NotFound();
            if (DirectoryAccount.Is(u.ExternalId)) return Results.BadRequest(new { error = "This person signs in with their Windows (directory) password. Change it in Active Directory; DeskAsk keeps no password for them." });
            if (PasswordProblem(b.Password) is { } pp) return Results.BadRequest(new { error = pp });
            u.PasswordHash = hasher.HashPassword(u, b.Password!);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("user.password_reset", u.Email, ct: ct);
            return Results.Ok(new { ok = true });
        });

        // a company's employees picked from its domain controller (Devices setup › Customers › a company › Users): they sign in with their Windows password
        r.MapPost("/api/companies/{companyId:int}/users/from-directory", async (int companyId, JsonElement b, IdentityDb db, IPasswordHasher<UserAccount> hasher, ICompanyDirectory companies, IAccess access, IAuditLog audit, CancellationToken ct) =>
        {
            if (await companies.GetAsync(companyId, ct) == null) return Results.NotFound(new { error = "That company does not exist." });
            var role = b.TryGetProperty("role", out var rv) && rv.ValueKind == JsonValueKind.String ? rv.GetString()!.Trim().ToLowerInvariant() : "customer";
            if (role is not ("customer" or "cadmin")) return Results.BadRequest(new { error = "Choose employee or customer administrator." });
            if (!b.TryGetProperty("people", out var ps) || ps.ValueKind != JsonValueKind.Array || ps.GetArrayLength() is 0 or > 500) return Results.BadRequest(new { error = "Choose 1 to 500 people." });
            static string S(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
            int created = 0, linked = 0, skipped = 0; var tie = new List<Guid>();
            foreach (var p in ps.EnumerateArray())
            {
                var account = S(p, "account"); var email = S(p, "email").ToLowerInvariant(); var name = S(p, "name");
                if (account.Length is 0 or > 104 || account.Any(char.IsControl) || account.Contains(':') || email.Length > 320 || !System.Net.Mail.MailAddress.TryCreate(email, out _)) { skipped++; continue; }
                var ext = CompanyAccount.Make(companyId, account);
                var ex = await db.Users.FirstOrDefaultAsync(x => x.Email == email, ct);
                if (ex != null)
                {
                    // only a customer user who is not linked to another sign-in is taken over; staff and other links are left alone
                    if (ex.Role is "customer" or "cadmin" && (ex.ExternalId == null || ex.ExternalId == ext)) { ex.ExternalId = ext; ex.PasswordHash = hasher.HashPassword(ex, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))); tie.Add(ex.Id); linked++; } else skipped++;
                    continue;
                }
                var u = new UserAccount { Email = email, Name = name == "" ? email : (name.Length > 200 ? name[..200] : name), Role = role, ExternalId = ext };
                u.PasswordHash = hasher.HashPassword(u, Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));   // never used: they sign in with Windows
                db.Users.Add(u); tie.Add(u.Id); created++;
            }
            await db.SaveChangesAsync(ct);
            foreach (var id in tie) await access.SetCompanyAsync(id, companyId, ct);
            await audit.WriteAsync("user.imported", "company:" + companyId, $"from the domain controller: created {created}, linked {linked}, skipped {skipped}, role {role}", ct: ct);
            return Results.Ok(new { created, linked, skipped });
        }).RequireAuthorization(p => p.RequireRole("admin"));

        g.MapPost("/{id:guid}/two-step/reset", async (Guid id, IdentityDb db, IAuditLog audit, CancellationToken ct) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (u == null) return Results.NotFound();
            u.TotpEnabled = false; u.TotpSecret = null; u.TotpLastStep = 0; u.RecoveryHashes = "[]";
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("user.two_step_reset", u.Email, ct: ct);
            return Results.Ok(new { ok = true });
        });
    }

    static Task<bool> LastAdmin(IdentityDb db, UserAccount u, CancellationToken ct) =>
        db.Users.AllAsync(x => x.Id == u.Id || x.Role != "admin" || !x.Active, ct);
}

/// <summary>Lets other modules look up people (names for assignees, requesters) without reading the identity tables.</summary>
public sealed class UserDirectory : IUserDirectory
{
    readonly IdentityDb db;
    public UserDirectory(IdentityDb db) => this.db = db;
    public async Task<UserRef?> GetAsync(Guid id, CancellationToken ct = default) =>
        await db.Users.AsNoTracking().Where(x => x.Id == id).Select(u => new UserRef(u.Id, u.Name, u.Email, u.Role, u.Active)).FirstOrDefaultAsync(ct);
    public async Task<IReadOnlyList<UserRef>> ListAsync(CancellationToken ct = default) =>
        await db.Users.AsNoTracking().OrderBy(x => x.Name).Select(u => new UserRef(u.Id, u.Name, u.Email, u.Role, u.Active)).ToListAsync(ct);
}
