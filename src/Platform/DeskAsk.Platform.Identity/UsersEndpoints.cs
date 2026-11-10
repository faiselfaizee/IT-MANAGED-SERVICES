using System.Security.Claims;
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

    static object Row(UserAccount u) => new { id = u.Id, email = u.Email, name = u.Name, role = u.Role, active = u.Active, twoStep = u.TotpEnabled, createdAt = u.CreatedAt };

    static string? PasswordProblem(string? p) => string.IsNullOrEmpty(p) || p.Length < MinPassword ? $"The password needs at least {MinPassword} characters." : null;

    public static void Map(IEndpointRouteBuilder r)
    {
        var g = r.MapGroup("/api/platform/users").RequireAuthorization(p => p.RequireRole("admin"));

        g.MapGet("", async (IdentityDb db, CancellationToken ct) =>
            Results.Ok((await db.Users.AsNoTracking().OrderBy(x => x.Email).ToListAsync(ct)).Select(Row)));

        g.MapGet("/roles", () => Results.Ok(Roles));

        g.MapPost("", async (NewUser b, IdentityDb db, IPasswordHasher<UserAccount> hasher, IAuditLog audit, CancellationToken ct) =>
        {
            var email = (b.Email ?? "").Trim().ToLowerInvariant();
            if (email.Length > 320 || !System.Net.Mail.MailAddress.TryCreate(email, out _) || !email.Contains('@')) return Results.BadRequest(new { error = "Enter a valid email address." });
            var name = (b.Name ?? "").Trim();
            if (name.Length == 0 || name.Length > 200) return Results.BadRequest(new { error = "Enter the person's name." });
            var role = (b.Role ?? "").Trim().ToLowerInvariant();
            if (!Roles.Contains(role)) return Results.BadRequest(new { error = "Choose a role: " + string.Join(", ", Roles) + "." });
            if (PasswordProblem(b.Password) is { } pp) return Results.BadRequest(new { error = pp });
            if (await db.Users.AnyAsync(x => x.Email == email, ct)) return Results.Conflict(new { error = "Someone with this email already exists." });
            var u = new UserAccount { Email = email, Name = name, Role = role };
            u.PasswordHash = hasher.HashPassword(u, b.Password!);
            db.Users.Add(u);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("user.created", u.Email, "role=" + role, ct: ct);
            return Results.Created("/api/platform/users/" + u.Id, Row(u));
        });

        g.MapPatch("/{id:guid}", async (Guid id, EditUser b, ClaimsPrincipal me, IdentityDb db, IAuditLog audit, CancellationToken ct) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (u == null) return Results.NotFound();
            var self = me.FindFirstValue(Claims.Subject) == id.ToString();
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
            if (PasswordProblem(b.Password) is { } pp) return Results.BadRequest(new { error = pp });
            u.PasswordHash = hasher.HashPassword(u, b.Password!);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("user.password_reset", u.Email, ct: ct);
            return Results.Ok(new { ok = true });
        });

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
