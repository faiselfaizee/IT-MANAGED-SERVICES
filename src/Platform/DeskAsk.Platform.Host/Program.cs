using DeskAsk.Platform;
using DeskAsk.Platform.Connect;
using DeskAsk.Platform.Core;
using DeskAsk.Platform.Identity;
using DeskAsk.Platform.Jobs;
using DeskAsk.Platform.Licensing;
using DeskAsk.Platform.Updates;
using DeskAsk.Platform.Tenancy;

var builder = WebApplication.CreateBuilder(args);

// One build, several roles (ADR 0004): Platform:Roles = "web,worker,connect" (default: all).
var roles = PlatformRoles.Parse(builder.Configuration);

var catalogConn = builder.Configuration["Platform:CatalogConnection"];
ITenantCatalog catalog = string.IsNullOrWhiteSpace(catalogConn)
    ? new InMemoryTenantCatalog(new[] { new TenantInfo("dev", "Development", "localhost", builder.Configuration["Platform:DevTenantConnection"] ?? "") })   // local development only
    : new PgTenantCatalog(catalogConn);
builder.Services.AddTenancy(catalog);

// Modules register here as they are built (Phase 1 onward).
var modules = new List<IPlatformModule> { new CoreModule(), new IdentityModule(), new ConnectModule(), new LicensingModule(), new UpdatesModule(), new JobsModule() };
foreach (var m in modules) m.AddServices(builder.Services, builder.Configuration);

var app = builder.Build();
app.UseTenancy();
app.UseTenantSchemas();
app.UseIdentity();

app.MapGet("/health", () => Results.Ok(new { status = "ok", version = typeof(Program).Assembly.GetName().Version?.ToString(), roles = roles.ToString() }));
app.MapGet("/api/platform/whoami", (ITenantContext t) => Results.Ok(new { tenant = t.Require().Id, name = t.Require().Name }));
foreach (var m in modules) m.MapEndpoints(app);

// The web app (web/dist) is shipped inside the bundle as wwwroot. Everything that is not an API or sign-in address falls back to it.
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("/{*path:regex(^(?!api/|connect/|account/|\\.well-known/|health).*$)}", "index.html");

app.Run();

public partial class Program { }
