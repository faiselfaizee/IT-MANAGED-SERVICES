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
}

/// <summary>A message read from the customer's support mailbox.</summary>
public sealed record InboundMail(string MessageId, string FromAddr, string FromName, string Subject, string Body, DateTimeOffset At, bool Auto = false);

/// <summary>What the Service Desk does with incoming mail (the Mail module reads the mailbox and hands each message over).</summary>
public interface IInboundMailHandler
{
    /// <summary>Adds the message to the ticket it answers, or opens a ticket when automatic opening is on and the sender is a known customer. Returns the ticket number, or null when a person must decide.</summary>
    Task<string?> HandleAsync(InboundMail m, CancellationToken ct = default);
    /// <summary>Opens a ticket from the message. The company is optional.</summary>
    Task<string> CreateTicketAsync(InboundMail m, int? companyId, CancellationToken ct = default);
}
