using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace DeskAsk.Platform.Assets;

/// <summary>
/// The software repository (6.19.0): approved programs that engineers install on computers in one step. A program is an uploaded installer
/// (.msi or .exe for Windows; .deb, .rpm or .sh for Linux) kept by the server with its SHA-256, or a package name for winget, Chocolatey or
/// the Linux package manager. Installing queues an ordinary agent job: the computer downloads the installer over a link that works for one
/// day, checks the SHA-256, runs it silently and reports the exit code. No agent change is needed.
/// </summary>
public static class PackagesApi
{
    public const long MaxSize = 2L << 30;            // 2 GB per installer
    const int MaxPart = 8 << 20;                      // uploads come in pieces of up to 8 MB (proxies in front refuse large bodies)
    static readonly Regex SourceId = new(@"^[A-Za-z0-9][A-Za-z0-9.+_:~-]{0,127}$");
    static readonly Regex FileName = new(@"^[A-Za-z0-9][A-Za-z0-9 ._()+-]{0,150}\.(msi|exe|deb|rpm|sh)$", RegexOptions.IgnoreCase);
    static readonly Regex UploadId = new(@"^up[A-Za-z0-9_-]{20,40}$");   // "up" + AgentsApi.Rand(20): 27 characters of base64url
    static readonly string[] Kinds = { "file", "winget", "choco", "apt", "script" };
    const int MaxScript = 20000;

