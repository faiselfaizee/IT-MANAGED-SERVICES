using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DeskAsk.Platform.Updates;

/// <summary>
/// Updates (ADR 0009). The platform never replaces its own files. An administrator who signed in with two-step sign-in uploads a signed
/// bundle; this module only drops it in the inbox folder. The separate deskask-updater service verifies the signature itself, swaps the
/// files, tests the new version and goes back to the old one when the test fails. Status files written by the updater are shown here.
///
/// Folder (Updates:Dir): inbox/&lt;id&gt;/package.dkb + job.json (written last), state/status.json, state/jobs/&lt;id&gt;.json
/// </summary>
public sealed class UpdatesModule : IPlatformModule
{
    public string Key => "updates";
    public string Schema => "core";
    public const long MaxPackage = 1000L * 1024 * 1024;
    static readonly Regex IdRx = new("^[a-f0-9]{16,40}$", RegexOptions.Compiled);

    public void AddServices(IServiceCollection s, IConfiguration cfg) { }

    static string? Dir(IConfiguration c) => string.IsNullOrWhiteSpace(c["Updates:Dir"]) ? null : c["Updates:Dir"];

    static JsonElement? ReadJson(string path)
    {
        try
        {
            var fi = new FileInfo(path); if (!fi.Exists || fi.Length > 1024 * 1024) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var d = JsonDocument.Parse(fs);
            return d.RootElement.Clone();
        }
        catch (Exception) { return null; }
    }

    public void MapEndpoints(IEndpointRouteBuilder r)
    {
        r.MapGet("/api/platform/updates", (IConfiguration cfg) =>
        {
            var dir = Dir(cfg);
            if (dir == null || !Directory.Exists(Path.Combine(dir, "inbox"))) return Results.Ok(new { installed = false });
            var status = ReadJson(Path.Combine(dir, "state", "status.json"));
            var alive = status is { } st && st.TryGetProperty("updated", out var u) && u.ValueKind == JsonValueKind.Number
                        && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - u.GetInt64() < 3 * 60_000L;
            var jobs = new List<JsonElement>(); var seen = new HashSet<string>();
            var jd = Path.Combine(dir, "state", "jobs");
            if (Directory.Exists(jd))
                foreach (var f in new DirectoryInfo(jd).GetFiles("*.json").OrderByDescending(x => x.LastWriteTimeUtc).Take(30))
                    if (ReadJson(f.FullName) is { } j) { jobs.Add(j); seen.Add(Path.GetFileNameWithoutExtension(f.Name)); }
            var queued = new List<object>();
            foreach (var d in new DirectoryInfo(Path.Combine(dir, "inbox")).GetDirectories())
                if (IdRx.IsMatch(d.Name) && !seen.Contains(d.Name) && ReadJson(Path.Combine(d.FullName, "job.json")) is { } q)
                    queued.Add(new { id = d.Name, file = q.TryGetProperty("file", out var fe) ? fe.GetString() : "", by = q.TryGetProperty("by", out var be) ? be.GetString() : "" });
            return Results.Ok(new { installed = true, serviceAlive = alive, status, jobs, queued });
        }).RequireAuthorization(p => p.RequireRole("admin"));

        r.MapPost("/api/platform/updates", async (HttpContext http, IConfiguration cfg, IAuditLog audit, CancellationToken ct) =>
        {
            var dir = Dir(cfg);
            if (dir == null || !Directory.Exists(Path.Combine(dir, "inbox"))) return Results.Json(new { error = "updates_not_set_up", reason = "The updater service is not set up on this server (Updates:Dir)." }, statusCode: 409);
            // an update runs as the server's own program: only an administrator who passed the second step may start one
            if (!http.User.HasClaim("amr", "otp")) return Results.Json(new { error = "two_step_required", reason = "Sign in with two-step sign-in to upload an update." }, statusCode: 403);

            var name = Regex.Replace(Path.GetFileName(http.Request.Query["name"].ToString()), @"[^A-Za-z0-9._ ()-]", "_");
            if (name == "") name = "package.dkb";
            if (!name.EndsWith(".dkb", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { error = "not_a_bundle", reason = "Choose a .dkb release bundle." });
            var inbox = Path.Combine(dir, "inbox");
            // one package at a time: a folder with its job.json that the updater has not started on yet
            if (new DirectoryInfo(inbox).GetDirectories().Any(d => IdRx.IsMatch(d.Name) && File.Exists(Path.Combine(d.FullName, "job.json"))
                                                                  && !File.Exists(Path.Combine(dir, "state", "jobs", d.Name + ".json"))))
                return Results.Json(new { error = "busy", reason = "Another package is waiting for the updater." }, statusCode: 409);

            var lim = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (lim != null && !lim.IsReadOnly) lim.MaxRequestBodySize = MaxPackage + (1L << 20);

            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            var folder = Path.Combine(inbox, id); Directory.CreateDirectory(folder);
            var ok = false; long total = 0; string sha;
            try
            {
                using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var head = new byte[2]; var headLen = 0;
                await using (var fs = new FileStream(Path.Combine(folder, "package.dkb"), FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buf = new byte[81920]; int n;
                    while ((n = await http.Request.Body.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
                    {
                        if (headLen < 2) { var c = Math.Min(2 - headLen, n); Array.Copy(buf, 0, head, headLen, c); headLen += c; }
                        total += n;
                        if (total > MaxPackage) return Results.BadRequest(new { error = "too_large" });
                        h.AppendData(buf, 0, n); await fs.WriteAsync(buf.AsMemory(0, n), ct);
                    }
                }
                if (total < 100 || head[0] != (byte)'P' || head[1] != (byte)'K') return Results.BadRequest(new { error = "not_a_bundle", reason = "This is not a release bundle." });
                sha = Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
                var by = http.User.FindFirstValue("email") ?? http.User.FindFirstValue("sub") ?? "";
                var force = http.Request.Query["force"] == "1";
                var job = JsonSerializer.Serialize(new { id, file = name, by, requested = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), force });
                var part = Path.Combine(folder, "job.json.part");
                await File.WriteAllTextAsync(part, job, new UTF8Encoding(false), ct);
                File.Move(part, Path.Combine(folder, "job.json"));   // last: from now on the updater may take it
                ok = true;
            }
            finally { if (!ok) { try { Directory.Delete(folder, true); } catch (Exception) { } } }
            await audit.WriteAsync("update.uploaded", name, $"{total} bytes, SHA-256 {sha}", ct: ct);
            return Results.Json(new { ok = true, id, sha256 = sha, size = total, note = "The updater verifies the signature before it changes anything." }, statusCode: 202);
        }).RequireAuthorization(p => p.RequireRole("admin"));

        r.MapDelete("/api/platform/updates/{id}", async (string id, IConfiguration cfg, IAuditLog audit, CancellationToken ct) =>
        {
            var dir = Dir(cfg);
            if (dir == null || !IdRx.IsMatch(id)) return Results.BadRequest(new { error = "bad_id" });
            var folder = Path.Combine(dir, "inbox", id);
            if (!Directory.Exists(folder)) return Results.NotFound();
            if (File.Exists(Path.Combine(dir, "state", "jobs", id + ".json"))) return Results.Json(new { error = "started", reason = "The updater has already started on it." }, statusCode: 409);
            try { Directory.Delete(folder, true); } catch (Exception e) { return Results.Json(new { error = "cannot_remove", reason = e.Message }, statusCode: 409); }
            await audit.WriteAsync("update.cancelled", id, ct: ct);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(p => p.RequireRole("admin"));
    }
}
