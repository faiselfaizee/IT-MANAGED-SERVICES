using System.Collections.Concurrent;

namespace DeskAsk.Platform.Monitoring;

/// <summary>
/// Tells an agent which services and programs to report and whether to send Windows event log errors, from the rules that apply to its
/// device. Agents ask once a minute, so the rules are kept for 30 seconds per customer.
/// </summary>
public sealed class AgentWatchList : IAgentWatch
{
    static readonly ConcurrentDictionary<string, (DateTime At, List<Rule> Rules)> Cache = new();
    readonly ITenantContext tenant;
    public AgentWatchList(ITenantContext t) => tenant = t;

    /// <summary>Forget the cached rules of this customer (after a rule changes).</summary>
    public static void Forget(string tenantId) => Cache.TryRemove(tenantId, out _);

    public async Task<AgentWatch> ForAsync(long assetId, string kind, int? companyId, CancellationToken ct = default)
    {
        var t = tenant.Require();
        if (!Cache.TryGetValue(t.Id, out var hit) || DateTime.UtcNow - hit.At > TimeSpan.FromSeconds(30))
        {
            await using var db = await MonitoringModule.Open(tenant, ct);
            hit = (DateTime.UtcNow, (await RulesApi.LoadAsync(db, true, ct)).Where(r => r.Metric is "service" or "process" or "log").ToList());
            Cache[t.Id] = hit;
        }
        var mine = hit.Rules.Where(r => (r.CompanyId == null || r.CompanyId == companyId) && (r.Kinds.Length == 0 || r.Kinds.Contains(kind))).ToList();
        return new AgentWatch(
            mine.Where(r => r.Metric == "service").SelectMany(r => r.Names).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList(),
            mine.Where(r => r.Metric == "process").SelectMany(r => r.Names).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList(),
            mine.Any(r => r.Metric == "log" && r.EventKind is "" or "eventlog"));
    }
}
