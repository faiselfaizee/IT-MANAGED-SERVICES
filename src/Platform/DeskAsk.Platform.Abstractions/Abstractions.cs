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
}

/// <summary>The process roles one build can take (ADR 0004). Set with Platform:Roles, for example "web,worker".</summary>
[Flags]
public enum PlatformRole { None = 0, Web = 1, Worker = 2, Connect = 4, All = Web | Worker | Connect }

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
