using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.AdManager;

/// <summary>
/// What makes the directory easy to work with: the domain's UPN suffixes and password rules for the forms, user templates, a check of a
/// bulk file before anything is changed, and automation policies (disable inactive users, stale computers, tidy disabled accounts).
/// </summary>
internal static class ManageApi
{
    public static void Map(IEndpointRouteBuilder r, string key)
    {
        // ---- what the forms need: UPN suffixes, password rules
        AdModule.Use(r.MapGet("/api/adsuite/domains/{id:int}/info", async (int id, ClaimsPrincipal u, ITenantContext t, IDataProtectionProvider dp, IControllerRelay relay, IAccess access, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            var d = await Dir.LoadAsync(db, dp, t, id, ct); if (d == null) return Results.NotFound();
            if (await Perms.ReadScopeAsync(db, access, u, id, ct) is { Count: 0 }) return AdModule.Forbid("You have no access to this domain.");
            try
            {
                var res = await Dir.RunAsync(relay, d, new object[] { new { op = "rootdse" }, Dir.Search(d.BaseDn, "(objectClass=*)", new[] { "minPwdLength", "pwdProperties", "maxPwdAge", "pwdHistoryLength", "lockoutThreshold" }, "base", 1) }, ct, 30);
                var root = Dir.Entries(res[0]).FirstOrDefault();
                var dom = Dir.Entries(res[1]).FirstOrDefault();
                var suffixes = new List<string> { d.DnsName };
                var config = root?.S("configurationNamingContext") ?? "";
                if (config != "")
                {
                    try
                    {
                        var p = await Dir.RunAsync(relay, d, new[] { Dir.Search("CN=Partitions," + config, "(objectClass=*)", new[] { "uPNSuffixes" }, "base", 1) }, ct, 20);
                        suffixes.AddRange(Dir.Entries(p[0]).FirstOrDefault()?.L("uPNSuffixes") ?? new List<string>());
                    }
                    catch (DirError) { /* the service account may not read the configuration partition: the domain name is enough */ }
                }
                var maxAge = dom?.Num("maxPwdAge") ?? 0;   // negative 100-ns ticks; 0 or very large means never
                return Results.Ok(new
                {
                    dnsName = d.DnsName, baseDn = d.BaseDn, tls = d.Tls,
                    upnSuffixes = suffixes.Where(x => x != "").Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    password = new
                    {
                        minLength = (int)(dom?.Num("minPwdLength") ?? 0),
                        complex = ((dom?.Num("pwdProperties") ?? 0) & 1) == 1,
                        history = (int)(dom?.Num("pwdHistoryLength") ?? 0),
                        maxAgeDays = maxAge < 0 && maxAge > long.MinValue ? (int)(TimeSpan.FromTicks(-maxAge).TotalDays) : 0,
                        lockoutThreshold = (int)(dom?.Num("lockoutThreshold") ?? 0)
                    }
                });
            }
            catch (DirError e) { return Dir.Explain(e); }
        }), key);

        // ---- the overview: how many accounts are on, off, locked, active, inactive; computers likewise
        AdModule.Use(r.MapGet("/api/adsuite/domains/{id:int}/overview", async (int id, ClaimsPrincipal u, ITenantContext t, IDataProtectionProvider dp, IControllerRelay relay, IAccess access, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            var d = await Dir.LoadAsync(db, dp, t, id, ct); if (d == null) return Results.NotFound();
            var scope = await Perms.ReadScopeAsync(db, access, u, id, ct);
            if (scope is { Count: 0 }) return AdModule.Forbid("You have no access to this domain.");
            const int Max = 20000;
            var bases = scope ?? new List<string> { d.BaseDn };
            var ua = new[] { "userAccountControl", "msDS-User-Account-Control-Computed", "lockoutTime", "lastLogonTimestamp", "accountExpires", "adminCount" };
            try
            {
                var ops = bases.Select(b => Dir.Search(b, "(&(objectCategory=person)(objectClass=user))", ua, "sub", Max))
                    .Concat(bases.Select(b => Dir.Search(b, "(objectClass=computer)", new[] { "userAccountControl", "lastLogonTimestamp" }, "sub", Max))).ToArray();
                var res = await Dir.RunAsync(relay, d, ops, ct, 120);
                var now = DateTime.UtcNow; var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int on = 0, off = 0, locked = 0, expired = 0, priv = 0, a30 = 0, a90 = 0, old = 0, never = 0, con = 0, coff = 0, cstale = 0; var cut = false;
                for (var i = 0; i < res.Count; i++)
                {
                    var computers = i >= bases.Count; cut |= Dir.Truncated(res[i]);
                    foreach (var e in Dir.Entries(res[i]))
                    {
                        if (!seen.Add(e.Dn)) continue;
                        var o = Shape.ToObj(e); var last = Dir.FileTime(e.Num("lastLogonTimestamp"));
                        if (computers)
                        {
                            if (o.Disabled) coff++; else if (last == null || last < now.AddDays(-90)) cstale++; else con++;
                            continue;
                        }
                        if (o.Disabled) { off++; continue; }
                        on++;
                        if (o.Locked) locked++;
                        if (o.Expired) expired++;
                        if (e.Num("adminCount") == 1) priv++;
                        if (last == null) never++; else if (last >= now.AddDays(-30)) a30++; else if (last >= now.AddDays(-90)) a90++; else old++;
                    }
                }
                int waiting;
                await using (var c = new NpgsqlCommand("select (select count(*) from adsuite.approvals where domain_id = @d and status = 'pending') + (select count(*) from adsuite.requests where domain_id = @d and status = 'pending')", db))
                { AdModule.P(c, "d", id); waiting = Convert.ToInt32(await c.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture); }
                return Results.Ok(new
                {
                    users = new { enabled = on, disabled = off, locked, expired, privileged = priv },
                    activity = new { last30 = a30, last90 = a90, older = old, never },
                    computers = new { active = con, stale = cstale, disabled = coff },
                    waiting, truncated = cut
                });
            }
            catch (DirError e) { return Dir.Explain(e); }
        }), key);

        // ---- user templates: where new accounts go, their groups, default fields and naming
        AdModule.Use(r.MapGet("/api/adsuite/domains/{id:int}/templates", async (int id, ClaimsPrincipal u, ITenantContext t, IAccess access, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            if (await Perms.ScopeAsync(db, access, u, id, "create", ct) is { Count: 0 } && !AdModule.IsAdmin(u)) return Results.Ok(Array.Empty<object>());
            var list = new List<object>();
            await using var c = new NpgsqlCommand("select id, name, description, ou, groups, attrs::text, naming::text, options::text from adsuite.templates where domain_id = @d order by name", db);
            AdModule.P(c, "d", id);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
                list.Add(new { id = rd.GetInt32(0), name = rd.GetString(1), description = rd.GetString(2), ou = rd.GetString(3), groups = rd.GetFieldValue<string[]>(4),
                    attrs = JsonDocument.Parse(rd.GetString(5)).RootElement.Clone(), naming = JsonDocument.Parse(rd.GetString(6)).RootElement.Clone(), options = JsonDocument.Parse(rd.GetString(7)).RootElement.Clone() });
            return Results.Ok(list);
        }), key);

        AdModule.AdminOnly(r.MapPost("/api/adsuite/domains/{id:int}/templates", (int id, JsonElement body, ClaimsPrincipal u, ITenantContext t, IDataProtectionProvider dp, IAuditLog audit, CancellationToken ct) =>
            SaveTemplate(id, null, body, u, t, dp, audit, ct)), key);
        AdModule.AdminOnly(r.MapPut("/api/adsuite/templates/{tid:int}", async (int tid, JsonElement body, ClaimsPrincipal u, ITenantContext t, IDataProtectionProvider dp, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            await using var c = new NpgsqlCommand("select domain_id from adsuite.templates where id = @i", db); AdModule.P(c, "i", tid);
            return await c.ExecuteScalarAsync(ct) is int dom ? await SaveTemplate(dom, tid, body, u, t, dp, audit, ct) : Results.NotFound();
        }), key);
        AdModule.AdminOnly(r.MapDelete("/api/adsuite/templates/{tid:int}", async (int tid, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            await using var c = new NpgsqlCommand("delete from adsuite.templates where id = @i returning name", db); AdModule.P(c, "i", tid);
            if (await c.ExecuteScalarAsync(ct) is not string n) return Results.NotFound();
            await audit.WriteAsync("adsuite.template.delete", n, "", null, ct);
            return Results.NoContent();
        }), key);

        // ---- bulk: check a file before anything is changed (the browser then runs the rows one by one, with progress)
        AdModule.Use(r.MapPost("/api/adsuite/domains/{id:int}/bulk/check", async (int id, JsonElement body, ClaimsPrincipal u, ITenantContext t, IDataProtectionProvider dp, IControllerRelay relay, IAccess access, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            var d = await Dir.LoadAsync(db, dp, t, id, ct); if (d == null) return Results.NotFound();
            var mode = AdModule.Str(body, "mode") ?? "create";
            if (!body.TryGetProperty("rows", out var rs) || rs.ValueKind != JsonValueKind.Array) return AdModule.Bad("bad_input", "No rows.");
            var rows = rs.EnumerateArray().ToList();
            if (rows.Count is 0 or > 1000) return AdModule.Bad("bad_input", "Between 1 and 1,000 rows at a time.");
            string S(JsonElement e, string n) => (AdModule.Str(e, n) ?? "").Trim();

            // which sign-in names already exist (one search per 40 names)
            var sams = rows.Select(x => S(x, "sam")).Where(x => x != "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var found = new Dictionary<string, (string Dn, bool Disabled)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var chunk in sams.Chunk(40))
                {
                    var f = "(&(objectCategory=person)(objectClass=user)(|" + string.Concat(chunk.Select(x => $"(sAMAccountName={Dir.Esc(x)})")) + "))";
                    var res = await Dir.RunAsync(relay, d, new[] { Dir.Search(d.BaseDn, f, new[] { "sAMAccountName", "userAccountControl" }, "sub", 100) }, ct, 60);
                    foreach (var e in Dir.Entries(res[0])) found[e.S("sAMAccountName")] = (e.Dn, ((int)e.Num("userAccountControl") & Dir.UacDisabled) != 0);
                }
            }
            catch (DirError e) { return Dir.Explain(e); }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outRows = new List<object>(); var okCount = 0;
            var allowed = Actions.EditableAttrs.ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < rows.Count; i++)
            {
                var x = rows[i]; var problems = new List<string>(); var sam = S(x, "sam"); string dn = "";
                if (!Dir.Sam.IsMatch(sam)) problems.Add("the sign-in name has letters, digits and . _ - only, up to 20 characters");
                else if (!seen.Add(sam)) problems.Add("this sign-in name is in the file twice");
                if (mode == "create")
                {
                    if (found.ContainsKey(sam)) problems.Add("an account with this sign-in name already exists");
                    var ou = S(x, "ou");
                    if (!Dir.ValidDn(ou) || !Dir.IsUnder(ou, d.BaseDn)) problems.Add("the OU is not inside this domain");
                    else if (!await Perms.CanAsync(db, access, u, id, ou, "create", ct)) problems.Add("you may not create accounts in this OU");
                    if (S(x, "first") == "" && S(x, "last") == "" && S(x, "display") == "") problems.Add("give a first name, last name or display name");
                }
                else
                {
                    if (found.TryGetValue(sam, out var hit)) dn = hit.Dn; else if (problems.Count == 0) problems.Add("no account has this sign-in name");
                    if (dn != "" && !await Perms.CanAsync(db, access, u, id, dn, "modify", ct)) problems.Add("you may not change this account");
                }
                if (x.TryGetProperty("attrs", out var at) && at.ValueKind == JsonValueKind.Object)
                    foreach (var p in at.EnumerateObject()) if (!allowed.Contains(p.Name)) problems.Add(p.Name + " cannot be set");
                if (problems.Count == 0) okCount++;
                outRows.Add(new { row = i + 1, sam, dn, ok = problems.Count == 0, problems });
            }
            return Results.Ok(new { rows = outRows, ok = okCount, total = rows.Count });
        }), key);

