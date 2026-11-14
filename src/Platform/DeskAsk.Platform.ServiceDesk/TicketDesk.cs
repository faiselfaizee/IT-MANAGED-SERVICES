using Npgsql;

namespace DeskAsk.Platform.ServiceDesk;

/// <summary>Tickets opened by other modules (Monitoring alerts): the customer's service levels apply, the source names the module.</summary>
public sealed class TicketDesk : ITicketDesk
{
    readonly ITenantContext tenant; readonly ISettings settings; readonly ICompanyDirectory companies;
    public TicketDesk(ITenantContext t, ISettings s, ICompanyDirectory c) { tenant = t; settings = s; companies = c; }
    async Task<NpgsqlConnection> Open(CancellationToken ct) { var db = new NpgsqlConnection(tenant.Require().ConnectionString); await db.OpenAsync(ct); return db; }

    public async Task<(long Id, string Number)> OpenAsync(TicketRequest r, CancellationToken ct = default)
    {
        var prio = SlaConfig.Prios.Contains(r.Priority) ? r.Priority : "medium";
        var companyName = r.CompanyId is int ci ? (await companies.GetAsync(ci, ct))?.Name ?? "" : "";
        var sla = await SlaConfig.LoadAsync(settings, ct);
        await using var db = await Open(ct);
        await using var c = new NpgsqlCommand(@"insert into servicedesk.tickets
            (subject, description, priority, company_id, company_name, asset_id, asset_name, requester_name, source, created_by, respond_due, resolve_due)
            values (@s, @d, @p, @co, @con, @a, @an, @src, @src, @by, now() + make_interval(secs => @rp * 3600), now() + make_interval(secs => @rs * 3600)) returning id, number", db);
        ServiceDeskModule.P(c, "s", r.Subject.Length > 200 ? r.Subject[..200] : r.Subject); ServiceDeskModule.P(c, "d", r.Body); ServiceDeskModule.P(c, "p", prio);
        ServiceDeskModule.P(c, "co", r.CompanyId); ServiceDeskModule.P(c, "con", companyName); ServiceDeskModule.P(c, "a", r.AssetId); ServiceDeskModule.P(c, "an", r.AssetName);
        ServiceDeskModule.P(c, "src", r.Source); ServiceDeskModule.P(c, "by", r.CreatedBy); ServiceDeskModule.P(c, "rp", sla.Respond[prio]); ServiceDeskModule.P(c, "rs", sla.Resolve[prio]);
        long id; string number;
        await using (var rd = await c.ExecuteReaderAsync(ct)) { await rd.ReadAsync(ct); id = rd.GetInt64(0); number = rd.GetString(1); }
        await ServiceDeskModule.Event(db, id, "ticket.created", new { number, priority = prio, company = r.CompanyId, source = r.Source }, ct);
        return (id, number);
    }

    public async Task NoteAsync(long ticketId, string text, CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var c = new NpgsqlCommand("insert into servicedesk.comments (ticket_id, kind, author_name, body) select id, 'system', 'Monitoring', @b from servicedesk.tickets where id = @t; update servicedesk.tickets set updated_at = now() where id = @t", db);
        ServiceDeskModule.P(c, "t", ticketId); ServiceDeskModule.P(c, "b", text); await c.ExecuteNonQueryAsync(ct);
    }

    public async Task<(string Number, string Status)?> GetAsync(long ticketId, CancellationToken ct = default)
    {
        await using var db = await Open(ct);
        await using var c = new NpgsqlCommand("select number, status from servicedesk.tickets where id = @t", db); ServiceDeskModule.P(c, "t", ticketId);
        await using var rd = await c.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct) ? (rd.GetString(0), rd.GetString(1)) : null;
    }
}
