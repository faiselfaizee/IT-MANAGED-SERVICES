using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;

namespace DeskAsk.Platform.Assets;

/// <summary>
/// The Assistant (6.23.0): plain-English questions about assets, tickets, alerts and the links between assets, answered from the person's own data
/// with a table and links, never from outside. It is rules, not a language model: the question is matched to what DeskAsk can answer, and
/// what it cannot answer it says so and offers examples. Everything is limited to the customers the person may see.
/// </summary>
public static class AssistantApi
{
    public sealed class Tbl
    {
        public string Title { get; set; } = "";
        public List<string> Headers { get; set; } = new();
        public List<List<string>> Rows { get; set; } = new();
        /// <summary>One link per row (the first column opens it); empty when the rows do not link anywhere.</summary>
        public List<string> Links { get; set; } = new();
        public bool More { get; set; }
    }
    public sealed class Ans
    {
        public string Text { get; set; } = "";
        public int? Count { get; set; }
        public List<Tbl> Tables { get; set; } = new();
        public List<string> Suggestions { get; set; } = new();
    }

    const int Limit = 100;
    static readonly string[] Examples =
    {
        "How many servers do we have?", "Which assets have a warranty ending in 60 days?", "Which computers are low on disk space?",
        "Which devices are down?", "Show open critical alerts", "Show overdue tickets", "Tickets with no engineer", "Which computers have Chrome installed?",
        "What is affected if the main switch fails?", "Computers not reporting for 7 days", "What is assigned to Ravi?"
    };

