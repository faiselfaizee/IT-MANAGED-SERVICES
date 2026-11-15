namespace DeskAsk.Platform;

/// <summary>One customer. Its data lives in its own database (ADR 0002).</summary>
public sealed record TenantInfo(string Id, string Name, string Host, string ConnectionString, bool Active = true);

/// <summary>The customer the current request belongs to. Set once by the tenancy middleware, never by a module.</summary>
public interface ITenantContext
{
    TenantInfo? Current { get; }
    TenantInfo Require();
}

/// <summary>Finds customers. The platform catalog database is the real source; tests use the in-memory one.</summary>
public interface ITenantCatalog
{
    Task<TenantInfo?> FindByHostAsync(string host, CancellationToken ct = default);
    /// <summary>All active customers: the worker walks them to run each one's background jobs.</summary>
    Task<IReadOnlyList<TenantInfo>> ListActiveAsync(CancellationToken ct = default);
}

/// <summary>The process roles one build can take (ADR 0004). Set with Platform:Roles, for example "web,worker".</summary>
[Flags]
public enum PlatformRole { None = 0, Web = 1, Worker = 2, Connect = 4, All = Web | Worker | Connect }

public static class PlatformRoles
{
    public static PlatformRole Parse(IConfiguration cfg)
    {
        var roles = PlatformRole.None;
        foreach (var r in (cfg["Platform:Roles"] ?? "all").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Enum.TryParse<PlatformRole>(r, true, out var one)) roles |= one;
        return roles;
    }
}

/// <summary>A product module (Service Desk, Assets, Vault ...). A module owns one schema and may not read another module's tables.</summary>
public interface IPlatformModule
{
    string Key { get; }
    string Schema { get; }
    void AddServices(IServiceCollection services, IConfiguration configuration);
    void MapEndpoints(IEndpointRouteBuilder routes);
}

/// <summary>Writes to the customer's tamper-evident audit log (each row carries the hash of the one before it).</summary>
public interface IAuditLog
{
    /// <summary>actor defaults to the signed-in person of the current request, else "system".</summary>
    Task WriteAsync(string action, string target = "", string detail = "", string? actor = null, CancellationToken ct = default);
}

/// <summary>Per-customer settings, stored as JSON text. Never put secrets here (use the Vault module).</summary>
public interface ISettings
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string jsonValue, CancellationToken ct = default);
    Task<bool> DeleteAsync(string key, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, string>> ListAsync(string prefix = "", CancellationToken ct = default);
}

/// <summary>A module's part of the customer database. Run once per customer per process, before the first request is handled.</summary>
public interface ITenantSchema
{
    string Name { get; }
    Task EnsureAsync(IServiceProvider services, TenantInfo tenant, CancellationToken ct);
}

/// <summary>The product modules a customer can be licensed for.</summary>
public static class Modules
{
    public const string ServiceDesk = "servicedesk", Assets = "assets", Monitoring = "monitoring", AdSuite = "adsuite", Vault = "vault", Backup = "backup";
    public static readonly string[] All = { ServiceDesk, Assets, Monitoring, AdSuite, Vault, Backup };
}

public sealed record ModuleGrant(Dictionary<string, int>? Limits = null);

/// <summary>What the vendor signed: who, which plan, which modules (with limits), from when to when.</summary>
public sealed record LicensePayload(int V, string Tenant, string Plan, long Issued, long NotBefore, long Expires, int GraceDays, Dictionary<string, ModuleGrant> Modules);

public enum LicenseState { Missing, Invalid, NotYetValid, Valid, Grace, Expired }

public sealed record License(LicenseState State, LicensePayload? Payload, string Reason)
{
    /// <summary>Valid, or expired but still inside the grace period.</summary>
    public bool InForce => State is LicenseState.Valid or LicenseState.Grace;
    public bool Has(string module) => InForce && Payload != null && Payload.Modules.ContainsKey(module);
    public int? Limit(string module, string name) =>
        Has(module) && Payload!.Modules[module].Limits is { } l && l.TryGetValue(name, out var v) ? v : null;
}

/// <summary>The licence of the current customer. Modules ask this; they never read licence files themselves.</summary>
public interface IEntitlements
{
    Task<License> CurrentAsync(CancellationToken ct = default);
}

public static class EntitlementExtensions
{
    /// <summary>402 {"error":"module_not_licensed"} unless the customer's licence includes the module.</summary>
    public static RouteHandlerBuilder RequireModule(this RouteHandlerBuilder b, string module) =>
        b.AddEndpointFilter(async (ctx, next) =>
        {
            var lic = await ctx.HttpContext.RequestServices.GetRequiredService<IEntitlements>().CurrentAsync(ctx.HttpContext.RequestAborted);
            return lic.Has(module) ? await next(ctx) : Results.Json(new { error = "module_not_licensed", module }, statusCode: 402);
        });

