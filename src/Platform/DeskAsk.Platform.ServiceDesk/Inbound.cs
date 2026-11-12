using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.ServiceDesk;

/// <summary>
/// What the Service Desk does with mail from the support mailbox. A message whose subject carries a ticket number and which comes from the
/// requester, a colleague in the same company or a staff member is added to that ticket as a reply. Anything else waits in the inbox for a person,
/// unless automatic opening is on and the sender is a known customer user tied to a company.
/// </summary>
public sealed class InboundHandler : IInboundMailHandler
{
    static readonly Regex Ref = new(@"\bTKT-\d{6}\b", RegexOptions.IgnoreCase);
    readonly ITenantContext tenant; readonly ISettings settings; readonly IUserDirectory users; readonly ICompanyDirectory companies;
    public InboundHandler(ITenantContext t, ISettings s, IUserDirectory u, ICompanyDirectory c) { tenant = t; settings = s; users = u; companies = c; }

    async Task<bool> AutoCreate(CancellationToken ct)
    {
        try { var raw = await settings.GetAsync("mail.inbound", ct); return raw != null && JsonDocument.Parse(raw).RootElement.TryGetProperty("autoCreate", out var v) && v.ValueKind == JsonValueKind.True; }
        catch (JsonException) { return false; }
    }

    async Task<(UserRef? user, int? company)> Sender(NpgsqlConnection db, string email, CancellationToken ct)
    {
        var u = (await users.ListAsync(ct)).FirstOrDefault(x => x.Active && string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase));
        if (u == null) return (null, null);
        await using var c = new NpgsqlCommand("select company_id from servicedesk.people where user_id = @u", db); ServiceDeskModule.P(c, "u", u.Id);
        return (u, await c.ExecuteScalarAsync(ct) is int ci ? ci : null);
    }

    public async Task<string?> HandleAsync(InboundMail m, CancellationToken ct = default)
    {
        await using var db = new NpgsqlConnection(tenant.Require().ConnectionString); await db.OpenAsync(ct);
        var (user, company) = await Sender(db, m.FromAddr, ct);
        if (Ref.Match(m.Subject) is { Success: true } r)
        {
            var number = r.Value.ToUpperInvariant();
            long id = 0; string status = "", requester = ""; int? ticketCompany = null; var found = false;
            await using (var c = new NpgsqlCommand("select id, status, requester_email, company_id from servicedesk.tickets where number = @n", db))
            {
                ServiceDeskModule.P(c, "n", number);
                await using var rd = await c.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct)) { found = true; id = rd.GetInt64(0); status = rd.GetString(1); requester = rd.GetString(2); ticketCompany = rd.IsDBNull(3) ? null : rd.GetInt32(3); }
            }
            var staff = user?.Role is "admin" or "engineer";
            var mine = string.Equals(requester, m.FromAddr, StringComparison.OrdinalIgnoreCase) || (user != null && company != null && company == ticketCompany);
            if (found && status != "closed" && (staff || mine) && m.Body.Trim() != "")
            {
                await using (var c = new NpgsqlCommand("insert into servicedesk.comments (ticket_id, author_id, author_name, kind, body) values (@t, @a, @n, 'public', @b)", db))
                { ServiceDeskModule.P(c, "t", id); ServiceDeskModule.P(c, "a", user?.Id); ServiceDeskModule.P(c, "n", user?.Name ?? (m.FromName != "" ? m.FromName : m.FromAddr)); ServiceDeskModule.P(c, "b", m.Body); await c.ExecuteNonQueryAsync(ct); }
                if (staff)
                {
                    await using var c = new NpgsqlCommand("update servicedesk.tickets set first_response_at = coalesce(first_response_at, now()), updated_at = now() where id = @i", db); ServiceDeskModule.P(c, "i", id); await c.ExecuteNonQueryAsync(ct);
                }
                else if (status is "hold" or "resolved")
                {   // the customer answered: work continues and the clock runs again
                    var sla = await SlaConfig.LoadAsync(settings, ct);
                    await using (var c = new NpgsqlCommand("update servicedesk.tickets set status = 'open', paused_s = paused_s + coalesce(extract(epoch from now() - paused_at)::bigint, 0), paused_at = null, resolved_at = null, updated_at = now() where id = @i", db)) { ServiceDeskModule.P(c, "i", id); await c.ExecuteNonQueryAsync(ct); }
                    await using (var d = new NpgsqlCommand($"update servicedesk.tickets set respond_due = {SlaConfig.RespondDue}, resolve_due = {SlaConfig.ResolveDue} where id = @i", db)) { sla.Bind(d); ServiceDeskModule.P(d, "i", id); await d.ExecuteNonQueryAsync(ct); }
                    await using var n = new NpgsqlCommand("insert into servicedesk.comments (ticket_id, kind, author_name, body) values (@t, 'system', 'System', @b)", db); ServiceDeskModule.P(n, "t", id); ServiceDeskModule.P(n, "b", $"Status: {status} → open (the customer replied by mail)."); await n.ExecuteNonQueryAsync(ct);
                }
                else
                {
                    await using var c = new NpgsqlCommand("update servicedesk.tickets set updated_at = now() where id = @i", db); ServiceDeskModule.P(c, "i", id); await c.ExecuteNonQueryAsync(ct);
                }
                await ServiceDeskModule.Event(db, id, "ticket.comment", new { by = m.FromAddr, customer = !staff, mail = true }, ct);
                return number;
            }
            return null;   // unknown ticket, closed ticket or a stranger: a person decides
        }
        if (user != null && company != null && user.Role is "customer" or "cadmin" && await AutoCreate(ct)) return await CreateTicketAsync(m, company, ct);
        return null;
    }

    public async Task<string> CreateTicketAsync(InboundMail m, int? companyId, CancellationToken ct = default)
    {
        await using var db = new NpgsqlConnection(tenant.Require().ConnectionString); await db.OpenAsync(ct);
        var (user, userCompany) = await Sender(db, m.FromAddr, ct);
        var company = companyId ?? (user?.Role is "customer" or "cadmin" ? userCompany : null);
        var companyName = "";
        if (company is int ci) { var co = await companies.GetAsync(ci, ct); if (co == null) throw new InvalidOperationException("That company does not exist."); companyName = co.Name; }
        var sla = await SlaConfig.LoadAsync(settings, ct);
        var subject = Ref.Replace(m.Subject, "").Trim(' ', '[', ']', '-', ':'); if (subject == "") subject = "(no subject)"; if (subject.Length > 200) subject = subject[..200];
        await using var c = new NpgsqlCommand(@"insert into servicedesk.tickets
            (subject, description, priority, company_id, company_name, requester_id, requester_name, requester_email, source, created_by, respond_due, resolve_due)
            values (@s, @d, 'medium', @co, @con, @ri, @rn, @re, 'mail', 'mail', now() + make_interval(secs => @rp * 3600), now() + make_interval(secs => @rs * 3600)) returning id, number", db);
        ServiceDeskModule.P(c, "s", subject); ServiceDeskModule.P(c, "d", m.Body); ServiceDeskModule.P(c, "co", company); ServiceDeskModule.P(c, "con", companyName);
        ServiceDeskModule.P(c, "ri", user?.Id); ServiceDeskModule.P(c, "rn", user?.Name ?? (m.FromName != "" ? m.FromName : m.FromAddr)); ServiceDeskModule.P(c, "re", m.FromAddr);
        ServiceDeskModule.P(c, "rp", sla.Respond["medium"]); ServiceDeskModule.P(c, "rs", sla.Resolve["medium"]);
        long id; string number;
        await using (var rd = await c.ExecuteReaderAsync(ct)) { await rd.ReadAsync(ct); id = rd.GetInt64(0); number = rd.GetString(1); }
        await ServiceDeskModule.Event(db, id, "ticket.created", new { number, priority = "medium", company, mail = true }, ct);
        return number;
    }
}
