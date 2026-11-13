using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace DeskAsk.Platform.Assets;

/// <summary>
/// Agentless discovery by IP address or DNS name: probe a host, scan a range, guess what it is and (with a saved credential) read its details over WinRM or SSH.
/// Only administrators can use it. Credentials are sealed in the customer database and are never returned.
/// </summary>
public static class DiscoveryApi
{
    static readonly (int Port, string Name)[] Ports =
    {
        (22, "SSH"), (80, "HTTP"), (135, "RPC"), (161, "SNMP*"), (443, "HTTPS"), (445, "SMB"), (554, "RTSP"), (631, "IPP"), (3389, "RDP"),
        (5985, "WinRM"), (5986, "WinRM-S"), (8000, "HTTP-alt"), (8080, "HTTP-alt"), (9100, "JetDirect")
    };
    static readonly Regex HostRe = new(@"^(?=.{1,253}\z)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\z|^[0-9a-fA-F:]{2,45}\z", RegexOptions.Compiled);

    internal sealed record Guess(string Os, string Cat, string Sub, string How);

    internal static string? Target(string? t) { t = (t ?? "").Trim(); return t == "" || !HostRe.IsMatch(t) ? null : t; }
    internal static string ShortName(string fqdn) { var i = fqdn.IndexOf('.'); return i >= 0 ? fqdn[..i] : fqdn; }