    /// <summary>True when count is still below the licensed limit (no limit = unlimited). Call before creating the next item.</summary>
    public static async Task<bool> WithinLimitAsync(this IEntitlements e, string module, string limit, long currentCount, CancellationToken ct = default)
    {
        var l = (await e.CurrentAsync(ct)).Limit(module, limit);
        return l == null || currentCount < l;
    }
}

/// <summary>What a background job gets: the customer it runs for, its id, payload (JSON text), attempt number and a scope whose ITenantContext is set.</summary>
public sealed record JobContext(TenantInfo Tenant, long Id, string Payload, int Attempt, IServiceProvider Services);

/// <summary>Runs one kind of background job. Must be safe to run twice (a job is retried after a failure or a crash).</summary>
public interface IJobHandler
{
    string Type { get; }
    Task RunAsync(JobContext job, CancellationToken ct);
}

public interface IJobQueue
{
    /// <summary>Queues a job for the current customer. With a dedupeKey, a job with that key that is still waiting or running is returned instead of a second one.</summary>
    Task<long> EnqueueAsync(string type, string payloadJson = "{}", DateTimeOffset? runAt = null, string? dedupeKey = null, CancellationToken ct = default);
}

/// <summary>A job every customer gets again and again: queued whenever its interval has passed.</summary>
public sealed record RecurringJob(string Name, string Type, TimeSpan Every);

/// <summary>A customer company as other modules may see it. The Assets module owns the companies.</summary>
public sealed record CompanyRef(int Id, string Name, string Code, bool Active);
/// <summary>An asset as other modules may see it (read only).</summary>
public sealed record AssetRef(long Id, string Tag, string Name, int? CompanyId);

public interface ICompanyDirectory
{
    Task<CompanyRef?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<CompanyRef>> ListAsync(CancellationToken ct = default);
}

public interface IAssetDirectory
{
    Task<AssetRef?> GetAsync(long id, CancellationToken ct = default);
}

/// <summary>A person who can sign in, as other modules may see them. The Identity module owns the accounts.</summary>
public sealed record UserRef(Guid Id, string Name, string Email, string Role, bool Active);

public interface IUserDirectory
{
    Task<UserRef?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<UserRef>> ListAsync(CancellationToken ct = default);
}

/// <summary>What a person may do, decided in one place (the access module). Modules ask this instead of checking roles themselves.</summary>
public interface IAccess
{
    /// <summary>May this person do this to this module? companyId null asks "to anything at all"; a number asks "to that company's data".</summary>
    Task<bool> AllowedAsync(System.Security.Claims.ClaimsPrincipal user, string module, string action, int? companyId = null, CancellationToken ct = default);
    /// <summary>The companies the person may act on: null means every company, an empty list means none.</summary>
    Task<IReadOnlyList<int>?> ScopeAsync(System.Security.Claims.ClaimsPrincipal user, string module, string action, CancellationToken ct = default);
    /// <summary>Ties a customer user to the one company whose data they may see (null removes the tie).</summary>
    Task SetCompanyAsync(Guid userId, int? companyId, CancellationToken ct = default);
    /// <summary>Every security group: id and name.</summary>
    Task<IReadOnlyList<(Guid Id, string Name)>> GroupsAsync(CancellationToken ct = default);
    /// <summary>The groups a person is in, directly or through a group inside a group.</summary>
    Task<IReadOnlyList<Guid>> GroupsOfAsync(Guid userId, CancellationToken ct = default);
    /// <summary>The people in a group, directly or through groups inside it.</summary>
    Task<IReadOnlyList<Guid>> GroupUsersAsync(Guid groupId, CancellationToken ct = default);
}

public static class Acts { public const string Read = "read", Add = "add", Modify = "modify", Delete = "delete"; public static readonly string[] All = { Read, Add, Modify, Delete }; }

public static class AccessExtensions
{
    /// <summary>
    /// 403 {"error":"forbidden"} unless the person may do this action in the module: on some company's data, or (allCompanies) on every company's data.
    /// Use allCompanies for endpoints that are not limited by company.
    /// </summary>
    public static RouteHandlerBuilder RequireAccess(this RouteHandlerBuilder b, string module, string action, bool allCompanies = false) =>
        b.AddEndpointFilter(async (ctx, next) =>
        {
            var h = ctx.HttpContext; var access = h.RequestServices.GetRequiredService<IAccess>();
            var ok = allCompanies ? await access.ScopeAsync(h.User, module, action, h.RequestAborted) == null
                                  : await access.AllowedAsync(h.User, module, action, null, h.RequestAborted);
            return ok ? await next(ctx) : Results.Json(new { error = "forbidden", module, action }, statusCode: 403);
        });
}