    /// <summary>Where installers are kept: Assets:PackageDir, or the install folder's data\packages, one folder per customer.</summary>
    internal static string Dir(IConfiguration cfg, ITenantContext t)
    {
        var root = cfg["Assets:PackageDir"] is { Length: > 0 } d ? d : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "data", "packages"));
        var dir = Path.Combine(root, Regex.Replace(t.Require().Id, "[^A-Za-z0-9_-]", "_"));
        Directory.CreateDirectory(dir);
        return dir;
    }
    static string FilePath(IConfiguration cfg, ITenantContext t, int id) => Path.Combine(Dir(cfg, t), id + ".pkg");
    static string PartPath(IConfiguration cfg, ITenantContext t, string uid) => Path.Combine(Dir(cfg, t), uid + ".part");

    static ITimeLimitedDataProtector Links(IDataProtectionProvider dp) => dp.CreateProtector("assets.package.link").ToTimeLimitedDataProtector();

    static string Row(NpgsqlDataReader r, int i) => r.GetString(i);
    const string Cols = "p.id, p.name, p.version, p.publisher, p.os, p.kind, p.source, p.file_name, p.size, p.sha256, p.args, p.notes, p.enabled, p.by_name, p.created_at, p.updated_at, (select count(*) from assets.package_installs i where i.package_id = p.id), p.rule, p.match_name, p.script";
    static object Out(NpgsqlDataReader r) => new
    {
        id = r.GetInt32(0), name = Row(r, 1), version = Row(r, 2), publisher = Row(r, 3), os = Row(r, 4), kind = Row(r, 5), source = Row(r, 6), fileName = Row(r, 7),
        size = r.GetInt64(8), sha256 = Row(r, 9), args = Row(r, 10), notes = Row(r, 11), enabled = r.GetBoolean(12), by = Row(r, 13), createdAt = r.GetDateTime(14), updatedAt = r.GetDateTime(15), installs = r.GetInt64(16), rule = r.GetString(17), matchName = r.GetString(18), script = r.GetString(19),
    };

    static string Text(JsonElement b, string n, int max)
    {
        if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty(n, out var v) || v.ValueKind != JsonValueKind.String) return "";
        var s = v.GetString()!.Trim();
        return s.Length > max ? s[..max] : s;
    }
    static bool Clean(string s) => !s.Any(char.IsControl);
    /// <summary>A script may have line breaks and tabs, but no other control characters.</summary>
    static bool CleanScript(string s) => !s.Any(ch => char.IsControl(ch) && ch is not ('\n' or '\r' or '\t'));
    static string RawScript(JsonElement b)
    {
        if (b.ValueKind != JsonValueKind.Object || !b.TryGetProperty("script", out var v) || v.ValueKind != JsonValueKind.String) return "";
        var s = v.GetString()!.Replace("\r\n", "\n").Trim('\n', ' ', '\t');
        return s.Length > MaxScript ? s[..MaxScript] : s;
    }

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        var admin = (RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);
        var staff = (RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin", "engineer")).RequireModule(key).RequireAccess(key, Acts.Modify);

        staff(r.MapGet("/api/assets/packages", async (string? os, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            await using var c = new NpgsqlCommand($"select {Cols} from assets.packages p where (@os = '' or p.os = @os) order by lower(p.name), p.version desc", db); AgentsApi.P(c, "os", os ?? "");
            var rows = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rows.Add(Out(rd));
            return Results.Ok(new { items = rows });
        }));

        // ---- an installer comes in pieces: start, then send each piece at its offset, then save the program with the upload's id
        admin(r.MapPost("/api/assets/packages/uploads", (IConfiguration cfg, ITenantContext t) =>
        {
            var uid = "up" + AgentsApi.Rand(20);
            File.WriteAllBytes(PartPath(cfg, t, uid), Array.Empty<byte>());
            return Results.Ok(new { id = uid, maxPart = MaxPart, maxSize = MaxSize });
        }));
        admin(r.MapPut("/api/assets/packages/uploads/{uid}", async (string uid, long offset, HttpContext http, IConfiguration cfg, ITenantContext t, CancellationToken ct) =>
        {
            if (!UploadId.IsMatch(uid)) return AgentsApi.Bad("bad_input", "Unknown upload.");
            var path = PartPath(cfg, t, uid); if (!File.Exists(path)) return Results.NotFound(new { error = "upload_not_found", detail = "The upload has ended. Start again." });
            var lim = http.Features.Get<IHttpMaxRequestBodySizeFeature>(); if (lim != null && !lim.IsReadOnly) lim.MaxRequestBodySize = MaxPart + 1024;
            await using var f = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
            if (offset != f.Length) return Results.Conflict(new { error = "wrong_offset", length = f.Length });   // the client resumes from here
            var buf = new MemoryStream(); await http.Request.Body.CopyToAsync(buf, ct);
            if (buf.Length == 0 || buf.Length > MaxPart) return AgentsApi.Bad("bad_input", "Send pieces of 1 byte to 8 MB.");
            if (f.Length + buf.Length > MaxSize) return AgentsApi.Bad("too_large", "An installer can be at most 2 GB.");
            buf.Position = 0; await buf.CopyToAsync(f, ct);
            return Results.Ok(new { length = f.Length });
        }));

        admin(r.MapPost("/api/assets/packages", async (JsonElement b, HttpContext http, IConfiguration cfg, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var name = Text(b, "name", 120); var version = Text(b, "version", 60); var publisher = Text(b, "publisher", 120); var os = Text(b, "os", 10); var kind = Text(b, "kind", 10);
            var source = Text(b, "source", 128); var args = Text(b, "args", 500); var notes = Text(b, "notes", 1000); var upload = Text(b, "upload", 40); var fileName = Text(b, "fileName", 160);
            if (name == "" || !Clean(name + version + publisher + args)) return AgentsApi.Bad("bad_input", "Give the program's name (no line breaks).");
            if (os is not ("windows" or "linux")) return AgentsApi.Bad("bad_input", "os must be windows or linux");
            if (!Kinds.Contains(kind)) return AgentsApi.Bad("bad_input", "kind must be file, winget, choco, apt or script");
            if ((os == "windows" && kind == "apt") || (os == "linux" && (kind == "winget" || kind == "choco"))) return AgentsApi.Bad("bad_input", "winget and Chocolatey are for Windows; the package manager kind is for Linux.");
            string sha = ""; long size = 0; string? part = null; var script = "";
            if (kind == "file")
            {
                if (!FileName.IsMatch(fileName)) return AgentsApi.Bad("bad_input", "The installer must be a .msi or .exe (Windows), or a .deb, .rpm or .sh (Linux), with a plain file name.");
                var ext = Path.GetExtension(fileName).ToLowerInvariant();
                if (os == "windows" ? ext is not (".msi" or ".exe") : ext is not (".deb" or ".rpm" or ".sh")) return AgentsApi.Bad("bad_input", "That kind of file does not install on " + (os == "windows" ? "Windows" : "Linux") + ".");
                if (!UploadId.IsMatch(upload) || !File.Exists(part = PartPath(cfg, t, upload))) return AgentsApi.Bad("bad_input", "Upload the installer first.");
                size = new FileInfo(part).Length; if (size == 0) return AgentsApi.Bad("bad_input", "The installer is empty.");
                await using (var fs = File.OpenRead(part)) sha = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
                var given = Text(b, "sha256", 64).ToUpperInvariant();
                if (given != "" && given != sha) { File.Delete(part); return AgentsApi.Bad("hash_mismatch", "The file that arrived does not match the SHA-256 you gave. Upload it again."); }
                source = "";
            }
            else if (kind == "script")
            {
                script = RawScript(b);
                if (script == "") return AgentsApi.Bad("bad_input", "Type the install script: PowerShell for Windows, bash for Linux.");
                if (!CleanScript(script)) return AgentsApi.Bad("bad_input", "The script has characters that are not text.");
                if (Text(b, "script", MaxScript + 10).Length > MaxScript) return AgentsApi.Bad("too_large", "A script can be at most 20,000 characters.");
                source = ""; fileName = "";
                sha = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(script)));   // so a change to the script shows in the audit log
            }
            else
            {
                if (!SourceId.IsMatch(source)) return AgentsApi.Bad("bad_input", kind == "apt" ? "Give the package name, for example htop." : "Give the package id, for example Google.Chrome (winget) or googlechrome (Chocolatey).");
                fileName = "";
            }
            await using var db = await AgentsApi.Open(t, ct);
            await using var c = new NpgsqlCommand(@"insert into assets.packages (name, version, publisher, os, kind, source, file_name, size, sha256, args, notes, by_name, script)
                values (@n, @v, @p, @o, @k, @s, @f, @z, @h, @a, @no, @by, @sc) returning id", db);
            AgentsApi.P(c, "sc", script);
            AgentsApi.P(c, "n", name); AgentsApi.P(c, "v", version); AgentsApi.P(c, "p", publisher); AgentsApi.P(c, "o", os); AgentsApi.P(c, "k", kind); AgentsApi.P(c, "s", source); AgentsApi.P(c, "f", fileName);
            AgentsApi.P(c, "z", size); AgentsApi.P(c, "h", sha); AgentsApi.P(c, "a", args); AgentsApi.P(c, "no", notes); AgentsApi.P(c, "by", AgentsApi.Actor(http.User));
            var id = (int)(await c.ExecuteScalarAsync(ct))!;
            if (part != null) File.Move(part, FilePath(cfg, t, id), true);
            await audit.WriteAsync("assets.package_added", id.ToString(), $"{name} {version} ({os}, {kind}{(sha != "" ? ", SHA-256 " + sha : ", " + source)})".Replace("  ", " "), ct: ct);
            return Results.Ok(new { id, sha256 = sha, size });
        }));

        // name, version, switches and notes can change; the installer itself is replaced by adding the new version
        admin(r.MapPut("/api/assets/packages/{id:int}", async (int id, JsonElement b, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            var name = Text(b, "name", 120); var args = Text(b, "args", 500);
            if (!Clean(name + args + Text(b, "version", 60) + Text(b, "publisher", 120))) return AgentsApi.Bad("bad_input", "No line breaks.");
            bool? enabled = b.TryGetProperty("enabled", out var en) && en.ValueKind is JsonValueKind.True or JsonValueKind.False ? en.GetBoolean() : null;
            string? rule = b.TryGetProperty("rule", out var ru) && ru.ValueKind == JsonValueKind.String ? ru.GetString()!.Trim() : null;
            if (rule != null && rule is not ("" or "required" or "prohibited")) return AgentsApi.Bad("bad_input", "rule must be empty, required or prohibited");
            var matchName = b.TryGetProperty("matchName", out _) ? Text(b, "matchName", 120) : null;
            string? newScript = b.TryGetProperty("script", out _) ? RawScript(b) : null;
            if (newScript != null && !CleanScript(newScript)) return AgentsApi.Bad("bad_input", "The script has characters that are not text.");
            if (matchName != null && !Clean(matchName)) return AgentsApi.Bad("bad_input", "No line breaks.");
            await using var db = await AgentsApi.Open(t, ct);
            await using var c = new NpgsqlCommand(@"update assets.packages set name = coalesce(nullif(@n, ''), name), version = coalesce(@v, version), publisher = coalesce(@p, publisher), args = coalesce(@a, args),
                notes = coalesce(@no, notes), enabled = coalesce(@en, enabled), rule = coalesce(@ru, rule), match_name = coalesce(@mn, match_name),
                script = case when kind = 'script' and @sc::text is not null and @sc::text <> '' then @sc::text else script end,
                sha256 = case when kind = 'script' and @sc::text is not null and @sc::text <> '' then @sh::text else sha256 end, updated_at = now() where id = @i", db);
            AgentsApi.P(c, "sc", newScript); AgentsApi.P(c, "sh", newScript == null || newScript == "" ? null : Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(newScript))));
            object? Opt(string n, int max) => b.TryGetProperty(n, out _) ? Text(b, n, max) : null;
            AgentsApi.P(c, "n", name); AgentsApi.P(c, "v", Opt("version", 60)); AgentsApi.P(c, "p", Opt("publisher", 120)); AgentsApi.P(c, "a", Opt("args", 500)); AgentsApi.P(c, "no", Opt("notes", 1000)); AgentsApi.P(c, "en", enabled); AgentsApi.P(c, "i", id);
            AgentsApi.P(c, "ru", rule); AgentsApi.P(c, "mn", matchName);
            if (await c.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await audit.WriteAsync("assets.package_changed", id.ToString(), name, ct: ct);
            return Results.Ok(new { id });
        }));

        admin(r.MapDelete("/api/assets/packages/{id:int}", async (int id, IConfiguration cfg, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            await using var c = new NpgsqlCommand("delete from assets.packages where id = @i returning name || ' ' || version", db); AgentsApi.P(c, "i", id);
            if (await c.ExecuteScalarAsync(ct) is not string what) return Results.NotFound();
            try { File.Delete(FilePath(cfg, t, id)); } catch (IOException) { }
            await audit.WriteAsync("assets.package_removed", id.ToString(), what.Trim(), ct: ct);
            return Results.NoContent();
        }));

        // ---- install on one computer or many: each gets its own job (live log, exit code) in the asset's Logs
        staff(r.MapPost("/api/assets/packages/{id:int}/install", async (int id, JsonElement b, HttpContext http, IConfiguration cfg, ITenantContext t, IDataProtectionProvider dp, AgentHub hub, IAuditLog audit, CancellationToken ct) =>
        {
            if (!b.TryGetProperty("assetIds", out var ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() is 0 or > 200) return AgentsApi.Bad("bad_input", "Choose 1 to 200 computers.");
            await using var db = await AgentsApi.Open(t, ct);
            string name, version, os, kind, source, fileName, sha, args, script; bool enabled;
            await using (var q = new NpgsqlCommand("select name, version, os, kind, source, file_name, sha256, args, enabled, script from assets.packages where id = @i", db))
            {
                AgentsApi.P(q, "i", id); await using var rd = await q.ExecuteReaderAsync(ct);
                if (!await rd.ReadAsync(ct)) return Results.NotFound();
                (name, version, os, kind, source, fileName, sha, args, enabled, script) = (rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetString(4), rd.GetString(5), rd.GetString(6), rd.GetString(7), rd.GetBoolean(8), rd.GetString(9));
            }
            if (!enabled) return AgentsApi.Bad("package_off", "This program is switched off in the repository.");
            if (kind == "file" && !File.Exists(FilePath(cfg, t, id))) return AgentsApi.Bad("file_missing", "The installer file is missing on the server. Add the program again.");
            var by = AgentsApi.Actor(http.User); var label = (name + " " + version).Trim();
            var queued = new List<object>(); var skipped = new List<object>();
            foreach (var e in ids.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt64(out var aid)) continue;
                var (err, tg) = await ServicesApi.Target(http, db, aid, cfg, ct);
                if (err != null || tg == null) { skipped.Add(new { assetId = aid, reason = Reason(err) }); continue; }
                if (tg.Platform != os) { skipped.Add(new { assetId = aid, name = tg.AssetName, reason = "This program is for " + (os == "windows" ? "Windows" : "Linux") + " computers." }); continue; }
                var link = "";
                if (kind == "file")
                {
                    var token = Links(dp).Protect(t.Require().Id + "|" + id + "|" + aid, TimeSpan.FromDays(1));
                    link = AgentsApi.PublicUrl(http, cfg) + "/agent/v1/packages/" + Uri.EscapeDataString(token);
                }
                var win = os == "windows";
                var body = kind switch
                {
                    "file" => win ? WinFile(link, sha, fileName, args) : LinFile(link, sha, fileName, args),
                    "winget" => Winget(source, args),
                    "choco" => Choco(source, args),
                    "script" => win ? WinScript(script) : LinScript(script),
                    _ => LinPackage(source),
                };
                var jid = await ServicesApi.Queue(db, hub, t, tg, win ? "powershell" : "bash", "Install " + AgentsApi.Clip(label, 80), body, 3600, by, ct);
                await using (var ins = new NpgsqlCommand("insert into assets.package_installs (job_id, package_id, asset_id, by_name) values (@j, @p, @a, @b)", db))
                { AgentsApi.P(ins, "j", jid); AgentsApi.P(ins, "p", id); AgentsApi.P(ins, "a", aid); AgentsApi.P(ins, "b", by); await ins.ExecuteNonQueryAsync(ct); }
                await AssetsDevice.History(db, aid, by, "install", JsonSerializer.Serialize(new { program = label, package = id }), ct);
                queued.Add(new { assetId = aid, name = tg.AssetName, job = jid });
            }
            await audit.WriteAsync("assets.package_install", id.ToString(), $"{label} on {queued.Count} computer(s), {skipped.Count} skipped", ct: ct);
            return Results.Ok(new { queued, skipped });
        }));

        staff(r.MapGet("/api/assets/packages/{id:int}/installs", async (int id, ITenantContext t, CancellationToken ct) =>
        {
            await using var db = await AgentsApi.Open(t, ct);
            await using var c = new NpgsqlCommand(@"select i.job_id, i.asset_id, coalesce(a.name, ''), i.at, i.by_name, coalesce(j.status, ''), j.exit_code
                from assets.package_installs i left join assets.assets a on a.id = i.asset_id left join assets.jobs j on j.id = i.job_id where i.package_id = @p order by i.at desc limit 200", db);
            AgentsApi.P(c, "p", id);
            var rows = new List<object>(); await using var rd = await c.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rows.Add(new { job = rd.GetString(0), assetId = rd.GetInt64(1), asset = rd.GetString(2), at = rd.GetDateTime(3), by = rd.GetString(4), status = rd.GetString(5), exit = rd.IsDBNull(6) ? (int?)null : rd.GetInt32(6) });
            return Results.Ok(new { items = rows });
        }));

        // ---- the computer downloads the installer with the one-day link from its job (no agent key needed: the link is the permission)
        r.MapGet("/agent/v1/packages/{token}", async (string token, IConfiguration cfg, ITenantContext t, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            string plain;
            try { plain = Links(dp).Unprotect(token); } catch (CryptographicException) { return Results.NotFound(); }
            var parts = plain.Split('|');
            if (parts.Length != 3 || parts[0] != t.Require().Id || !int.TryParse(parts[1], out var id)) return Results.NotFound();
            await using var db = await AgentsApi.Open(t, ct);
            await using var c = new NpgsqlCommand("select file_name from assets.packages where id = @i and kind = 'file' and enabled", db); AgentsApi.P(c, "i", id);
            if (await c.ExecuteScalarAsync(ct) is not string file) return Results.NotFound();
            var path = FilePath(cfg, t, id); if (!File.Exists(path)) return Results.NotFound();
            return Results.File(path, "application/octet-stream", file, enableRangeProcessing: true);
        }).RequireFoundation().RequireModule(key);
    }

    static string Reason(IResult? err) => err switch
    {
        IStatusCodeHttpResult { StatusCode: 404 } => "Not found or not yours to change.",
        IValueHttpResult v when v.Value != null && JsonSerializer.SerializeToElement(v.Value) is { ValueKind: JsonValueKind.Object } j && j.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String => d.GetString()!,
        _ => "The computer cannot take commands now.",
    };

    // ---------------------------------------------------------------- what runs on the computer
    static string Ps(string s) => ServicesApi.Ps(s);
    static string Sh(string s) => ServicesApi.Sh(s);

    static string WinFile(string url, string sha, string file, string args) => "$Url = " + Ps(url) + "\n$Sha = " + Ps(sha) + "\n$File = " + Ps(file) + "\n$Switches = " + Ps(args) + @"
