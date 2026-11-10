using DeskAsk.Platform;
using DeskAsk.Platform.Connect;
using DeskAsk.Platform.Core;
using DeskAsk.Platform.Identity;
using DeskAsk.Platform.Jobs;
using DeskAsk.Platform.Licensing;
using DeskAsk.Platform.Updates;
using DeskAsk.Platform.Tenancy;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = "DeskAskPlatform");   // does nothing unless started by the Windows service manager

// Settings and secrets of a server install live in one protected file (DESKASK_CONFIG), see docs/windows-install.md.
var configFile = Environment.GetEnvironmentVariable("DESKASK_CONFIG");
if (!string.IsNullOrWhiteSpace(configFile)) builder.Configuration.AddJsonFile(configFile, optional: false, reloadOnChange: false);

// "DeskAsk.Platform.Host tenant create|list|enable|disable|migrate|init ..." manages customers and exits (docs/provisioning.md).
if (args.Length > 0 && args[0] == "tenant") { Environment.ExitCode = await TenantCli.RunAsync(args[1..], builder); return; }

// Behind a reverse proxy (IIS) the platform only sees plain http. When it listens on this machine only, the proxy in front of it is
// the only thing that can reach it, so X-Forwarded-Proto / -For / -Host are believed (sign-in needs to know the outside address was https, and each
// customer is found by the address in the browser, which a proxy does not always keep in the Host header).
// Platform:TrustProxyHeaders=true does the same for a platform that is reachable from elsewhere.
var listenUrls = (builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var trustProxy = builder.Configuration.GetValue<bool>("Platform:TrustProxyHeaders")
    || (listenUrls.Length > 0 && listenUrls.All(u => u.Contains("//localhost", StringComparison.OrdinalIgnoreCase) || u.Contains("//127.0.0.1") || u.Contains("//[::1]")));
if (trustProxy) builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownNetworks.Clear(); o.KnownProxies.Clear();
});

var (roles, modules) = PlatformSetup.Add(builder);

var app = builder.Build();
if (trustProxy) app.UseForwardedHeaders();
app.UseTenancy();
app.UseTenantSchemas();
app.UseIdentity();

app.MapGet("/health", (HttpContext c) => Results.Ok(new { status = "ok", version = typeof(Program).Assembly.GetName().Version?.ToString(), roles = roles.ToString(), host = c.Request.Host.Host }));
app.MapGet("/api/platform/whoami", (ITenantContext t) => Results.Ok(new { tenant = t.Require().Id, name = t.Require().Name }));
foreach (var m in modules) m.MapEndpoints(app);

// The web app (web/dist) is shipped inside the bundle as wwwroot. Everything that is not an API or sign-in address falls back to it.
app.UseDefaultFiles();
app.UseStaticFiles();
// Not for addresses that end in a file name (.js, .css, .ico, ...): once an endpoint matches, the static file middleware steps aside,
// so a script request would be answered with the page instead of the script.
app.MapFallbackToFile("/{*path:regex(^(?!api/|connect/|account/|\\.well-known/|health)(?!.*\\.[A-Za-z0-9]+$).*$)}", "index.html");

app.Run();

public partial class Program { }