/// <summary>Sends e-mail for the current customer through the Mail module. Messages are queued and retried, never sent inside the caller's request.</summary>
public interface IMailQueue
{
    /// <summary>Queues a message. Returns false (and queues nothing) when the customer has not set up mail or the address is empty.</summary>
    Task<bool> EnqueueAsync(string to, string subject, string body, string? reference = null, CancellationToken ct = default);
    /// <summary>Queues a message written from one of the customer's mail templates (they can edit the wording). Values replace {{name}} in the template; a "path" value (for example "#/servicedesk/5") becomes a full {{link}}.</summary>
    Task<bool> EnqueueTemplateAsync(string template, string to, IReadOnlyDictionary<string, string> values, string? reference = null, CancellationToken ct = default);
    /// <summary>As EnqueueTemplateAsync, with files attached (for example a PDF report). Keep them small: 10 MB in all at most.</summary>
    Task<bool> EnqueueTemplateAsync(string template, string to, IReadOnlyDictionary<string, string> values, IReadOnlyList<MailFile> files, string? reference = null, CancellationToken ct = default);
}

/// <summary>A file attached to an outgoing message.</summary>
public sealed record MailFile(string Name, string ContentType, byte[] Data);

/// <summary>A file that came with an incoming message.</summary>
public sealed record InboundFile(string Name, string ContentType, byte[] Data);

/// <summary>A message read from the customer's support mailbox.</summary>
public sealed record InboundMail(string MessageId, string FromAddr, string FromName, string Subject, string Body, DateTimeOffset At, bool Auto = false, IReadOnlyList<InboundFile>? Files = null);

/// <summary>What the Service Desk does with incoming mail (the Mail module reads the mailbox and hands each message over).</summary>
public interface IInboundMailHandler
{
    /// <summary>Adds the message to the ticket it answers, or opens a ticket when automatic opening is on and the sender is a known customer. Returns the ticket number, or null when a person must decide.</summary>
    Task<string?> HandleAsync(InboundMail m, CancellationToken ct = default);
    /// <summary>Opens a ticket from the message. The company is optional.</summary>
    Task<string> CreateTicketAsync(InboundMail m, int? companyId, CancellationToken ct = default);
}

/// <summary>Free space on one disk or datastore. FreePct is 0 to 100.</summary>
public sealed record DiskUse(string Name, double FreePct, long FreeBytes, long TotalBytes);
/// <summary>A service check on a device: last result, failures in a row and, for certificates, days left.</summary>
public sealed record CheckState(int Id, string Name, string Type, bool? Ok, int Fails, double? CertDays, string Detail, DateTime? At);
/// <summary>A hardware part a controller reports as not OK (power supply, drive, fan, hypervisor host ...).</summary>
public sealed record PartFault(string Part, string Name, string State);
/// <summary>
/// One device in service as Monitoring sees it. The Assets module owns the data (agents, site controllers, service checks).
/// Kind is computer, server, network, printer or other. PingUp is null when no site controller watches it.
/// </summary>
public sealed record DeviceState(long Id, string Tag, string Name, int? CompanyId, string Company, string Kind, string Ip, string Os,
    bool HasAgent, string AgentVersion, DateTime? AgentSeen, bool? PingUp, DateTime? DownSince, int RttMs, DateTime? CheckedAt,
    DateTime? MetricsAt, double? Cpu, double? Mem, long MemTotal, long UptimeS, IReadOnlyList<DiskUse> Disks,
    IReadOnlyList<CheckState> Checks, IReadOnlyList<PartFault> Faults,
    IReadOnlyList<PortState>? Ports = null, DateTime? PortsAt = null, IReadOnlyList<WatchedItem>? Watched = null, DateTime? WatchedAt = null,
    IReadOnlyList<ServiceInfo>? Inventory = null, DateTime? InventoryAt = null, IReadOnlyDictionary<string, string>? ServiceModes = null)
{
    /// <summary>Every service the agent listed (empty before the first list).</summary>
    public IReadOnlyList<ServiceInfo> InventoryList => Inventory ?? Array.Empty<ServiceInfo>();
    /// <summary>What a person decided about a service: "watch" (also when it is not automatic) or "ignore". Keys are lower case.</summary>
    public IReadOnlyDictionary<string, string> Modes => ServiceModes ?? new Dictionary<string, string>();
    /// <summary>Ports of a network device (empty for other devices or before the first traffic reading).</summary>
    public IReadOnlyList<PortState> PortList => Ports ?? Array.Empty<PortState>();
    /// <summary>Services and programs the agent was asked to watch.</summary>
    public IReadOnlyList<WatchedItem> WatchedList => Watched ?? Array.Empty<WatchedItem>();
}