        // ---- automation policies
        AdModule.AdminOnly(r.MapGet("/api/adsuite/domains/{id:int}/policies", async (int id, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            return Results.Ok(await AutomationJob.ListAsync(db, id, ct));
        }), key);
        AdModule.AdminOnly(r.MapPost("/api/adsuite/domains/{id:int}/policies", (int id, JsonElement body, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) => SavePolicy(id, null, body, u, t, audit, ct)), key);
        AdModule.AdminOnly(r.MapPut("/api/adsuite/policies/{pid:int}", async (int pid, JsonElement body, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            await using var c = new NpgsqlCommand("select domain_id from adsuite.policies where id = @i", db); AdModule.P(c, "i", pid);
            return await c.ExecuteScalarAsync(ct) is int dom ? await SavePolicy(dom, pid, body, u, t, audit, ct) : Results.NotFound();
        }), key);
        AdModule.AdminOnly(r.MapDelete("/api/adsuite/policies/{pid:int}", async (int pid, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            await using var c = new NpgsqlCommand("delete from adsuite.policies where id = @i returning name", db); AdModule.P(c, "i", pid);
            if (await c.ExecuteScalarAsync(ct) is not string n) return Results.NotFound();
            await audit.WriteAsync("adsuite.policy.delete", n, "", null, ct);
            return Results.NoContent();
        }), key);
        // what a policy would do now, without doing it
        AdModule.AdminOnly(r.MapPost("/api/adsuite/policies/{pid:int}/preview", async (int pid, ITenantContext t, IDataProtectionProvider dp, IControllerRelay relay, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            var p = await AutomationJob.LoadAsync(db, pid, ct); if (p == null) return Results.NotFound();
            var d = await Dir.LoadAsync(db, dp, t, p.DomainId, ct); if (d == null) return Results.NotFound();
            try
            {
                var (act, skipped) = await AutomationJob.FindAsync(relay, d, p, ct);
                return Results.Ok(new { would = act.Select(x => new { dn = x.Dn, name = x.Name }).ToList(), skipped = skipped.Select(x => new { dn = x.Dn, name = x.Name, why = x.Why }).ToList() });
            }
            catch (DirError e) { return Dir.Explain(e); }
        }), key);
        // run it now, in the background (it can take a few minutes)
        AdModule.AdminOnly(r.MapPost("/api/adsuite/policies/{pid:int}/run", async (int pid, ITenantContext t, IJobQueue jobs, ClaimsPrincipal u, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AdModule.Open(t, ct);
            if (await AutomationJob.LoadAsync(db, pid, ct) is not { } p) return Results.NotFound();
            await jobs.EnqueueAsync(AutomationJob.JobType, JsonSerializer.Serialize(new { policy = pid, by = AdModule.Actor(u) }), null, "adsuite.automation:" + pid, ct);
            await audit.WriteAsync("adsuite.policy.run", p.Name, "started by hand", null, ct);
            return Results.Accepted(value: new { ok = true });
        }), key);
    }

    static async Task<IResult> SaveTemplate(int domainId, int? tid, JsonElement body, ClaimsPrincipal u, ITenantContext t, IDataProtectionProvider dp, IAuditLog audit, CancellationToken ct)
    {
        await using var db = await AdModule.Open(t, ct);
        var d = await Dir.LoadAsync(db, dp, t, domainId, ct); if (d == null) return Results.NotFound();
        var name = (AdModule.Str(body, "name") ?? "").Trim();
        if (name == "" || name.Length > 100) return AdModule.Bad("bad_input", "Give the template a name.");
        var ou = (AdModule.Str(body, "ou") ?? "").Trim();
        if (ou != "" && (!Dir.ValidDn(ou) || !Dir.IsUnder(ou, d.BaseDn))) return AdModule.Bad("bad_input", "The OU is not inside this domain.");
        var groups = new List<string>();
        if (body.TryGetProperty("groups", out var gs) && gs.ValueKind == JsonValueKind.Array) foreach (var g in gs.EnumerateArray()) if (g.ValueKind == JsonValueKind.String) groups.Add(g.GetString()!.Trim());
        if (groups.Count > 50 || groups.Any(g => !Dir.ValidDn(g) || !Dir.IsUnder(g, d.BaseDn))) return AdModule.Bad("bad_input", "Choose up to 50 groups of this domain.");
        var allowed = Actions.EditableAttrs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var attrs = new Dictionary<string, string>();
        if (body.TryGetProperty("attrs", out var at) && at.ValueKind == JsonValueKind.Object)
            foreach (var p in at.EnumerateObject())
            {
                if (!allowed.Contains(p.Name) || p.Name is "displayName" or "manager") return AdModule.Bad("bad_input", p.Name + " cannot be part of a template.");
                var v = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()!.Trim() : "";
                if (v.Length > 1024 || v.Any(char.IsControl)) return AdModule.Bad("bad_input", p.Name + " is too long.");
                if (v != "") attrs[p.Name] = v;
            }
        JsonElement Part(string n) => body.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Object ? v : JsonDocument.Parse("{}").RootElement;
        var naming = Part("naming"); var options = Part("options");
        if (naming.GetRawText().Length > 2000 || options.GetRawText().Length > 2000) return AdModule.Bad("bad_input", "The template is too large.");
        try
        {
            await using var c = new NpgsqlCommand(tid == null
                ? "insert into adsuite.templates (domain_id, name, description, ou, groups, attrs, naming, options, created_by) values (@d,@n,@ds,@ou,@g,@a::jsonb,@nm::jsonb,@op::jsonb,@by) returning id"
                : "update adsuite.templates set name=@n, description=@ds, ou=@ou, groups=@g, attrs=@a::jsonb, naming=@nm::jsonb, options=@op::jsonb where id=@i returning id", db);
            AdModule.P(c, "d", domainId); AdModule.P(c, "n", name); AdModule.P(c, "ds", AdModule.Clip(AdModule.Str(body, "description"), 500)); AdModule.P(c, "ou", ou);
            c.Parameters.AddWithValue("g", groups.ToArray()); AdModule.P(c, "a", JsonSerializer.Serialize(attrs)); AdModule.P(c, "nm", naming.GetRawText()); AdModule.P(c, "op", options.GetRawText());
            AdModule.P(c, "by", AdModule.Actor(u)); AdModule.P(c, "i", tid ?? 0);
            var id = (int)(await c.ExecuteScalarAsync(ct))!;
            await audit.WriteAsync(tid == null ? "adsuite.template.create" : "adsuite.template.update", $"{d.Name}: {name}", "", null, ct);
            return Results.Ok(new { id });
        }
        catch (PostgresException e) when (e.SqlState == "23505") { return Results.Conflict(new { error = "exists", detail = "A template with this name exists." }); }
    }

    static async Task<IResult> SavePolicy(int domainId, int? pid, JsonElement body, ClaimsPrincipal u, ITenantContext t, IAuditLog audit, CancellationToken ct)
    {
        await using var db = await AdModule.Open(t, ct);
        string baseDn;
        await using (var q = new NpgsqlCommand("select base_dn from adsuite.domains where id = @d", db)) { AdModule.P(q, "d", domainId); if (await q.ExecuteScalarAsync(ct) is not string b) return Results.NotFound(); baseDn = b; }
        var name = (AdModule.Str(body, "name") ?? "").Trim();
        var kind = AdModule.Str(body, "kind") ?? "";
        if (name == "" || name.Length > 100) return AdModule.Bad("bad_input", "Give the policy a name.");
        if (kind is not ("disable-inactive-users" or "disable-stale-computers" or "move-disabled-users")) return AdModule.Bad("bad_input", "Unknown kind of policy.");
        var days = AdModule.Int(body, "days") ?? 90;
        if (kind != "move-disabled-users" && days is < 30 or > 3650) return AdModule.Bad("bad_input", "Between 30 and 3,650 days: the directory updates the last sign-in only about every two weeks.");
        var ou = (AdModule.Str(body, "ou") ?? "").Trim(); var move = (AdModule.Str(body, "moveTo") ?? "").Trim();
        if (ou != "" && (!Dir.ValidDn(ou) || !Dir.IsUnder(ou, baseDn))) return AdModule.Bad("bad_input", "The OU is not inside this domain.");
        if (move != "" && (!Dir.ValidDn(move) || !Dir.IsUnder(move, baseDn))) return AdModule.Bad("bad_input", "Where to move them is not inside this domain.");
        if (kind == "move-disabled-users" && move == "") return AdModule.Bad("bad_input", "Choose the OU disabled accounts are moved to.");
        await using var c = new NpgsqlCommand(pid == null
            ? "insert into adsuite.policies (domain_id, name, kind, days, ou, move_to, enabled, created_by) values (@d,@n,@k,@days,@ou,@mv,@en,@by) returning id"
            : "update adsuite.policies set name=@n, kind=@k, days=@days, ou=@ou, move_to=@mv, enabled=@en where id=@i returning id", db);
        AdModule.P(c, "d", domainId); AdModule.P(c, "n", name); AdModule.P(c, "k", kind); AdModule.P(c, "days", days); AdModule.P(c, "ou", ou); AdModule.P(c, "mv", move);
        AdModule.P(c, "en", AdModule.Bool(body, "enabled")); AdModule.P(c, "by", AdModule.Actor(u)); AdModule.P(c, "i", pid ?? 0);
        var id = (int)(await c.ExecuteScalarAsync(ct))!;
        await audit.WriteAsync(pid == null ? "adsuite.policy.create" : "adsuite.policy.update", name, $"{kind}, {days} days{(AdModule.Bool(body, "enabled") ? ", on" : ", off")}", null, ct);
        return Results.Ok(new { id });
    }
}

