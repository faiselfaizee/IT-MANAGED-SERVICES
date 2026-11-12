using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.ServiceDesk;

/// <summary>Who is calling, as the Service Desk sees them: the role decides what they may do, and customers are tied to one company.</summary>
public sealed record Who(Guid Id, string Name, string Email, string Role, int? CompanyId, bool MayRaise, bool MayModify)
{
    public bool Staff => Role is "admin" or "engineer";
    public bool Customer => Role is "customer" or "cadmin";
    public bool Admin => Role == "admin";
}

/// <summary>
/// Service Desk (module "servicedesk"). Staff (admin, engineer) work every ticket, viewers read, customer users see their own company's tickets
/// and may raise and answer them. Internal notes never reach customers. Service levels pause while a ticket waits for the customer.
/// </summary>
public sealed class ServiceDeskModule : IPlatformModule
{
    public string Key => Modules.ServiceDesk;
    public string Schema => "servicedesk";

    public void AddServices(IServiceCollection s, IConfiguration cfg)
    {
        s.AddSingleton<ITenantSchema, ServiceDeskSchema>();
        s.AddSingleton<IJobHandler, SlaJob>();
        s.AddSingleton(new RecurringJob("servicedesk.sla", SlaJob.JobType, TimeSpan.FromMinutes(1)));
    }

    public void MapEndpoints(IEndpointRouteBuilder r)
    {
        TicketsApi.Map(r, Key);
        SettingsApi.Map(r, Key);
        ChangesApi.Map(r, Key);
    }

    // ---------------------------------------------------------------- shared helpers
    internal static IResult Bad(string error, string? detail = null) => Results.BadRequest(new { error, detail });
    internal static async Task<NpgsqlConnection> Open(ITenantContext t, CancellationToken ct) { var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct); return db; }
    internal static void P(NpgsqlCommand c, string n, object? v) => c.Parameters.AddWithValue(n, v ?? DBNull.Value);

    internal static async Task<Who> WhoAsync(ClaimsPrincipal u, NpgsqlConnection db, CancellationToken ct)
    {
        Guid.TryParse(u.FindFirstValue("sub"), out var id);
        var role = u.FindFirstValue("role") ?? "";
        int? company = null; bool raise = true, modify = true;
        if (id != Guid.Empty)
        {
            await using var c = new NpgsqlCommand("select company_id, may_raise, may_modify from servicedesk.people where user_id = @u", db); P(c, "u", id);
            await using var rd = await c.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct)) { company = rd.IsDBNull(0) ? null : rd.GetInt32(0); raise = rd.GetBoolean(1); modify = rd.GetBoolean(2); }
        }
        return new Who(id, u.FindFirstValue("name") ?? u.FindFirstValue("email") ?? "unknown", u.FindFirstValue("email") ?? "", role, company, raise, modify);
    }

    internal static async Task Event(NpgsqlConnection db, long ticket, string kind, object? data, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("insert into servicedesk.events (ticket_id, kind, data) values (@t, @k, @d)", db);
        P(c, "t", ticket); P(c, "k", kind); P(c, "d", JsonSerializer.Serialize(data ?? new { }));
        await c.ExecuteNonQueryAsync(ct);
    }
}