/// <summary>
/// One port of a network device with its latest traffic (percent of the port speed, bits per second, errors and discards per minute).
/// Watched ports are the ones whose going down matters (by default the ports with a description, such as uplinks).
/// </summary>
public sealed record PortState(string Index, string Name, string Alias, string Kind, bool Up, bool AdminUp, long Mbps, bool Rates,
    double InPct, double OutPct, double InBps, double OutBps, double Errors, double Discards, bool Watched, DateTime? DownSince);

/// <summary>One service of a computer as its service manager lists it. Start is auto, manual, disabled or other; State is running, stopped, starting, stopping, paused, failed or inactive.</summary>
public sealed record ServiceInfo(string Name, string Display, string State, string Start);

/// <summary>A service (Kind "service") or a program (Kind "process") the agent reports on.</summary>
public sealed record WatchedItem(string Name, string Kind, bool Running, string State, int Count);

/// <summary>A syslog message, SNMP trap or Windows event log entry from a device. Severity on the syslog scale (0 emergency ... 7 debug).</summary>
public sealed record DeviceEvent(long Id, long AssetId, DateTime At, string Kind, int Severity, string Source, int EventId, string Text);

/// <summary>Port traffic over a window, for "busy for N minutes": the lowest of the larger of in and out.</summary>
public sealed record PortSustained(double MinPct, int Samples, DateTime First);

/// <summary>Monthly figures of a device for reports.</summary>
public sealed record DeviceAverages(double? CpuAvg, double? CpuMax, double? MemAvg, double? MemMax, double? DiskFreeMin);

/// <summary>A point of a port's traffic chart.</summary>
public sealed record PortPoint(DateTime At, double InPct, double OutPct, double InMaxPct, double OutMaxPct, double InBps, double OutBps, double Errors);

/// <summary>What Monitoring wants an agent to report: services, programs and Windows event log errors. Implemented by Monitoring.</summary>
public sealed record AgentWatch(IReadOnlyList<string> Services, IReadOnlyList<string> Processes, bool Events);
public interface IAgentWatch
{
    Task<AgentWatch> ForAsync(long assetId, string kind, int? companyId, CancellationToken ct = default);
}

/// <summary>Actions on devices that other modules can ask the Assets module for (it owns the agents).</summary>
public interface IDeviceActions
{
    /// <summary>Asks the device's agent to restart a service. Returns the job id, or null when the device has no agent.</summary>
    Task<string?> RestartServiceAsync(long assetId, string service, string by, CancellationToken ct = default);
    /// <summary>
    /// Asks the device's agent to set an account's password (target local, domain or linux). The password is held sealed until the agent
    /// fetches it once. Returns a handle for <see cref="PasswordChangeAsync"/>, or null when the device has no agent or the wrong system.
    /// </summary>
    Task<string?> SetPasswordAsync(long assetId, string account, string target, string password, string by, CancellationToken ct = default);
    /// <summary>Where a password change stands: queued (not fetched yet), fetched (the agent has it), done or failed.</summary>
    Task<PasswordChange?> PasswordChangeAsync(string handle, CancellationToken ct = default);
    /// <summary>Withdraws a password change the agent has not fetched yet. True when it was withdrawn.</summary>
    Task<bool> CancelPasswordChangeAsync(string handle, CancellationToken ct = default);
}
public sealed record PasswordChange(string State, string Error, DateTime Created, DateTime? FetchedAt, DateTime? DoneAt);
/// <summary>The lowest processor and memory use over a time window (so "above 90% for 15 minutes" means the lowest was above 90).</summary>
public sealed record Sustained(double? MinCpu, double? MinMem, int Samples, DateTime First);
/// <summary>One point of a performance chart: minute values, or hourly average and peak.</summary>
public sealed record MetricPoint(DateTime At, double? Cpu, double? CpuMax, double? Mem, double? MemMax, double? DiskFreeMin);