    internal static async Task<bool> Tcp(string host, int port, int ms)
    {
        try { using var c = new TcpClient(); using var cts = new CancellationTokenSource(ms); await c.ConnectAsync(host, port, cts.Token); return true; } catch { return false; }
    }
    static async Task<bool> PingHost(string host)
    {
        try { using var p = new Ping(); var r = await p.SendPingAsync(host, 1000); return r.Status == IPStatus.Success; } catch { return false; }
    }
    static async Task<(bool Ok, string Out, string Err)> Exec(string cmd, IEnumerable<string> args, int timeoutMs, Dictionary<string, string>? env = null, string? stdin = null)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin != null, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;
            using var p = Process.Start(psi)!;
            var so = new StringBuilder(); var se = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null && so.Length < 8_000_000) so.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null && se.Length < 1_000_000) se.AppendLine(e.Data); };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            if (stdin != null) { try { await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close(); } catch { } }
            using var cts = new CancellationTokenSource(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } return (false, so.ToString(), se.Length > 0 ? se.ToString() : "timed out"); }
            p.WaitForExit();
            return (p.ExitCode == 0, so.ToString(), se.ToString());
        }
        catch (Exception e) { return (false, "", e.Message); }
    }
    static async Task<string> ArpMac(string ip)
    {
        if (!Regex.IsMatch(ip, @"^\d+\.\d+\.\d+\.\d+\z")) return "";
        var r = OperatingSystem.IsWindows() ? await Exec("arp", new[] { "-a", ip }, 5000) : await Exec("ip", new[] { "neigh", "show", ip }, 5000);
        var m = Regex.Match(r.Out, "([0-9a-f]{2}[:-]){5}[0-9a-f]{2}", RegexOptions.IgnoreCase);
        return m.Success ? m.Value.ToUpperInvariant().Replace('-', ':') : "";
    }
    /// <summary>What a device probably is, from the ports it answers on.</summary>
    internal static Guess GuessFrom(IReadOnlyCollection<int> open)
    {
        bool Has(int p) => open.Contains(p);
        if (Has(5985) || Has(5986) || (Has(135) && Has(445))) return new("Windows", Has(3389) ? "IT hardware" : "Server", Has(3389) ? "Endpoint" : "Server", "winrm");
        if (Has(9100) || Has(631)) return new("", "Printers & peripherals", "Printer", "");
        if (Has(554)) return new("", "CCTV", "IP camera", "");
        if (Has(22)) return new("Linux / network device", "Server", "Server", "ssh");
        if (Has(3389)) return new("Windows", "IT hardware", "Endpoint", "winrm");
        if (Has(80) || Has(443) || Has(8080)) return new("", "Network", "Other", "");
        return new("", "", "", "");
    }
    /// <summary>The addresses of a CIDR range up to /22 (1024 addresses); network and broadcast addresses are left out.</summary>
    internal static List<string>? Expand(string cidr, out string error)
    {
        error = ""; var m = Regex.Match((cidr ?? "").Trim(), @"^(\d+)\.(\d+)\.(\d+)\.(\d+)/(\d+)\z");
        if (!m.Success) { error = "Enter a range like 192.168.1.0/24."; return null; }
        var bits = int.Parse(m.Groups[5].Value);
        if (bits < 22 || bits > 32) { error = "Scan at most a /22 (1024 addresses) at a time."; return null; }
        var o = Enumerable.Range(1, 4).Select(i => long.Parse(m.Groups[i].Value)).ToArray();
        if (o.Any(x => x > 255)) { error = "That is not a valid address."; return null; }
        if (o[0] == 169 && o[1] == 254) { error = "Link-local addresses cannot be scanned."; return null; }
        if (o[0] >= 224) { error = "Multicast and reserved addresses cannot be scanned."; return null; }
        uint bs = (uint)((o[0] << 24) | (o[1] << 16) | (o[2] << 8) | o[3]); long size = 1L << (32 - bits); uint start = (uint)(bs & ~(uint)(size - 1));
        var ips = new List<string>();
        for (long i = 0; i < size; i++)
        {
            if (size > 2 && (i == 0 || i == size - 1)) continue;
            var v = (uint)(start + i); ips.Add(((v >> 24) & 255) + "." + ((v >> 16) & 255) + "." + ((v >> 8) & 255) + "." + (v & 255));
        }
        return ips;
    }
    internal static long IpKey(string ip) => ip.Split('.').Aggregate(0L, (x, y) => x * 256 + long.Parse(y));

    static string Resource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!; using var rd = new StreamReader(s); return rd.ReadToEnd();
    }
    const string WinRm = "$ErrorActionPreference='Stop'\n$sec=ConvertTo-SecureString $env:DK_PW -AsPlainText -Force; $cred=New-Object System.Management.Automation.PSCredential($env:DK_USER,$sec)\n$o=@{ComputerName=$env:DK_HOST;Credential=$cred;FilePath=$env:DK_SCRIPT;ErrorAction='Stop'}\nif($env:DK_SSL -eq '1'){$o.UseSSL=$true;$o.SessionOption=New-PSSessionOption -SkipCACheck -SkipCNCheck}\nInvoke-Command @o";

    /// <summary>Turns powershell.exe error output (CLIXML or console text wrapped over several lines) into one readable sentence.</summary>
    public static string CleanError(string err)
    {
        var s = err ?? "";
        if (s.Contains("CLIXML"))
        {
            var parts = System.Text.RegularExpressions.Regex.Matches(s, "<S S=\"Error\">(.*?)</S>", System.Text.RegularExpressions.RegexOptions.Singleline).Select(m => m.Groups[1].Value);
            s = string.Concat(parts);
            s = System.Text.RegularExpressions.Regex.Replace(s, "_x([0-9A-Fa-f]{4})_", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
            s = System.Net.WebUtility.HtmlDecode(s);
        }
        var lines = s.Split('\n').Select(l => l.Trim()).Where(l => l != "" && !l.StartsWith("+ ") && !l.StartsWith("At line:") && !l.StartsWith("CategoryInfo") && !l.StartsWith("FullyQualifiedErrorId")).ToList();
        s = string.Join(" ", lines);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"^\[[^\]]*\]\s*Connecting to remote server \S+ failed with the following error message\s*:\s*", "");
        var cut = s.IndexOf("For more information, see the about_Remote_Troubleshooting", StringComparison.OrdinalIgnoreCase); if (cut > 0) s = s[..cut];
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        return s == "" ? "unknown error" : AgentsApi.Clip(s, 600);
    }

    /// <summary>Adds what to do next for the usual WinRM failures. <paramref name="from"/> names the computer that runs the remoting.</summary>
    public static string WinHint(string msg, string from)
    {
        var m = msg.ToLowerInvariant();
        if (m.Contains("access is denied") || m.Contains("logon failure") || m.Contains("user name or password"))
            return msg + " - the login was refused: use an account in the target's Administrators group (DOMAIN\\user, or COMPUTER\\user for a local account).";
        if (m.Contains("trustedhosts") || m.Contains("kerberos") || m.Contains("cannot process the request"))
            return msg + " - the target is reached by IP address or is outside the domain, so Windows needs it listed as trusted: on " + from + " run as administrator  Set-Item WSMan:\\localhost\\Client\\TrustedHosts -Value '<address or name>' -Concatenate -Force";
        if (m.Contains("cannot be resolved") || m.Contains("network path was not found"))
            return msg + " - the name cannot be found from " + from + ": use the IP address or fix DNS.";
        if (m.Contains("winrm") || m.Contains("cannot connect"))
            return msg + " - run  Enable-PSRemoting -Force  on the target and check its firewall allows port 5985 from " + from + ".";
        return msg;
    }

    static JsonElement ParseCollected(string o)
    {
        var t = o.Trim(); var i = t.LastIndexOf("\n{", StringComparison.Ordinal); var s = i >= 0 ? t[(i + 1)..] : t;
        try { return JsonDocument.Parse(s.TrimStart('﻿')).RootElement.Clone(); }
        catch { throw new InvalidOperationException("The computer answered, but the reply could not be read."); }
    }
    /// <summary>Runs the collector script against a host (Windows over WinRM from a Windows server, Linux over SSH). Throws InvalidOperationException with a readable message.</summary>
    internal static async Task<JsonElement> RunCollector(string kind, string username, string secret, string host)
    {
        if (kind == "windows")
        {
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Reading Windows computers over WinRM needs the server to run on Windows. Install the agent on that computer instead.");
            var script = Path.Combine(Path.GetTempPath(), "dk-" + AgentsApi.Rand(8) + ".ps1");
            try
            {
                await File.WriteAllTextAsync(script, Resource("collect-windows.ps1"));
                // A host name lets Kerberos work between domain computers; an IP address needs TrustedHosts or HTTPS.
                var names = new List<string>();
                if (System.Net.IPAddress.TryParse(host, out _))
                {
                    try { var n = (await System.Net.Dns.GetHostEntryAsync(host)).HostName; if (!string.IsNullOrWhiteSpace(n) && n != host) names.Add(n); } catch { }
                }
                names.Add(host);
                string last = "unknown error";
                foreach (var name in names)
                {
                    var env = new Dictionary<string, string> { ["DK_HOST"] = name, ["DK_USER"] = username, ["DK_PW"] = secret, ["DK_SCRIPT"] = script };
                    var r = await Exec("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", WinRm }, 180000, env);
                    if (r.Ok) return ParseCollected(r.Out);
                    last = "WinRM to " + name + " as " + username + " failed: " + WinHint(CleanError(r.Err), "the DeskAsk server");
                }
                throw new InvalidOperationException(last);
            }
            finally { try { File.Delete(script); } catch { } }
        }
        var key = Path.Combine(Path.GetTempPath(), "dk-" + AgentsApi.Rand(8));
        try
        {
            await File.WriteAllTextAsync(key, secret.EndsWith('\n') ? secret : secret + "\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var r = await Exec("ssh", new[] { "-i", key, "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new", "-o", "ConnectTimeout=8", username + "@" + host, "bash -s" }, 120000, null, Resource("collect-linux.sh"));
            if (!(r.Ok || r.Out.Trim() != ""))
            {
                var last = r.Err.Trim().Split('\n').LastOrDefault() ?? ""; if (last == "") last = "unknown error";
                throw new InvalidOperationException("SSH failed: " + AgentsApi.Clip(last, 300));
            }
            return ParseCollected(r.Out);
        }
        finally { try { File.WriteAllText(key, ""); File.Delete(key); } catch { } }
    }

    static IResult Bad(string error, string? detail = null) => AgentsApi.Bad(error, detail);

    static string? Sealed(IConfiguration cfg, ITenantContext t, string v) => v == "" ? "" : SecretBox.Seal(cfg, t.Require().Id, v);
    static string Unseal(IConfiguration cfg, ITenantContext t, string v) { try { return v == "" ? "" : SecretBox.Open(cfg, t.Require().Id, v); } catch { return ""; } }

    sealed record CredRow(int Id, string Name, string Kind, string User, string Secret);
    static async Task<List<CredRow>> Creds(NpgsqlConnection db, int? id, CancellationToken ct)
    {
        var o = new List<CredRow>();
        await using var c = new NpgsqlCommand("select id, name, kind, username, secret from assets.creds where (@i::int is null or id = @i) order by created_at desc", db); AgentsApi.P(c, "i", id);
        await using var rd = await c.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) o.Add(new CredRow(rd.GetInt32(0), rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetString(4)));
        return o;
    }

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        RouteHandlerBuilder Admin(RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);

        Admin(r.MapPost("/api/assets/discover/probe", async (JsonElement body, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var target = Target(AgentsApi.Str(body, "target")); if (target == null) return Bad("bad_input", "Enter an IP address or a DNS name.");
            var hostname = ""; var fqdn = ""; List<string> ips;
            if (IPAddress.TryParse(target, out _))
            {
                ips = new() { target };
                try { var h = await Dns.GetHostEntryAsync(target, ct); if (h.HostName != target) { hostname = ShortName(h.HostName); fqdn = h.HostName; } } catch { }
            }
            else
            {
                try { ips = (await Dns.GetHostAddressesAsync(target, ct)).Select(a => a.ToString()).ToList(); } catch { return Bad("bad_input", "The name \"" + target + "\" does not resolve in DNS."); }
                if (ips.Count == 0) return Bad("bad_input", "The name \"" + target + "\" does not resolve in DNS.");
                hostname = ShortName(target); fqdn = target;
            }
            var ip = ips[0];
            var up = PingHost(ip); var tasks = Ports.Select(p => Tcp(ip, p.Port, 1500)).ToArray();
            await Task.WhenAll(tasks.Cast<Task>().Append(up));
            var open = Ports.Where((_, i) => tasks[i].Result).ToList();
            var g = GuessFrom(open.Select(p => p.Port).ToList());
            var mac = await ArpMac(ip);
            object? existing = null;
            await using (var db = await AgentsApi.Open(t, ct))
            {
                await using var c = new NpgsqlCommand("select id, name from assets.assets where ip_address = @ip or (hostname <> '' and lower(hostname) = lower(@h)) limit 1", db);
                AgentsApi.P(c, "ip", ip); AgentsApi.P(c, "h", hostname == "" ? "#" : hostname);
                await using var rd = await c.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct)) existing = new { id = rd.GetInt64(0), name = rd.GetString(1) };
            }
            await audit.WriteAsync("assets.discover", target, "probe", ct: ct);
            return Results.Ok(new { target, hostname, fqdn, ips, reachable = up.Result || open.Count > 0, open = open.Select(p => p.Port).ToList(), openNames = open.Select(p => p.Port + " " + p.Name).ToList(), mac, guess = g, existing });
        }));

        Admin(r.MapPost("/api/assets/discover/scan", async (JsonElement body, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var cidr = AgentsApi.Str(body, "cidr") ?? "";
            var ips = Expand(cidr, out var err); if (ips == null) return Bad("bad_input", err);
            HashSet<string> known;
            await using (var db = await AgentsApi.Open(t, ct))
            {
                await using var c = new NpgsqlCommand("select ip_address from assets.assets where ip_address <> ''", db); known = new();
                await using var rd = await c.ExecuteReaderAsync(ct); while (await rd.ReadAsync(ct)) known.Add(rd.GetString(0));
            }
            var probe = new[] { 22, 135, 445, 3389, 5985, 80, 443, 9100, 554 };
            var found = new List<object>(); var keyed = new List<(long K, object V)>();
            await Parallel.ForEachAsync(ips, new ParallelOptions { MaxDegreeOfParallelism = 32, CancellationToken = ct }, async (ip, c2) =>
            {
                var res = await Task.WhenAll(probe.Select(p => Tcp(ip, p, 700)));
                var open = probe.Where((_, i) => res[i]).ToList(); if (open.Count == 0) return;
                var name = ""; try { var h = await Dns.GetHostEntryAsync(ip, c2); if (h.HostName != ip) name = ShortName(h.HostName); } catch { }
                var g = GuessFrom(open);
                lock (keyed) keyed.Add((IpKey(ip), new { ip, hostname = name, open, cat = g.Cat, sub = g.Sub, known = known.Contains(ip) }));
            });
            await audit.WriteAsync("assets.discover", cidr, "scan: " + keyed.Count + " hosts answered", ct: ct);
            return Results.Ok(new { scanned = ips.Count, found = keyed.OrderBy(x => x.K).Select(x => x.V).ToList() });
        }));

        // ---------------------------------------------------------------- saved credentials
        Admin(r.MapGet("/api/assets/discover/creds", async (ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct); var o = new List<object>();
            await using var c = new NpgsqlCommand("select c.id, c.name, c.kind, c.username, c.company_id, coalesce(co.name,''), c.created_at, c.account_type from assets.creds c left join assets.companies co on co.id = c.company_id order by lower(c.name)", db);
            await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) o.Add(new { id = rd.GetInt32(0), name = rd.GetString(1), kind = rd.GetString(2), username = rd.GetString(3), companyId = rd.IsDBNull(4) ? (int?)null : rd.GetInt32(4), company = rd.GetString(5), createdAt = rd.GetDateTime(6), accountType = rd.GetString(7) });
            return Results.Ok(new { items = o });
        }));
        Admin(r.MapPost("/api/assets/discover/creds", async (JsonElement body, ITenantContext t, IConfiguration cfg, IAuditLog audit, CancellationToken ct) =>
        {
            var name = AgentsApi.Clip(AgentsApi.Str(body, "name"), 80).Trim(); var kindIn = AgentsApi.Str(body, "kind") ?? "windows";
            var kind = kindIn is "ssh" or "sshpw" or "snmp" or "login" ? kindIn : "windows";
            var user = AgentsApi.Clip(AgentsApi.Str(body, "username"), 120).Trim(); if (user == "" && kind == "snmp") user = "snmp";
            var secret = AgentsApi.Str(body, "secret") ?? "";
            if (name == "") return Bad("bad_input", "Give the credential a name.");
            if (user == "") return Bad("bad_input", "Enter the user name.");
            if (secret == "") return Bad("bad_input", kind == "ssh" ? "Paste the private key." : kind == "sshpw" ? "Enter the password." : kind == "snmp" ? "Enter the SNMP community string." : "Enter the password.");
            if (kind == "ssh" && !Regex.IsMatch(secret, "BEGIN [A-Z ]*PRIVATE KEY")) return Bad("bad_input", "That does not look like an SSH private key (PEM / OpenSSH format).");
            int? company = body.TryGetProperty("companyId", out var ce) && ce.ValueKind == JsonValueKind.Number ? ce.GetInt32() : null;
            var acct = AgentsApi.Str(body, "accountType") ?? ""; if (acct is not ("domain" or "service" or "local")) acct = "";
            await using var db = await AgentsApi.Open(t, ct);
            await using var c = new NpgsqlCommand("insert into assets.creds (name, kind, username, secret, company_id, account_type) values (@n,@k,@u,@s,@c,@at) returning id", db);
            AgentsApi.P(c, "at", acct); AgentsApi.P(c, "n", name); AgentsApi.P(c, "k", kind); AgentsApi.P(c, "u", user); AgentsApi.P(c, "s", Sealed(cfg, t, secret)); AgentsApi.P(c, "c", company);
            int id; try { id = (int)(await c.ExecuteScalarAsync(ct))!; } catch (PostgresException e) when (e.SqlState == "23503") { return Bad("bad_input", "Customer not found."); }
            await audit.WriteAsync("assets.credential_added", name, kind, ct: ct);
            return Results.Ok(new { id });
        }));
        Admin(r.MapPut("/api/assets/discover/creds/{id:int}", async (int id, JsonElement body, ITenantContext t, IConfiguration cfg, IAuditLog audit, CancellationToken ct) =>
        {
            var name = AgentsApi.Clip(AgentsApi.Str(body, "name"), 80).Trim(); var user = AgentsApi.Clip(AgentsApi.Str(body, "username"), 120).Trim();
            var secret = AgentsApi.Str(body, "secret") ?? "";   // empty keeps the saved one
            if (name == "") return Bad("bad_input", "Give the credential a name.");
            var acct = AgentsApi.Str(body, "accountType") ?? ""; if (acct is not ("domain" or "service" or "local")) acct = "";
            int? company = body.TryGetProperty("companyId", out var ce) && ce.ValueKind == JsonValueKind.Number ? ce.GetInt32() : null;
            await using var db = await AgentsApi.Open(t, ct);
            await using var c = new NpgsqlCommand("update assets.creds set name = @n, username = case when @u = '' then username else @u end, secret = case when @s::text is null then secret else @s end, company_id = @c, account_type = @at where id = @i", db);
            AgentsApi.P(c, "n", name); AgentsApi.P(c, "u", user); AgentsApi.P(c, "s", secret == "" ? null : Sealed(cfg, t, secret)); AgentsApi.P(c, "c", company); AgentsApi.P(c, "at", acct); AgentsApi.P(c, "i", id);
            try { if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound(); } catch (PostgresException e) when (e.SqlState == "23503") { return Bad("bad_input", "Customer not found."); }
            await audit.WriteAsync("assets.credential_changed", id.ToString(), name, ct: ct);
            return Results.Ok(new { ok = true });
        }));
        Admin(r.MapDelete("/api/assets/discover/creds/{id:int}", async (int id, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            await using var c = new NpgsqlCommand("delete from assets.creds where id = @i", db); AgentsApi.P(c, "i", id);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await audit.WriteAsync("assets.credential_removed", id.ToString(), "", ct: ct);
            return Results.NoContent();
        }));

        // ---------------------------------------------------------------- collect / fetch details
        Admin(r.MapPost("/api/assets/discover/collect", async (JsonElement body, ITenantContext t, IConfiguration cfg, IAuditLog audit, CancellationToken ct) =>
        {
            var target = Target(AgentsApi.Str(body, "target")); if (target == null) return Bad("bad_input", "Enter an IP address or a DNS name.");
            if (!body.TryGetProperty("credId", out var ci) || ci.ValueKind != JsonValueKind.Number) return Bad("bad_input", "Choose a saved credential.");
            await using var db = await AgentsApi.Open(t, ct);
            var cr = (await Creds(db, ci.GetInt32(), ct)).FirstOrDefault(); if (cr == null) return Bad("bad_input", "Choose a saved credential.");
            var secret = Unseal(cfg, t, cr.Secret); if (secret == "") return Bad("bad_input", "The saved credential cannot be decrypted (the key changed). Enter it again.");
            if (cr.Kind is not ("windows" or "ssh")) return Bad("bad_input", "That credential kind cannot read device details.");
            await audit.WriteAsync("assets.discover", target, "collect with credential \"" + cr.Name + "\"", ct: ct);
            try
            {
                var raw = await RunCollector(cr.Kind, cr.User, secret, target);
                var n = Inventory.Normalize(raw);
                return Results.Ok(new { raw, inventory = n, source = cr.Kind == "windows" ? "winrm" : "ssh", credential = cr.Name });
            }
            catch (InvalidOperationException e) { return Bad("collect_failed", e.Message); }
        }));

        // "Fetch details": the agent's data first, then agentless collection with a saved credential.
        Admin(r.MapPost("/api/assets/discover/lookup", async (JsonElement body, ITenantContext t, IConfiguration cfg, IAuditLog audit, CancellationToken ct) =>
        {
            var host = Target(AgentsApi.Str(body, "host")); if (host == null) return Bad("bad_input", "Enter an IP address or a DNS name.");
            await using var db = await AgentsApi.Open(t, ct);
            await using (var c = new NpgsqlCommand("select id, name, hostname, dns_name, ip_address, os_family, os_name, os_version, cpu_model, ram_bytes, total_storage_bytes, serial, manufacturer, model from assets.assets where agent_id <> '' and (lower(dns_name) = lower(@h) or lower(hostname) = lower(@h) or ip_address = @h or connected_ips ? @h) limit 1", db))
            {
                AgentsApi.P(c, "h", host); await using var rd = await c.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct))
                    return Results.Ok(new { source = "agent", existing = new { id = rd.GetInt64(0), name = rd.GetString(1) }, inventory = new { hostname = rd.GetString(2), dns_name = rd.GetString(3), ip_address = rd.GetString(4), os_family = rd.GetString(5), os_name = rd.GetString(6), os_version = rd.GetString(7), cpu_model = rd.GetString(8), ram_bytes = rd.GetInt64(9), total_storage_bytes = rd.GetInt64(10), serial_number = rd.GetString(11), make = rd.GetString(12), model = rd.GetString(13) } });
            }
            var all = await Creds(db, null, ct);
            int? want = body.TryGetProperty("credId", out var ci) && ci.ValueKind == JsonValueKind.Number ? ci.GetInt32() : null;
            var open = await Task.WhenAll(new[] { 5985, 5986, 22 }.Select(p => Tcp(host, p, 1500)));
            var use = want != null ? all.Where(c => c.Id == want).ToList() : all.Where(c => (c.Kind == "windows" && (open[0] || open[1])) || (c.Kind == "ssh" && open[2])).ToList();
            if (use.Count == 0) return Bad("no_credential", "No agent reports for " + host + " and no saved credential fits it. Install the agent on it, or add a credential.");
            string last = "Could not collect details from " + host + ".";
            foreach (var cr in use)
            {
                var secret = Unseal(cfg, t, cr.Secret); if (secret == "" || cr.Kind is not ("windows" or "ssh")) continue;
                try
                {
                    var raw = await RunCollector(cr.Kind, cr.User, secret, host);
                    await audit.WriteAsync("assets.discover", host, "fetched details with credential \"" + cr.Name + "\"", ct: ct);
                    return Results.Ok(new { raw, inventory = Inventory.Normalize(raw), source = cr.Kind == "windows" ? "winrm" : "ssh", credential = cr.Name });
                }
                catch (InvalidOperationException e) { last = e.Message; }
            }
            return Bad("collect_failed", last);
        }));

        // ---------------------------------------------------------------- adopt: save what was found as an asset
        Admin(r.MapPost("/api/assets/discover/adopt", async (JsonElement body, ClaimsPrincipal u, ITenantContext t, IEntitlements ent, IAuditLog audit, CancellationToken ct) =>
        {
            if (!body.TryGetProperty("probe", out var pr) || pr.ValueKind != JsonValueKind.Object) return Bad("bad_input", "probe is required");
            string S(JsonElement e, string n) => AgentsApi.Str(e, n) ?? "";
            string Or(params string[] v) { foreach (var x in v) if (!string.IsNullOrEmpty(x)) return x; return ""; }
            var g = pr.TryGetProperty("guess", out var ge) && ge.ValueKind == JsonValueKind.Object ? ge : default;
            var ips = pr.TryGetProperty("ips", out var ie) && ie.ValueKind == JsonValueKind.Array ? ie.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x != "").ToList() : new List<string>();
            var name = AgentsApi.Clip(Or(S(body, "name"), S(pr, "hostname"), S(pr, "target")), 160); if (name == "") return Bad("bad_input", "Give the asset a name.");
            var category = Or(S(body, "category"), g.ValueKind == JsonValueKind.Object ? S(g, "cat") : "");
            var ip = Or(ips.FirstOrDefault() ?? "", S(pr, "target")); var host = S(pr, "hostname");
            int? company = body.TryGetProperty("companyId", out var ce) && ce.ValueKind == JsonValueKind.Number ? ce.GetInt32() : null;
            await using var db = await AgentsApi.Open(t, ct);
            await using var tx = await db.BeginTransactionAsync(ct);
            long id;
            if (body.TryGetProperty("assetId", out var ae) && ae.ValueKind == JsonValueKind.Number)
            {
                id = ae.GetInt64();
                await using var c = new NpgsqlCommand("update assets.assets set ip_address = case when ip_address = '' then @ip else ip_address end, hostname = case when hostname = '' then @h else hostname end, category = case when category = '' then @cat else category end, updated_at = now(), updated_by = @a where id = @i", db, tx);
                AgentsApi.P(c, "ip", ip); AgentsApi.P(c, "h", host); AgentsApi.P(c, "cat", category); AgentsApi.P(c, "a", AgentsApi.Actor(u)); AgentsApi.P(c, "i", id);
                if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            }
            else
            {
                await using var cnt = new NpgsqlCommand("select count(*) from assets.assets where status not in ('retired','lost')", db, tx);
                var active = (long)(await cnt.ExecuteScalarAsync(ct))!;
                if (!await ent.WithinLimitAsync(key, "max_assets", active, ct)) return Results.Json(new { error = "limit_reached", limit = "max_assets" }, statusCode: 402);
                await using var c = new NpgsqlCommand("insert into assets.assets (name, status, company_id, category, hostname, ip_address, location, notes, source, created_by, updated_by) values (@n,'in_use',@co,@cat,@h,@ip,@loc,@no,'discover',@a,@a) returning id", db, tx);
                AgentsApi.P(c, "n", name); AgentsApi.P(c, "co", company); AgentsApi.P(c, "cat", category); AgentsApi.P(c, "h", host); AgentsApi.P(c, "ip", ip);
                AgentsApi.P(c, "loc", AgentsApi.Clip(S(body, "location"), 200)); AgentsApi.P(c, "no", AgentsApi.Clip(S(body, "notes"), 2000)); AgentsApi.P(c, "a", AgentsApi.Actor(u));
                id = (long)(await c.ExecuteScalarAsync(ct))!;
                await AssetsDevice.History(db, id, AgentsApi.Actor(u), "created", JsonSerializer.Serialize(new { via = "discover", target = S(pr, "target") }), ct, tx);
            }
            if (body.TryGetProperty("raw", out var raw) && raw.ValueKind == JsonValueKind.Object)
                await AssetsDevice.ApplyInventory(db, id, Inventory.Normalize(raw), AgentsApi.Str(body, "source") ?? "discover", AgentsApi.Actor(u), ct, tx);
            await tx.CommitAsync(ct);
            await audit.WriteAsync("assets.discover", id.ToString(), "adopted " + name, ct: ct);
            return Results.Ok(new { id });
        }));
    }
}