/// <summary>
/// Runs the automation policies once a day (and one policy when started by hand). Built-in accounts, privileged accounts and the
/// service account are never touched; at most 200 objects per run, so a wrong setting cannot sweep the whole directory at once.
/// </summary>
internal sealed class AutomationJob : IJobHandler
{
    public const string JobType = "adsuite.automation";
    public string Type => JobType;
    const string Uac = "1.2.840.113556.1.4.803";
    const int MaxPerRun = 200;

    internal sealed record Policy(int Id, int DomainId, string Name, string Kind, int Days, string Ou, string MoveTo, bool Enabled);
    internal sealed record Hit(string Dn, string Name, string Why = "");

    public async Task RunAsync(JobContext job, CancellationToken ct)
    {
        var sp = job.Services;
        var lic = await sp.GetRequiredService<IEntitlements>().CurrentAsync(ct);
        if (!lic.Has(Modules.AdSuite)) return;
        var t = sp.GetRequiredService<ITenantContext>(); var dp = sp.GetRequiredService<IDataProtectionProvider>();
        var relay = sp.GetService<IControllerRelay>(); var audit = sp.GetRequiredService<IAuditLog>();
        if (relay == null) return;
        int? only = null; var by = "automation";
        try
        {
            using var p = JsonDocument.Parse(string.IsNullOrWhiteSpace(job.Payload) ? "{}" : job.Payload);
            if (p.RootElement.TryGetProperty("policy", out var pi) && pi.TryGetInt32(out var v)) only = v;
            if (p.RootElement.TryGetProperty("by", out var b) && b.ValueKind == JsonValueKind.String) by = "automation (started by " + b.GetString() + ")";
        }
        catch (JsonException) { }
        await using var db = new NpgsqlConnection(job.Tenant.ConnectionString); await db.OpenAsync(ct);
        var list = new List<Policy>();
        await using (var c = new NpgsqlCommand("select id, domain_id, name, kind, days, ou, move_to, enabled from adsuite.policies where " + (only == null ? "enabled" : "id = @i") + " order by id", db))
        {
            AdModule.P(c, "i", only ?? 0);
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) list.Add(new Policy(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5), r.GetString(6), r.GetBoolean(7)));
        }
        foreach (var p in list)
        {
            string result;
            try
            {
                var d = await Dir.LoadAsync(db, dp, t, p.DomainId, ct);
                if (d == null || !d.Enabled) continue;
                var (act, skipped) = await FindAsync(relay, d, p, ct);
                int done = 0, failed = 0; var errors = new List<string>();
                foreach (var h in act)
                {
                    var ops = new List<object>();
                    if (p.Kind != "move-disabled-users")
                    {
                        // read the account again: only switch off what is still enabled
                        var look = await Dir.RunAsync(relay, d, new[] { Dir.Search(h.Dn, "(objectClass=*)", new[] { "userAccountControl", "description" }, "base", 1) }, ct);
                        var e = Dir.Entries(look[0]).FirstOrDefault(); if (e == null) continue;
                        var uac = (int)e.Num("userAccountControl", Dir.UacNormal);
                        ops.Add(Dir.Modify(h.Dn, ("replace", "userAccountControl", new[] { (uac | Dir.UacDisabled).ToString(CultureInfo.InvariantCulture) }),
                            ("replace", "description", new[] { $"Disabled {DateTime.UtcNow:yyyy-MM-dd} by DeskAsk automation: no sign-in for {p.Days} days" })));
                    }
                    if (p.MoveTo != "" && !Dir.Same(Dir.Parent(h.Dn), p.MoveTo)) ops.Add(new { op = "rename", dn = h.Dn, newRdn = Dir.Rdns(h.Dn)[0], newParent = p.MoveTo });
                    if (ops.Count == 0) continue;
                    try { await Dir.RunAsync(relay, d, ops, ct); done++; }
                    catch (DirError ex) { failed++; if (errors.Count < 5) errors.Add(h.Name + ": " + ex.Message); }
                }
                result = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC: {done} changed, {failed} failed, {skipped.Count} left alone (privileged or built in)" + (act.Count >= MaxPerRun ? $"; stopped at {MaxPerRun}, the rest go next time" : "") + (errors.Count > 0 ? ". " + string.Join("; ", errors) : "");
                await audit.WriteAsync("adsuite.policy.ran", $"{d.Name}: {p.Name}", result, by, ct);
            }
            catch (DirError ex) { result = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC: did not run: {ex.Message}"; }
            await using var u = new NpgsqlCommand("update adsuite.policies set last_run = now(), last_result = @r where id = @i", db);
            AdModule.P(u, "r", AdModule.Clip(result, 2000)); AdModule.P(u, "i", p.Id); await u.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>The objects a policy would change now, and the ones it leaves alone and why.</summary>
    internal static async Task<(List<Hit> Act, List<Hit> Skipped)> FindAsync(IControllerRelay relay, Domain d, Policy p, CancellationToken ct)
    {
        var cutoff = Dir.ToFileTime(DateTime.UtcNow.AddDays(-p.Days));
        var created = DateTime.UtcNow.AddDays(-p.Days).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".0Z";
        const string user = "(objectCategory=person)(objectClass=user)";
        var filter = p.Kind switch
        {
            // no sign-in since the cutoff, or never signed in and older than the cutoff
            "disable-inactive-users" => $"(&{user}(!(userAccountControl:{Uac}:=2))(|(lastLogonTimestamp<={cutoff})(&(!(lastLogonTimestamp=*))(whenCreated<={created}))))",
            "disable-stale-computers" => $"(&(objectClass=computer)(!(userAccountControl:{Uac}:=2))(|(lastLogonTimestamp<={cutoff})(&(!(lastLogonTimestamp=*))(whenCreated<={created}))))",
            _ => $"(&{user}(userAccountControl:{Uac}:=2))"
        };
        var res = await Dir.RunAsync(relay, d, new[] { Dir.Search(p.Ou == "" ? d.BaseDn : p.Ou, filter, new[] { "name", "displayName", "sAMAccountName", "userPrincipalName", "adminCount", "objectSid", "isCriticalSystemObject" }, "sub", 2000) }, ct, 90);
        var act = new List<Hit>(); var skipped = new List<Hit>();
        foreach (var e in Dir.Entries(res[0]))
        {
            var name = e.S("displayName") != "" ? e.S("displayName") : e.S("name") != "" ? e.S("name") : Dir.RdnValue(e.Dn);
            var rid = BrowseApi.Rid(e.S("objectSid"));
            var at = d.BindDn.IndexOf('@');
            var bind = Dir.Same(e.Dn, d.BindDn) || string.Equals(e.S("userPrincipalName"), d.BindDn, StringComparison.OrdinalIgnoreCase)
                || (at > 0 && !d.BindDn.Contains('=') && string.Equals(e.S("sAMAccountName"), d.BindDn[..at], StringComparison.OrdinalIgnoreCase));
            if (rid is > 0 and < 1000 || string.Equals(e.S("isCriticalSystemObject"), "TRUE", StringComparison.OrdinalIgnoreCase)) skipped.Add(new Hit(e.Dn, name, "built in"));
            else if (e.Num("adminCount") == 1) skipped.Add(new Hit(e.Dn, name, "privileged"));
            else if (bind) skipped.Add(new Hit(e.Dn, name, "DeskAsk's service account"));
            else if (p.Kind == "move-disabled-users" && Dir.IsUnder(e.Dn, p.MoveTo)) continue;
            else if (act.Count < MaxPerRun) act.Add(new Hit(e.Dn, name));
        }
        return (act, skipped);
    }

    internal static async Task<Policy?> LoadAsync(NpgsqlConnection db, int id, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand("select id, domain_id, name, kind, days, ou, move_to, enabled from adsuite.policies where id = @i", db); AdModule.P(c, "i", id);
        await using var r = await c.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new Policy(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetString(5), r.GetString(6), r.GetBoolean(7)) : null;
    }

    internal static async Task<List<object>> ListAsync(NpgsqlConnection db, int domainId, CancellationToken ct)
    {
        var list = new List<object>();
        await using var c = new NpgsqlCommand("select id, name, kind, days, ou, move_to, enabled, last_run, last_result from adsuite.policies where domain_id = @d order by name", db); AdModule.P(c, "d", domainId);
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new { id = r.GetInt32(0), name = r.GetString(1), kind = r.GetString(2), days = r.GetInt32(3), ou = r.GetString(4), moveTo = r.GetString(5), enabled = r.GetBoolean(6),
                lastRun = r.IsDBNull(7) ? (DateTime?)null : r.GetDateTime(7), lastResult = r.GetString(8) });
        return list;
    }
}