/// <summary>Live state of devices for the Monitoring module, from the Assets module (read only).</summary>
public interface IDeviceHealth
{
    /// <summary>Every device in service (not retired or lost), limited to these companies (null = all), or one device.</summary>
    Task<IReadOnlyList<DeviceState>> SnapshotAsync(IReadOnlyList<int>? companies, long? assetId = null, CancellationToken ct = default);
    /// <summary>Per device, the lowest processor and memory use in the last minutes.</summary>
    Task<IReadOnlyDictionary<long, Sustained>> SustainedAsync(int minutes, CancellationToken ct = default);
    /// <summary>Chart points: minute values (kept two days) or hourly figures (kept 90 days).</summary>
    Task<IReadOnlyList<MetricPoint>> SeriesAsync(long assetId, DateTime from, DateTime to, bool hourly, CancellationToken ct = default);
    /// <summary>Per (device, port index), the lowest traffic over the last minutes.</summary>
    Task<IReadOnlyDictionary<(long, string), PortSustained>> SustainedPortsAsync(int minutes, CancellationToken ct = default);
    /// <summary>A port's traffic chart: 5-minute readings (kept two days) or hourly figures (kept 30 days).</summary>
    Task<IReadOnlyList<PortPoint>> PortSeriesAsync(long assetId, string port, DateTime from, DateTime to, bool hourly, CancellationToken ct = default);
    /// <summary>Marks a port as watched or not; null goes back to the default (watched when it has a description).</summary>
    Task SetPortWatchAsync(long assetId, string port, bool? watched, CancellationToken ct = default);
    /// <summary>Decides what to do with a service of a device: "watch", "ignore", or null to go back to the default (automatic services are watched on servers).</summary>
    Task SetServiceModeAsync(long assetId, string service, string? mode, CancellationToken ct = default);
    /// <summary>Events of the last minutes at this severity or worse (lower number), newest first, at most max.</summary>
    Task<IReadOnlyList<DeviceEvent>> RecentEventsAsync(int minutes, int maxSeverity, int max, CancellationToken ct = default);
    /// <summary>Events for the events page: limited to companies (null = all), optionally one device, one kind, a severity and a text.</summary>
    Task<(long Total, IReadOnlyList<DeviceEvent> Items)> ListEventsAsync(IReadOnlyList<int>? companies, long? assetId, string kind, int maxSeverity, string search, int skip, int take, CancellationToken ct = default);
    /// <summary>Per device, processor, memory and disk figures over a period (from the hourly figures).</summary>
    Task<IReadOnlyDictionary<long, DeviceAverages>> AveragesAsync(DateTime from, DateTime to, CancellationToken ct = default);
}

/// <summary>What another module asks the Service Desk to open.</summary>
public sealed record TicketRequest(string Subject, string Body, string Priority, int? CompanyId, long? AssetId, string AssetName, string Source, string CreatedBy);

/// <summary>Opening tickets from other modules (Monitoring alerts). Implemented by the Service Desk.</summary>
public interface ITicketDesk
{
    /// <summary>Opens a ticket with the customer's service levels. Returns its id and number (TKT-000123).</summary>
    Task<(long Id, string Number)> OpenAsync(TicketRequest r, CancellationToken ct = default);
    /// <summary>Adds a system note to the ticket (for example "the alert has cleared"). Does nothing for an unknown id.</summary>
    Task NoteAsync(long ticketId, string text, CancellationToken ct = default);
    /// <summary>The ticket's number and status, or null when it no longer exists.</summary>
    Task<(string Number, string Status)?> GetAsync(long ticketId, CancellationToken ct = default);
}

/// <summary>A site controller (the small program installed inside a customer's network).</summary>
public sealed record ControllerInfo(string Id, string Name, int? CompanyId, bool Online, string Version);

/// <summary>The answer to a job handed to a controller. Error is the controller's own description ({code, message, step}) when Ok is false.</summary>
public sealed record RelayReply(bool Ok, System.Text.Json.JsonElement? Result, System.Text.Json.JsonElement? Error, bool TimedOut);

/// <summary>
/// Hands a job to a customer's controller and waits for its answer (provided by the Assets module, which owns controllers).
/// The payload may hold secrets: it is sealed while it waits and removed as soon as the controller has taken it.
/// </summary>
public interface IControllerRelay
{
    /// <summary>Every controller (not removed), with whether it has checked in during the last three minutes.</summary>
    Task<IReadOnlyList<ControllerInfo>> ControllersAsync(CancellationToken ct = default);
    /// <summary>Queues the job for the controller and waits up to the timeout for its answer.</summary>
    Task<RelayReply> CallAsync(string controllerId, string kind, object payload, TimeSpan timeout, CancellationToken ct = default);
}
