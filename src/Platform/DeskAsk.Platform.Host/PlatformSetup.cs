using DeskAsk.Platform.Assets;
using DeskAsk.Platform.Connect;
using DeskAsk.Platform.Core;
using DeskAsk.Platform.Identity;
using DeskAsk.Platform.Jobs;
using DeskAsk.Platform.Licensing;
using DeskAsk.Platform.Tenancy;
using DeskAsk.Platform.Updates;

namespace DeskAsk.Platform;

/// <summary>Registers the platform: customers, then every module. Used by the web host and by the "tenant" command line.</summary>
public static class PlatformSetup
{
    public static (PlatformRole Roles, IReadOnlyList<IPlatformModule> Modules) Add(WebApplicationBuilder builder)
    {
        var roles = PlatformRoles.Parse(builder.Configuration);   // one build, several roles (ADR 0004): Platform:Roles = "web,worker,connect" (default: all)

        var catalogConn = builder.Configuration["Platform:CatalogConnection"];
        ITenantCatalog catalog = string.IsNullOrWhiteSpace(catalogConn)
            ? new InMemoryTenantCatalog(new[] { new TenantInfo("dev", "Development", "localhost", builder.Configuration["Platform:DevTenantConnection"] ?? "") })   // local development only
            : new PgTenantCatalog(catalogConn);
        builder.Services.AddTenancy(catalog);

        // Modules register here as they are built (Phase 1 onward).
        var modules = new List<IPlatformModule> { new CoreModule(), new IdentityModule(), new ConnectModule(), new LicensingModule(), new UpdatesModule(), new JobsModule(), new AssetsModule() };
        foreach (var m in modules) m.AddServices(builder.Services, builder.Configuration);
        return (roles, modules);
    }
}