$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$dir = Join-Path $env:ProgramData 'DeskAskAgent\packages'; New-Item -ItemType Directory -Force -Path $dir | Out-Null
$f = Join-Path $dir $File
""Downloading $File ...""
Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $f
$h = (Get-FileHash -Path $f -Algorithm SHA256).Hash
if ($h -ne $Sha) { Remove-Item $f -Force; throw ""The download does not match the repository's copy (SHA-256 $h). Nothing was installed."" }
""Installing (SHA-256 checked) ...""
if ($f -like '*.msi') { $argv = '/i ""' + $f + '"" /qn /norestart ' + $Switches; $proc = Start-Process -FilePath ""$env:SystemRoot\System32\msiexec.exe"" -ArgumentList $argv -PassThru -WindowStyle Hidden }
elseif ($Switches) { $proc = Start-Process -FilePath $f -ArgumentList $Switches -PassThru -WindowStyle Hidden }
else { $proc = Start-Process -FilePath $f -PassThru -WindowStyle Hidden }
if (-not $proc.WaitForExit(3300000)) { try { $proc.Kill() } catch { }; throw 'The installer did not finish within 55 minutes and was stopped. It may be waiting for a person: add its silent switches to the program.' }
$code = $proc.ExitCode
Remove-Item $f -Force -ErrorAction SilentlyContinue
if (@(0, 1641, 3010) -notcontains $code) { ""The installer stopped with code $code.""; exit $code }
if ($code -eq 0) { 'Installed.' } else { 'Installed. Restart the computer to finish.' }";

    static string Winget(string id, string args) => "$Id = " + Ps(id) + "\n$Switches = " + Ps(args) + @"
$ErrorActionPreference = 'Stop'
# the agent runs as SYSTEM, where winget is not on the PATH: use App Installer's own copy
$w = Get-ChildItem ""$env:ProgramFiles\WindowsApps\Microsoft.DesktopAppInstaller_*_x64__8wekyb3d8bbwe\winget.exe"" -ErrorAction SilentlyContinue | Sort-Object FullName | Select-Object -Last 1 -ExpandProperty FullName
if (-not $w) { $w = (Get-Command winget.exe -ErrorAction SilentlyContinue).Source }
if (-not $w) { throw 'winget (App Installer) is not on this computer. Add the program as an uploaded installer instead.' }
$argv = @('install', '--id', $Id, '--exact', '--silent', '--scope', 'machine', '--accept-package-agreements', '--accept-source-agreements', '--disable-interactivity')
if ($Switches) { $argv += @('--override', $Switches) }
& $w @argv
$code = $LASTEXITCODE
if ($code -eq -1978335189) { 'It is already installed and up to date.'; exit 0 }
if ($code -ne 0) { ""winget stopped with code $code.""; exit 1 }
'Installed.'";

    static string Choco(string id, string args) => "$Id = " + Ps(id) + "\n$Switches = " + Ps(args) + @"
$ErrorActionPreference = 'Stop'
$c = Join-Path $env:ProgramData 'chocolatey\bin\choco.exe'
if (-not (Test-Path $c)) { throw 'Chocolatey is not on this computer. Add the program as an uploaded installer instead.' }
$argv = @('install', $Id, '-y', '--no-progress')
if ($Switches) { $argv += @('--install-arguments', $Switches) }
& $c @argv
$code = $LASTEXITCODE
if (@(0, 1641, 3010) -notcontains $code) { ""Chocolatey stopped with code $code.""; exit 1 }
'Installed.'";

    static string LinFile(string url, string sha, string file, string args) => "URL=" + Sh(url) + "\nSHA=" + Sh(sha.ToLowerInvariant()) + "\nFILE=" + Sh(file) + "\nSWITCHES=" + Sh(args) + @"
export DEBIAN_FRONTEND=noninteractive
d=/var/tmp/deskask-packages; mkdir -p ""$d""; f=""$d/$FILE""
echo ""Downloading $FILE ...""
if command -v curl >/dev/null 2>&1; then curl -fsSL ""$URL"" -o ""$f""; else wget -q -O ""$f"" ""$URL""; fi || { echo ""The download failed.""; exit 1; }
echo ""$SHA  $f"" | sha256sum -c - >/dev/null 2>&1 || { rm -f ""$f""; echo ""The download does not match the repository's copy. Nothing was installed.""; exit 1; }
echo ""Installing (SHA-256 checked) ...""
case ""$f"" in
  *.deb) if command -v apt-get >/dev/null 2>&1; then apt-get install -y ""$f""; else dpkg -i ""$f""; fi ;;
  *.rpm) if command -v dnf >/dev/null 2>&1; then dnf -y install ""$f""; elif command -v yum >/dev/null 2>&1; then yum -y localinstall ""$f""; else zypper --non-interactive install ""$f""; fi ;;
  *.sh) bash ""$f"" $SWITCHES ;;
