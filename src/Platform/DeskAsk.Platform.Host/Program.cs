using DeskAsk.Platform;
using DeskAsk.Platform.Connect;
using DeskAsk.Platform.Core;
using DeskAsk.Platform.Identity;
using DeskAsk.Platform.Jobs;
using DeskAsk.Platform.Licensing;
using DeskAsk.Platform.Updates;
using DeskAsk.Platform.Tenancy;

var builder = WebApplication.CreateBuilder(args);

// "DeskAsk.Platform.Host tenant create|list|enable|disable|migrate|init ..." manages customers and exits (docs/provisioning.md).
if (args.Length > 0 && args[0] == "tenant") { Environment.ExitCode = await TenantCli.RunAsync(args[1..], builder); return; }

var (roles, modules) = PlatformSetup.Add(builder);

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
