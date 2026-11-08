using Microsoft.EntityFrameworkCore;

namespace DeskAsk.Platform.Identity;

public sealed class UserAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";          // always lower case
    public string Name { get; set; } = "";
    public string Role { get; set; } = "user";
    public string PasswordHash { get; set; } = "";
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>The identity schema of one customer database: users plus the OpenIddict tables (clients, authorizations, tokens).</summary>
public sealed class IdentityDb : DbContext
{
    public IdentityDb(DbContextOptions<IdentityDb> options) : base(options) { }
    public DbSet<UserAccount> Users => Set<UserAccount>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("identity");
        b.Entity<UserAccount>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Role).HasMaxLength(50);
        });
    }
}
