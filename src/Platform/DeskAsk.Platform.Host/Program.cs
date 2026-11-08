using DeskAsk.Platform;
using DeskAsk.Platform.Identity;
using DeskAsk.Platform.Tenancy;

var builder = WebApplication.CreateBuilder(args);

// One build, several roles (ADR 0004): Platform:Roles = "web,worker,connect" (default: all).
var roles = PlatformRole.None;
foreach (var r in (builder.Configuration["Platform:Roles"] ?? "all").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    if (Enum.TryParse<PlatformRole>(r, true, out var one)) roles |= one;

var catalogConn = builder.Configuration["Platform:CatalogConnection"];
ITenantCatalog catalog = string.IsNullOrWhiteSpace(catalogConn)
    ? new InMemoryTenantCatalog(new[] { new TenantInfo("dev", "Development", "localhost", builder.Configuration["Platform:DevTenantConnection"] ?? "") })   // local development only
    : new PgTenantCatalog(catalogConn);
builder.Services.AddTenancy(catalog);

// Modules register here as they are built (Phase 1 onward).
var modules = new List<IPlatformModule> { new IdentityModule() };
foreach (var m in modules) m.AddServices(builder.Services, builder.Configuration);

var app = builder.Build();
app.UseTenancy();
app.UseIdentity();

app.MapGet("/health", () => Results.Ok(new { status = "ok", version = typeof(Program).Assembly.GetName().Version?.ToString(), roles = roles.ToString() }));
app.MapGet("/api/platform/whoami", (ITenantContext t) => Results.Ok(new { tenant = t.Require().Id, name = t.Require().Name }));
foreach (var m in modules) m.MapEndpoints(app);

app.Run();

public partial class Program { }