esac
rc=$?; rm -f ""$f""
[ $rc -eq 0 ] && echo ""Installed."" || echo ""The installer stopped with code $rc.""; exit $rc";

    /// <summary>The administrator's own PowerShell install script: stops at the first error and reports the exit code (0, 1641 and 3010 are success).</summary>
    static string WinScript(string script) => "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'\n" + script + "\nif ($LASTEXITCODE -and @(0, 1641, 3010) -notcontains $LASTEXITCODE) { exit $LASTEXITCODE }";
    static string LinScript(string script) => "set -e\n" + script;

    static string LinPackage(string pkg) => "PKG=" + Sh(pkg) + @"
export DEBIAN_FRONTEND=noninteractive
if command -v apt-get >/dev/null 2>&1; then apt-get update -qq && apt-get install -y -- ""$PKG""
elif command -v dnf >/dev/null 2>&1; then dnf -y install -- ""$PKG""
elif command -v yum >/dev/null 2>&1; then yum -y install -- ""$PKG""
elif command -v zypper >/dev/null 2>&1; then zypper --non-interactive install -- ""$PKG""
else echo ""No supported package manager (apt, dnf, yum, zypper) was found.""; exit 1; fi
rc=$?; [ $rc -eq 0 ] && echo ""$PKG is installed.""; exit $rc";
}