    static bool Has(string q, params string[] words) => words.Any(w => q.Contains(w, StringComparison.Ordinal));
    static int Num(string q, int dflt, int max)
    {
        var m = Regex.Match(q, @"(\d{1,4})\s*(?:day|days|d\b|%|percent|per cent)");
        if (!m.Success) m = Regex.Match(q, @"\b(?:in|within|under|below|than|over|last|past|for)\s+(\d{1,4})\b");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) && n > 0 ? Math.Min(n, max) : dflt;
    }

    static async Task<List<string[]>> Rows(NpgsqlConnection db, string sql, Action<NpgsqlCommand> bind, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand(sql, db); bind(c);
        var rows = new List<string[]>();
        await using var rd = await c.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var r = new string[rd.FieldCount];
            for (var i = 0; i < r.Length; i++) r[i] = rd.IsDBNull(i) ? "" : Convert.ToString(rd.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            rows.Add(r);
        }
        return rows;
    }

    /// <summary>Turns query rows (first column the id) into a table whose rows open <paramref name="href"/> + id.</summary>
    static Tbl Make(string title, string[] headers, List<string[]> rows, string? href)
    {
        var t = new Tbl { Title = title, Headers = headers.ToList(), More = rows.Count > Limit };
        foreach (var r in rows.Take(Limit)) { t.Rows.Add(r.Skip(1).ToList()); t.Links.Add(href == null ? "" : href + r[0]); }
        return t;
    }

    const string AssetFrom = @"from assets.assets a left join assets.companies co on co.id = a.company_id left join assets.types ty on ty.id = a.type_id
        where a.status not in ('retired', 'lost') and (not @sc or a.company_id = any(@scope))";

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        r.MapPost("/api/assets/assistant/ask", async (HttpContext http, ITenantContext t, JsonElement b, CancellationToken ct) =>
        {
            var raw = (b.ValueKind == JsonValueKind.Object && b.TryGetProperty("q", out var qp) && qp.ValueKind == JsonValueKind.String ? qp.GetString() : null)?.Trim() ?? "";
            if (raw.Length == 0 || raw.Length > 300) return Results.BadRequest(new { error = "Type a question of up to 300 characters." });
            var scope = await AssetsAccess.ScopeAsync(http, Acts.Read);
            var me = http.User.FindFirst("sub")?.Value ?? "";
            await using var db = await AgentsApi.Open(t, ct);
            try { return Results.Ok(await Answer(db, raw, scope, me, ct)); }
            catch (PostgresException e) when (e.SqlState == "42P01") { return Results.Ok(new Ans { Text = "That part of DeskAsk is not set up here yet, so there is nothing to answer from." }); }
        }).RequireAuthorization(p => p.RequireRole(AssetsAccess.Readers)).RequireModule(key).RequireAccess(key, Acts.Read);
    }

    static async Task<Ans> Answer(NpgsqlConnection db, string raw, IReadOnlyList<int>? scope, string me, CancellationToken ct)
    {
        var q = Regex.Replace(raw.ToLowerInvariant().Replace('’', '\''), @"\s+", " ").Trim().TrimEnd('?', '.', '!');
        void Sc(NpgsqlCommand c) => AssetsAccess.Bind(c, scope);
        var a = new Ans();
        string Term(params string[] lead)
        {
            var m = Regex.Match(q, @"\b(?:" + string.Join("|", lead) + @")\s+(?:the\s+|my\s+|our\s+)?(.{2,80})$");
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        if (Regex.IsMatch(q, @"^(help|what can you do|what do you do|examples?|hi|hello)\b")) { a.Text = "I answer questions about your assets, tickets, alerts and how assets depend on each other. Try one of these."; a.Suggestions = Examples.ToList(); return a; }

        // what depends on / is affected by an asset
        if (Has(q, "depend", "impact", "affected", "affect", "what breaks", "what happens if", "fails", "goes down"))
        {
            var name = Term("on", "if", "of", "by", "for", "when");
            name = Regex.Replace(name, @"\s+(fails?|goes down|is down|stops|breaks|crashes)$", "").Trim();
            if (name.Length < 2) { a.Text = "Which asset? For example: what is affected if SW-CORE-01 fails?"; a.Suggestions = new() { "What is affected if the main switch fails?" }; return a; }
            var hit = await FindAsset(db, name, Sc, ct);
            if (hit == null) { a.Text = $"I could not find an asset called \"{name}\"."; return a; }
            var rows = await Rows(db, @"
                with recursive edge(src, dst, kind) as (
                  select to_id, from_id, kind from assets.relations where kind in ('runs_on', 'depends_on', 'part_of')
                  union all select to_id, from_id, kind from assets.relations where kind = 'connected_to'
                  union all select from_id, to_id, kind from assets.relations where kind = 'connected_to'
                ), walk(id, depth, path) as (
                  select e.dst, 1, array[@i::bigint, e.dst] from edge e where e.src = @i
                  union all select e.dst, w.depth + 1, w.path || e.dst from walk w join edge e on e.src = w.id where w.depth < 4 and e.dst <> all(w.path)
                )
                select a.id, a.tag, a.name, coalesce(co.name, ''), min(w.depth)::text
                  from walk w join assets.assets a on a.id = w.id left join assets.companies co on co.id = a.company_id
                 where a.id <> @i and (not @sc or a.company_id = any(@scope)) group by a.id, a.tag, a.name, co.name order by min(w.depth), lower(a.name) limit 101",
                c => { c.Parameters.AddWithValue("i", hit.Value.id); Sc(c); }, ct);
            a.Count = Math.Min(rows.Count, Limit);
            a.Text = rows.Count == 0 ? $"Nothing is recorded as depending on {hit.Value.name}. Add links on its Relationships tab and I can follow them." : $"If {hit.Value.name} fails, {(rows.Count > Limit ? "more than " + Limit : rows.Count.ToString())} other asset(s) are affected, nearest first.";
            if (rows.Count > 0) a.Tables.Add(Make("Affected assets", new[] { "Tag", "Name", "Customer", "Steps away" }, rows, "#/assets/"));
            return a;
        }

        // tickets
        if (Has(q, "ticket", "incident", "service request", "breach", "overdue", "unassigned", "no engineer") || Regex.IsMatch(q, @"\bsla\b"))
        {
            var where = new List<string> { "(not @sc or k.company_id = any(@scope))" };
            string title; var open = "k.status not in ('resolved', 'closed')";
            if (Has(q, "overdue", "breach", "past due", "late", "missed")) { where.Add(open); where.Add("k.resolve_due < now()"); title = "Overdue tickets"; }
            else if (Has(q, "due soon", "about to", "due today", "will breach", "at risk", "nearly")) { where.Add(open); where.Add("k.resolve_due between now() and now() + interval '4 hours'"); title = "Tickets due in the next 4 hours"; }
            else if (Has(q, "unassigned", "no engineer", "not assigned", "no one", "nobody", "without an engineer", "no assignee")) { where.Add(open); where.Add("k.assignee_id is null"); title = "Tickets with no engineer"; }
            else if (Has(q, "my tickets", "assigned to me", "mine", "my open")) { where.Add(open); where.Add("k.assignee_id::text = @me"); title = "Your open tickets"; }
            else if (Has(q, "resolved", "closed", "solved")) { where.Add("k.status in ('resolved', 'closed')"); where.Add("k.updated_at > now() - interval '30 days'"); title = "Tickets resolved or closed in the last 30 days"; }
            else if (Has(q, "hold", "waiting")) { where.Add("k.status = 'hold'"); title = "Tickets on hold"; }
            else { where.Add(open); title = "Open tickets"; }
            foreach (var p in new[] { "critical", "high", "medium", "low" }) if (Regex.IsMatch(q, @"\b" + p + @"\b") && !q.Contains(p + " alert", StringComparison.Ordinal)) { where.Add("k.priority = '" + p + "'"); title += $" ({p} priority)"; break; }
            var who = Term("for", "from", "of", "about", "on");
            if (who.Length >= 2 && !Has(who, "ticket", "me", "today", "week", "month")) { where.Add("(k.asset_name ilike @w or k.company_name ilike @w or k.requester_name ilike @w or k.requester_email ilike @w or k.subject ilike @w)"); title += $" matching \"{who}\""; }
            var rows = await Rows(db, $@"select k.id, k.number, k.subject, k.priority, k.status, k.assignee_name, k.company_name, coalesce(to_char(k.resolve_due, 'YYYY-MM-DD HH24:MI'), '')
                from servicedesk.tickets k where {string.Join(" and ", where)} order by (k.priority = 'critical') desc, k.resolve_due nulls last, k.created_at desc limit 101",
                c => { Sc(c); c.Parameters.AddWithValue("me", me); c.Parameters.AddWithValue("w", "%" + who + "%"); }, ct);
            a.Count = Math.Min(rows.Count, Limit);
            a.Text = rows.Count == 0 ? $"{title}: none." : $"{title}: {(rows.Count > Limit ? "more than " + Limit : rows.Count.ToString())}.";
            if (rows.Count > 0) a.Tables.Add(Make(title, new[] { "Number", "Subject", "Priority", "Status", "Engineer", "Customer", "Due" }, rows, "#/servicedesk/"));
            a.Suggestions = new() { "Show overdue tickets", "Tickets with no engineer", "Show open critical tickets" };
            return a;
        }

        // alerts
        if (Has(q, "alert") || (Has(q, "critical", "warning") && Has(q, "monitor", "alarm")))
        {
            var sev = Has(q, "critical") ? "critical" : Has(q, "warning") ? "warning" : "";
            var rows = await Rows(db, @"select al.asset_id, al.asset_name, coalesce(nullif(al.company_name, ''), ''), al.severity, al.rule_name, al.text, to_char(al.opened_at, 'YYYY-MM-DD HH24:MI')
                from monitoring.alerts al where al.closed_at is null and (not @sc or al.company_id = any(@scope)) and (@sev = '' or al.severity = @sev) order by (al.severity = 'critical') desc, al.opened_at desc limit 101",
                c => { Sc(c); c.Parameters.AddWithValue("sev", sev); }, ct);
            a.Count = Math.Min(rows.Count, Limit);
            a.Text = rows.Count == 0 ? $"No open {sev} alerts. Everything monitored is quiet.".Replace("  ", " ") : $"{(rows.Count > Limit ? "More than " + Limit : rows.Count.ToString())} open {sev} alert(s), the most serious first.".Replace("  ", " ");
            if (rows.Count > 0) a.Tables.Add(Make("Open alerts", new[] { "Device", "Customer", "Severity", "Rule", "What happened", "Since" }, rows, "#/monitoring/device/"));
            return a;
        }

        // devices that are down
        if ((Regex.IsMatch(q, @"\bdown\b") || Has(q, "offline", "unreachable", "not responding", "not pinging")) && !Has(q, "agent", "reporting"))
        {
            var rows = await Rows(db, "select a.id, a.tag, a.name, coalesce(co.name, ''), a.ip_address, coalesce(to_char(a.checked_at, 'YYYY-MM-DD HH24:MI'), '') " + AssetFrom + " and a.monitor <> '' and a.check_up = false order by lower(a.name) limit 101", Sc, ct);
            a.Count = Math.Min(rows.Count, Limit);
            a.Text = rows.Count == 0 ? "Nothing that is monitored is down right now." : $"{(rows.Count > Limit ? "More than " + Limit : rows.Count.ToString())} monitored device(s) are down.";
            if (rows.Count > 0) a.Tables.Add(Make("Devices that are down", new[] { "Tag", "Name", "Customer", "Address", "Last checked" }, rows, "#/monitoring/device/"));
            return a;
        }

        // warranty
        if (Has(q, "warranty", "amc", "expire", "expiring", "out of support"))
        {
            var days = Num(q, 60, 3650); var expired = Regex.IsMatch(q, @"\b(expired|out of warranty|ended|over)\b");
            var rows = await Rows(db, @"select a.id, a.tag, a.name, coalesce(ty.name, ''), coalesce(co.name, ''), to_char(a.warranty_end, 'YYYY-MM-DD'), (a.warranty_end - current_date)::text " + AssetFrom +
                (expired ? " and a.warranty_end < current_date" : " and a.warranty_end between current_date and current_date + @d") + " order by a.warranty_end " + (expired ? "desc" : "") + " limit 101",
                c => { Sc(c); c.Parameters.AddWithValue("d", days); }, ct);
            var title = expired ? "Warranty already ended" : $"Warranty ending in the next {days} days";
            a.Count = Math.Min(rows.Count, Limit); a.Text = rows.Count == 0 ? $"{title}: none." : $"{title}: {(rows.Count > Limit ? "more than " + Limit : rows.Count.ToString())} asset(s).";
            if (rows.Count > 0) a.Tables.Add(Make(title, new[] { "Tag", "Name", "Type", "Customer", "Warranty ends", "Days left" }, rows, "#/assets/"));
            a.Suggestions = new() { "Which assets are out of warranty?", "Warranty ending in 30 days" };
            return a;
        }

        // low disk
        if (Has(q, "disk", "storage", "drive", "space", "full"))
        {
            var pct = Num(q, 15, 99);
            var rows = await Rows(db, @"select a.id, a.name, coalesce(co.name, ''), coalesce(nullif(p->>'mount', ''), p->>'name', ''),
                    round(100.0 * coalesce((p->>'free_bytes')::bigint, 0) / (p->>'size_bytes')::bigint)::text || ' %', round(coalesce((p->>'free_bytes')::bigint, 0) / 1073741824.0, 1)::text
                from assets.assets a left join assets.companies co on co.id = a.company_id
                cross join lateral jsonb_array_elements(case when jsonb_typeof(a.disks) = 'array' then a.disks else '[]' end) d
                cross join lateral jsonb_array_elements(case when jsonb_typeof(d->'partitions') = 'array' then d->'partitions' else '[]' end) p
                where a.status not in ('retired', 'lost') and (not @sc or a.company_id = any(@scope)) and coalesce((p->>'size_bytes')::bigint, 0) > 0
                  and 100.0 * coalesce((p->>'free_bytes')::bigint, 0) / (p->>'size_bytes')::bigint < @p
                order by 100.0 * coalesce((p->>'free_bytes')::bigint, 0) / (p->>'size_bytes')::bigint, lower(a.name) limit 101",
                c => { Sc(c); c.Parameters.AddWithValue("p", (double)pct); }, ct);
            a.Count = Math.Min(rows.Count, Limit); a.Text = rows.Count == 0 ? $"No partition has less than {pct} % free." : $"{(rows.Count > Limit ? "More than " + Limit : rows.Count.ToString())} partition(s) have less than {pct} % free, fullest first.";
            if (rows.Count > 0) a.Tables.Add(Make("Low disk space", new[] { "Name", "Customer", "Partition", "Free", "Free (GB)" }, rows, "#/assets/"));
            return a;
        }

        // antivirus
        if (Has(q, "antivirus", "anti-virus", "protected", "unprotected", "defender", "endpoint protection"))
        {
            var rows = await Rows(db, "select a.id, a.tag, a.name, coalesce(co.name, ''), coalesce(nullif(a.antivirus_status, ''), 'No data') " + AssetFrom + " and a.agent_id <> '' and coalesce(a.antivirus_status, '') <> 'Protected' order by lower(a.name) limit 101", Sc, ct);
            a.Count = Math.Min(rows.Count, Limit); a.Text = rows.Count == 0 ? "Every computer that reports is protected." : $"{(rows.Count > Limit ? "More than " + Limit : rows.Count.ToString())} computer(s) are not reported as protected.";
            if (rows.Count > 0) a.Tables.Add(Make("Antivirus not protected", new[] { "Tag", "Name", "Customer", "Status" }, rows, "#/assets/"));
            return a;
        }

        // agents not reporting
        if (Has(q, "not reporting", "stopped reporting", "not seen", "haven't reported", "have not reported", "no agent", "agent offline", "stale", "inactive", "not check"))
        {
            var days = Num(q, 7, 3650);
            var rows = await Rows(db, @"select a.id, a.tag, a.name, coalesce(co.name, ''), coalesce(to_char(a.last_seen, 'YYYY-MM-DD HH24:MI'), 'Never'), coalesce((current_date - a.last_seen::date)::text, '') " + AssetFrom +
                " and a.agent_id <> '' and (a.last_seen is null or a.last_seen < now() - make_interval(days => @d)) order by a.last_seen nulls first limit 101", c => { Sc(c); c.Parameters.AddWithValue("d", days); }, ct);
            a.Count = Math.Min(rows.Count, Limit); a.Text = rows.Count == 0 ? $"Every agent has reported in the last {days} days." : $"{(rows.Count > Limit ? "More than " + Limit : rows.Count.ToString())} computer(s) have not reported for {days} days.";
            if (rows.Count > 0) a.Tables.Add(Make("Not reporting", new[] { "Tag", "Name", "Customer", "Last seen", "Days" }, rows, "#/assets/"));
            return a;
        }

        // who has a program installed
        if (Has(q, "install", "software", "program", "application", "app ") || Regex.IsMatch(q, @"^(who|which (computers|machines|pcs|laptops|servers|devices)) (has|have|with|running)"))
        {
            var prog = Regex.Replace(Term("has", "have", "with", "running", "installed", "using", "of"), @"\s+(installed|software|program|application|on them)$", "").Trim();
            prog = Regex.Replace(prog, @"^(installed|software|the)\s+", "").Trim();
            if (prog.Length < 2) { a.Text = "Which program? For example: which computers have Chrome installed?"; return a; }
            var rows = await Rows(db, @"select a.id, a.tag, a.name, coalesce(co.name, ''), (select e->>'name' || coalesce(' ' || nullif(e->>'version', ''), '') from jsonb_array_elements(case when jsonb_typeof(a.installed_apps) = 'array' then a.installed_apps else '[]'::jsonb end) e
                                                                   where coalesce(e->>'name', '') ilike @p limit 1) " + AssetFrom +
                " and exists (select 1 from jsonb_array_elements(case when jsonb_typeof(a.installed_apps) = 'array' then a.installed_apps else '[]'::jsonb end) e where coalesce(e->>'name', '') ilike @p) order by lower(a.name) limit 101",
                c => { Sc(c); c.Parameters.AddWithValue("p", "%" + prog.Replace("%", "").Replace("_", "") + "%"); }, ct);
            a.Count = Math.Min(rows.Count, Limit); a.Text = rows.Count == 0 ? $"No computer reports a program like \"{prog}\"." : $"{(rows.Count > Limit ? "More than " + Limit : rows.Count.ToString())} computer(s) have \"{prog}\".";
            if (rows.Count > 0) a.Tables.Add(Make($"Computers with {prog}", new[] { "Tag", "Name", "Customer", "Program" }, rows, "#/assets/"));
            return a;
        }

        // counts and lists of assets: by type, system or status
        var types = await Rows(db, "select id, name from assets.types", _ => { }, ct);
        string Stem(string s) { s = s.ToLowerInvariant().Trim(); return s.EndsWith("ies") ? s[..^3] + "y" : s.EndsWith("s") && s.Length > 3 ? s[..^1] : s; }
        var qStems = Regex.Split(q, @"[^a-z0-9]+").Where(w => w.Length > 1).Select(Stem).ToHashSet();
        var type = types.FirstOrDefault(x => Stem(x[1]).Split(' ').All(qStems.Contains));
        var os = Has(q, "windows") ? "windows" : Has(q, "linux", "ubuntu", "centos", "debian", "rhel") ? "linux" : Regex.IsMatch(q, @"\bmac(os)?\b") ? "mac" : "";
        var status = Has(q, "in stock", "stock") ? "stock" : Has(q, "in repair", "repair") ? "repair" : Has(q, "in use") ? "in_use" : "";
        var counting = Regex.IsMatch(q, @"\bhow many\b|\bcount\b|\bnumber of\b|\btotal\b");
        var looksLikeName = Regex.IsMatch(q, @"[a-z]+[-_.]\d|\d{3,}");
        if (counting || (type != null && !looksLikeName) || os != "" || status != "" || Regex.IsMatch(q, @"^(list|show) (me )?(all|every)( the)? (assets|devices|computers)"))
        {
            var extra = ""; var label = "assets";
            if (type != null) { extra += " and a.type_id = @ty"; label = type[1].ToLowerInvariant(); }
            if (os != "") { extra += " and (a.os_family ilike @os or a.os_name ilike @os2)"; label = (type != null ? label : "computers") + " running " + os; }
            if (status != "") { extra += " and a.status = @st"; label += " (" + status.Replace('_', ' ') + ")"; }
            var rows = await Rows(db, "select a.id, a.tag, a.name, coalesce(ty.name, ''), coalesce(co.name, ''), a.status, a.ip_address, a.assigned_to " + AssetFrom + extra + " order by lower(a.name) limit 1001",
                c => { Sc(c); if (type != null) c.Parameters.AddWithValue("ty", int.Parse(type[0])); c.Parameters.AddWithValue("os", os + "%"); c.Parameters.AddWithValue("os2", "%" + os + "%"); c.Parameters.AddWithValue("st", status); }, ct);
            a.Count = Math.Min(rows.Count, 1000);
            a.Text = counting ? $"You have {(rows.Count > 1000 ? "more than 1000" : rows.Count.ToString())} {label}." : $"{(rows.Count > Limit ? "More than " + Limit : rows.Count.ToString())} {label}{(rows.Count > Limit ? ", the first " + Limit + " are listed" : "")}.";
            if (rows.Count > 0 && !(counting && rows.Count > Limit * 5)) a.Tables.Add(Make(char.ToUpper(label[0]) + label[1..], new[] { "Tag", "Name", "Type", "Customer", "Status", "Address", "Assigned to" }, rows, "#/assets/"));
            return a;
        }

        // what is assigned to a person
        var person = Regex.Match(q, @"(?:assigned to|belongs to|used by|owned by)\s+([a-z0-9@.' \-]{2,60})$");
        var possessive = Regex.Match(q, @"^(?:what|which|show|list)?\s*(?:is |are )?([a-z0-9@.\-]{2,40})'s\b");
        var who2 = person.Success ? person.Groups[1].Value.Trim() : possessive.Success ? possessive.Groups[1].Value.Trim() : "";
        if (who2.Length >= 2)
        {
            var rows = await Rows(db, "select a.id, a.tag, a.name, coalesce(ty.name, ''), coalesce(co.name, ''), a.status, a.assigned_to " + AssetFrom + " and a.assigned_to ilike @w order by lower(a.name) limit 101",
                c => { Sc(c); c.Parameters.AddWithValue("w", "%" + who2 + "%"); }, ct);
            a.Count = Math.Min(rows.Count, Limit); a.Text = rows.Count == 0 ? $"Nothing is assigned to \"{who2}\"." : $"{rows.Count} asset(s) assigned to \"{who2}\".";
            if (rows.Count > 0) a.Tables.Add(Make("Assigned assets", new[] { "Tag", "Name", "Type", "Customer", "Status", "Assigned to" }, rows, "#/assets/"));
            return a;
        }

        // last: look for the words in names, tags, serials, addresses and people
        var term = Regex.Replace(q, @"^(find|search|look up|lookup|show|where is|where's|what is|who is|details of|info on)\s+", "").Trim();
        if (term.Length >= 2)
        {
            var like = "%" + term.Replace("%", "").Replace("_", "") + "%";
            var rows = await Rows(db, "select a.id, a.tag, a.name, coalesce(ty.name, ''), coalesce(co.name, ''), a.status, a.ip_address, a.assigned_to " + AssetFrom +
                " and (a.name ilike @w or a.tag ilike @w or a.serial ilike @w or a.hostname ilike @w or a.ip_address ilike @w or a.assigned_to ilike @w or a.mac_addresses::text ilike @w) order by lower(a.name) limit 101",
                c => { Sc(c); c.Parameters.AddWithValue("w", like); }, ct);
            var tk = await Rows(db, "select k.id, k.number, k.subject, k.status, k.company_name from servicedesk.tickets k where (not @sc or k.company_id = any(@scope)) and (k.number ilike @w or k.subject ilike @w) order by k.created_at desc limit 11",
                c => { Sc(c); c.Parameters.AddWithValue("w", like); }, ct);
            a.Count = rows.Count;
            a.Text = rows.Count + tk.Count == 0 ? $"I found nothing for \"{term}\". Try a name, tag, serial number, address or a person, or one of the examples." : $"Found {rows.Count} asset(s) and {tk.Count} ticket(s) for \"{term}\".";
            if (rows.Count > 0) a.Tables.Add(Make("Assets", new[] { "Tag", "Name", "Type", "Customer", "Status", "Address", "Assigned to" }, rows, "#/assets/"));
            if (tk.Count > 0) a.Tables.Add(Make("Tickets", new[] { "Number", "Subject", "Status", "Customer" }, tk, "#/servicedesk/"));
            if (rows.Count + tk.Count == 0) a.Suggestions = Examples.Take(6).ToList();
            return a;
        }
        a.Text = "I did not understand that. Try one of these."; a.Suggestions = Examples.Take(6).ToList();
        return a;
    }

    static async Task<(long id, string name)?> FindAsset(NpgsqlConnection db, string name, Action<NpgsqlCommand> sc, CancellationToken ct)
    {
        var rows = await Rows(db, @"select a.id, a.name from assets.assets a where a.status not in ('retired', 'lost') and (not @sc or a.company_id = any(@scope))
                and (lower(a.name) = lower(@n) or lower(a.tag) = lower(@n) or lower(a.hostname) = lower(@n) or a.name ilike @w or a.hostname ilike @w or a.tag ilike @w)
                order by (lower(a.name) = lower(@n) or lower(a.tag) = lower(@n)) desc, length(a.name) limit 1",
            c => { sc(c); c.Parameters.AddWithValue("n", name); c.Parameters.AddWithValue("w", "%" + name.Replace("%", "").Replace("_", "") + "%"); }, ct);
        return rows.Count == 0 ? null : (long.Parse(rows[0][0]), rows[0][1]);
    }
}
