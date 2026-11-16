using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DeskAsk.Platform.Backup;

/// <summary>
/// File backups made by the agent: a plan says which folders of a device go where (a folder or USB disk, an SMB share, S3-compatible
/// storage, Azure Blob), how often and how long they are kept. The storage secrets and the backup password are sealed here and handed to
/// the device's agent over its signed-in connection; the password can be shown to an administrator (recorded) to restore without DeskAsk.
/// </summary>
public static class PlansApi
{
    public const string Purpose = "dk.backup.plan";
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public sealed record Plan(long Id, int? CompanyId, long AssetId, string AssetName, string Name, string[] Paths, string[] Excludes, int EveryHours, int AtHour,
        int KeepLast, int KeepDays, bool UseVss, bool Enabled, string StorageKind, string StorageWhere, string StorageSealed, string PasswordSealed, DateTime Updated);

    internal static async Task<List<Plan>> Load(NpgsqlConnection db, string where, Action<NpgsqlCommand> bind, CancellationToken ct)
    {
        await using var c = new NpgsqlCommand($@"select id, company_id, asset_id, asset_name, name, paths, excludes, every_hours, at_hour, keep_last, keep_days, use_vss, enabled,
                storage_kind, storage_where, storage_sealed, password_sealed, updated_at from backup.plans where {where} order by asset_name, name", db);
        bind(c);
        var o = new List<Plan>(); await using var rd = await c.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            o.Add(new Plan(rd.GetInt64(0), rd.IsDBNull(1) ? null : rd.GetInt32(1), rd.GetInt64(2), rd.GetString(3), rd.GetString(4), (string[])rd.GetValue(5), (string[])rd.GetValue(6),
                rd.GetInt32(7), rd.GetInt32(8), rd.GetInt32(9), rd.GetInt32(10), rd.GetBoolean(11), rd.GetBoolean(12), rd.GetString(13), rd.GetString(14), rd.GetString(15), rd.GetString(16), rd.GetDateTime(17)));
        return o;
    }

    /// <summary>Where the backups go, without secrets.</summary>
    static string Where(JsonObject s) => (string?)s["kind"] switch
    {
        "local" or "smb" => (string?)s["path"] ?? "",
        "s3" => $"{s["bucket"]}{(string.IsNullOrEmpty((string?)s["prefix"]) ? "" : "/" + s["prefix"])} at {(string.IsNullOrEmpty((string?)s["endpoint"]) ? "Amazon S3 " + s["region"] : s["endpoint"])}",
        "azure" => ((string?)s["sasUrl"] ?? "").Split('?')[0] + (string.IsNullOrEmpty((string?)s["prefix"]) ? "" : "/" + s["prefix"]),
        _ => "",
    };

    static string S(JsonElement b, string n) => b.ValueKind == JsonValueKind.Object && b.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

    /// <summary>The storage from the request, keeping the saved secrets where the request leaves them empty. Null and a message when it is not usable.</summary>
    internal static (JsonObject? Storage, string? Error) ReadStorage(JsonElement b, JsonObject? old)
    {
        if (!b.TryGetProperty("storage", out var s) || s.ValueKind != JsonValueKind.Object) return (null, "Choose where the backups go.");
        var kind = S(s, "kind");
        string Keep(string n) { var v = S(s, n); return v != "" ? v : old != null && (string?)old["kind"] == kind ? (string?)old[n] ?? "" : ""; }
        var o = new JsonObject { ["kind"] = kind };
        switch (kind)
        {
            case "local":
                var lp = S(s, "path");
                if (lp.Length < 2 || lp.Length > 400) return (null, "Give the backup folder, for example D:\\Backups or E:\\ for a USB disk.");
                if (lp.StartsWith(@"\\") || lp.StartsWith("//")) return (null, "That is a share: choose SMB share.");
                o["path"] = lp; break;
            case "smb":
                var sp = S(s, "path").Replace('/', '\\');
                var parts = sp.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
                if (!sp.StartsWith(@"\\") || parts.Length < 2 || sp.Length > 400) return (null, "Give the share as \\\\server\\share or \\\\server\\share\\folder.");
                o["path"] = sp; o["user"] = S(s, "user");
                var pw = Keep("password"); if (S(s, "user") != "" && pw == "") return (null, "Give the password of the share's account.");
                o["password"] = S(s, "user") == "" ? "" : pw; break;
            case "s3":
                var ep = S(s, "endpoint");
                if (ep != "" && !(ep.StartsWith("https://") || ep.StartsWith("http://")) ) return (null, "The endpoint starts with https:// (for example https://s3.wasabisys.com).");
                var bucket = S(s, "bucket"); if (bucket.Length < 3 || bucket.Length > 63) return (null, "Give the bucket name.");
                var ak = S(s, "accessKey"); if (ak == "") return (null, "Give the access key.");
                var sk = Keep("secretKey"); if (sk == "") return (null, "Give the secret key.");
                o["endpoint"] = ep; o["region"] = S(s, "region") == "" ? "us-east-1" : S(s, "region"); o["bucket"] = bucket; o["prefix"] = S(s, "prefix").Trim('/');
                o["accessKey"] = ak; o["secretKey"] = sk;
                o["pathStyle"] = s.TryGetProperty("pathStyle", out var psv) && psv.ValueKind == JsonValueKind.True; break;
            case "azure":
                var sas = Keep("sasUrl");
                if (!Uri.TryCreate(sas, UriKind.Absolute, out var u) || u.Scheme != "https" || !u.Query.Contains("sig=")) return (null, "Give the container's SAS address (https://account.blob.core.windows.net/container?sv=...&sig=...).");
                o["sasUrl"] = sas; o["prefix"] = S(s, "prefix").Trim('/'); break;
            default: return (null, "Where the backups go must be a folder or disk, an SMB share, S3 storage or Azure Blob.");
        }
        return (o, null);
    }

    static object Shape(Plan p, IReadOnlyDictionary<long, (int Count, DateTime? Last, long Bytes)> snaps, IReadOnlyDictionary<long, (string Status, long JobId)> jobs)
    {
        snaps.TryGetValue(p.Id, out var sn); jobs.TryGetValue(p.Id, out var jb);
        return new
        {
            id = p.Id, companyId = p.CompanyId, assetId = p.AssetId, assetName = p.AssetName, name = p.Name, paths = p.Paths, excludes = p.Excludes, everyHours = p.EveryHours,
            atHour = p.AtHour, keepLast = p.KeepLast, keepDays = p.KeepDays, useVss = p.UseVss, enabled = p.Enabled, storageKind = p.StorageKind, storageWhere = p.StorageWhere,
            updated = p.Updated, snapshots = sn.Count, lastSnapshot = sn.Last, lastBytes = sn.Bytes, status = jb.Status ?? "", jobId = jb.JobId == 0 ? (long?)null : jb.JobId,
        };
    }

    static async Task<(Dictionary<long, (int, DateTime?, long)>, Dictionary<long, (string, long)>)> Extras(NpgsqlConnection db, IReadOnlyList<Plan> plans, int grace, CancellationToken ct)
    {
        var snaps = new Dictionary<long, (int, DateTime?, long)>();
        await using (var c = new NpgsqlCommand(@"select plan_id, count(*)::int, max(at), (array_agg(bytes order by at desc))[1] from backup.snapshots group by plan_id", db))
        await using (var rd = await c.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct)) snaps[rd.GetInt64(0)] = (rd.GetInt32(1), rd.IsDBNull(2) ? null : rd.GetDateTime(2), rd.IsDBNull(3) ? 0 : rd.GetInt64(3));
        var jobs = new Dictionary<long, (string, long)>(); var now = DateTime.UtcNow;
        await using (var c = new NpgsqlCommand("select id, asset_id, lower(name), every_hours, watched, last_result, last_at, created_at from backup.jobs where tool = 'deskask'", db))
        await using (var rd = await c.ExecuteReaderAsync(ct))
        {
            var byKey = new Dictionary<(long, string), (string, long)>();
            while (await rd.ReadAsync(ct))
            {
                if (rd.IsDBNull(1)) continue;
                var st = BackupChecker.Status(rd.GetBoolean(4), rd.GetInt32(3), rd.GetString(5), rd.IsDBNull(6) ? null : rd.GetDateTime(6), rd.GetDateTime(7), grace, now);
                byKey[(rd.GetInt64(1), rd.GetString(2))] = (st, rd.GetInt64(0));
            }
            foreach (var p in plans) if (byKey.TryGetValue((p.AssetId, p.Name.Trim().ToLowerInvariant()), out var j)) jobs[p.Id] = j;
        }
        return (snaps, jobs);
    }

    static async Task<Plan?> One(HttpContext http, NpgsqlConnection db, long id, string act, CancellationToken ct)
    {
        var p = (await Load(db, "id = @i", c => BackupModule.P(c, "i", id), ct)).FirstOrDefault(); if (p == null) return null;
        var scope = await http.RequestServices.GetRequiredService<IAccess>().ScopeAsync(http.User, Modules.Backup, act, ct);
        return scope == null || (p.CompanyId is int co && scope.Contains(co)) ? p : null;
    }

    static string[] Lines(JsonElement b, string n, int max, int len)
    {
        if (!b.TryGetProperty(n, out var v)) return Array.Empty<string>();
        IEnumerable<string> raw = v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "")
            : v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Split('\n') : Array.Empty<string>();
        return raw.Select(x => x.Trim()).Where(x => x != "" && x.Length <= len).Distinct(StringComparer.OrdinalIgnoreCase).Take(max).ToArray();
    }

    static int Int(JsonElement b, string n, int def) => b.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : def;
    static bool Bool(JsonElement b, string n, bool def) => b.TryGetProperty(n, out var v) ? v.ValueKind == JsonValueKind.True || (v.ValueKind != JsonValueKind.False && def) : def;

    public static void Map(IEndpointRouteBuilder r, string key)
    {
        RouteHandlerBuilder Read(RouteHandlerBuilder b) => b.RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Read);
        RouteHandlerBuilder Modify(RouteHandlerBuilder b) => b.RequireAuthorization().RequireModule(key).RequireAccess(key, Acts.Modify);
        RouteHandlerBuilder Admin(RouteHandlerBuilder b) => b.RequireAuthorization(p => p.RequireRole("admin")).RequireModule(key);

        Read(r.MapGet("/api/backup/plans", async (HttpContext http, ITenantContext t, ISettings settings, CancellationToken ct) =>
        {
            var scope = await http.RequestServices.GetRequiredService<IAccess>().ScopeAsync(http.User, Modules.Backup, Acts.Read, ct);
            long.TryParse(http.Request.Query["asset"].ToString(), out var asset);
            await using var db = await BackupModule.Open(t, ct);
            var plans = await Load(db, "(not @sc or company_id = any(@scope)) and (@a = 0 or asset_id = @a)", c =>
            { BackupModule.P(c, "sc", scope != null); BackupModule.P(c, "scope", (scope ?? Array.Empty<int>()).ToArray()); BackupModule.P(c, "a", asset); }, ct);
            var (snaps, jobs) = await Extras(db, plans, (await BackupSettings.LoadAsync(settings, ct)).GraceHours, ct);
            return Results.Ok(plans.Select(p => Shape(p, snaps, jobs)));
        }));

        Read(r.MapGet("/api/backup/plans/{id:long}", async (long id, HttpContext http, ITenantContext t, ISettings settings, IDataProtectionProvider dp, CancellationToken ct) =>
        {
            await using var db = await BackupModule.Open(t, ct);
            var p = await One(http, db, id, Acts.Read, ct); if (p == null) return Results.NotFound();
            var (sn, jobs) = await Extras(db, new[] { p }, (await BackupSettings.LoadAsync(settings, ct)).GraceHours, ct);
            var snaps = new List<object>(); var restores = new List<object>();
            await using (var c = new NpgsqlCommand("select id, at, files, bytes, new_bytes, warnings from backup.snapshots where plan_id = @p order by at desc limit 200", db))
            {
                BackupModule.P(c, "p", id); await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) snaps.Add(new { id = rd.GetString(0), at = rd.GetDateTime(1), files = rd.GetInt32(2), bytes = rd.GetInt64(3), newBytes = rd.GetInt64(4), warnings = rd.GetInt32(5) });
            }
            await using (var c = new NpgsqlCommand("select id, snapshot_id, include, target, state, message, files, bytes, by_name, created_at, done_at from backup.restores where plan_id = @p order by id desc limit 20", db))
            {
                BackupModule.P(c, "p", id); await using var rd = await c.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                    restores.Add(new { id = rd.GetInt64(0), snapshot = rd.GetString(1), include = (string[])rd.GetValue(2), target = rd.GetString(3), state = rd.GetString(4), message = rd.GetString(5),
                        files = rd.GetInt32(6), bytes = rd.GetInt64(7), by = rd.GetString(8), created = rd.GetDateTime(9), done = rd.IsDBNull(10) ? (DateTime?)null : rd.GetDateTime(10) });
            }
            // for administrators changing the plan: the storage settings without their secrets
            JsonObject? edit = null;
            if (http.User.IsInRole("admin") && JsonNode.Parse(dp.CreateProtector(Purpose + ":" + t.Require().Id).Unprotect(p.StorageSealed)) is JsonObject so)
            {
                foreach (var secret in new[] { "password", "secretKey", "sasUrl" }) so.Remove(secret);
                edit = so;
            }
            return Results.Ok(new { plan = Shape(p, sn, jobs), snapshots = snaps, restores, storageEdit = edit });
        }));

        async Task<IResult> Save(long? id, JsonElement b, HttpContext http, ITenantContext t, IDataProtectionProvider dp, IAuditLog audit, CancellationToken ct)
        {
            var name = BackupModule.Clip(S(b, "name"), 120); if (name == "") return BackupModule.Bad("bad_input", "Give the plan a name.");
            var paths = Lines(b, "paths", 50, 400); if (paths.Length == 0) return BackupModule.Bad("bad_input", "Give at least one folder to back up, for example C:\\Users or D:\\Data.");
            var excludes = Lines(b, "excludes", 100, 200);
            var every = Int(b, "everyHours", 24); if (every is < 0 or > 744) return BackupModule.Bad("bad_input", "Run every 1 to 744 hours, or 0 for only when asked.");
            var at = Int(b, "atHour", 22); if (at is < -1 or > 23) return BackupModule.Bad("bad_input", "The hour is 0 to 23.");
            var keepLast = Int(b, "keepLast", 30); if (keepLast is < 1 or > 1000) return BackupModule.Bad("bad_input", "Keep 1 to 1000 backups.");
            var keepDays = Int(b, "keepDays", 30); if (keepDays is < 0 or > 3650) return BackupModule.Bad("bad_input", "Keep backups 0 to 3650 days.");
            var protector = dp.CreateProtector(Purpose + ":" + t.Require().Id);
            await using var db = await BackupModule.Open(t, ct);
            Plan? old = null;
            if (id is long pid) { old = await One(http, db, pid, Acts.Read, ct); if (old == null) return Results.NotFound(); }
            var assetId = old?.AssetId ?? (b.TryGetProperty("assetId", out var av) && av.ValueKind == JsonValueKind.Number ? av.GetInt64() : 0);
            var dev = assetId == 0 ? null : (await http.RequestServices.GetRequiredService<IDeviceHealth>().SnapshotAsync(null, assetId, ct)).FirstOrDefault();
            if (dev == null) return BackupModule.Bad("bad_input", "Choose the device to back up.");
            if (!dev.HasAgent) return BackupModule.Bad("no_agent", "The device has no DeskAsk agent; file backups are made by the agent.");
            JsonObject? oldStorage = old == null ? null : JsonNode.Parse(protector.Unprotect(old.StorageSealed)) as JsonObject;
            var (storage, err) = ReadStorage(b, oldStorage); if (storage == null) return BackupModule.Bad("bad_input", err);
            var useVss = Bool(b, "useVss", true); var enabled = Bool(b, "enabled", true);
            long planId;
            if (old == null)
            {
                var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
                await using var c = new NpgsqlCommand(@"insert into backup.plans (company_id, asset_id, asset_name, name, paths, excludes, every_hours, at_hour, keep_last, keep_days, use_vss, enabled,
                    storage_kind, storage_where, storage_sealed, password_sealed, created_by) values (@co, @a, @an, @n, @p, @x, @e, @h, @kl, @kd, @v, @en, @sk, @sw, @ss, @ps, @by) returning id", db);
                BackupModule.P(c, "ps", protector.Protect(password)); BackupModule.P(c, "by", BackupModule.Actor(http.User));
                Bind(c); planId = (long)(await c.ExecuteScalarAsync(ct))!;
            }
            else
            {
                await using var c = new NpgsqlCommand(@"update backup.plans set company_id = @co, asset_name = @an, name = @n, paths = @p, excludes = @x, every_hours = @e, at_hour = @h, keep_last = @kl,
                    keep_days = @kd, use_vss = @v, enabled = @en, storage_kind = @sk, storage_where = @sw, storage_sealed = @ss, updated_at = now() where id = @id", db);
                BackupModule.P(c, "id", old.Id); Bind(c); await c.ExecuteNonQueryAsync(ct); planId = old.Id;
            }
            void Bind(NpgsqlCommand c)
            {
                BackupModule.P(c, "co", dev.CompanyId); BackupModule.P(c, "a", dev.Id); BackupModule.P(c, "an", dev.Name); BackupModule.P(c, "n", name); BackupModule.P(c, "p", paths); BackupModule.P(c, "x", excludes);
                BackupModule.P(c, "e", every); BackupModule.P(c, "h", at); BackupModule.P(c, "kl", keepLast); BackupModule.P(c, "kd", keepDays); BackupModule.P(c, "v", useVss); BackupModule.P(c, "en", enabled);
                BackupModule.P(c, "sk", (string)storage!["kind"]!); BackupModule.P(c, "sw", Where(storage)); BackupModule.P(c, "ss", protector.Protect(storage.ToJsonString()));
            }
            // the job on the dashboard follows the plan's schedule
            await using (var c = new NpgsqlCommand("update backup.jobs set every_hours = @e where tool = 'deskask' and asset_id = @a and lower(name) = lower(@n)", db))
            { BackupModule.P(c, "e", every); BackupModule.P(c, "a", dev.Id); BackupModule.P(c, "n", name); await c.ExecuteNonQueryAsync(ct); }
            await audit.WriteAsync(old == null ? "backup.plan_created" : "backup.plan_changed", name, $"{dev.Name}: {string.Join(", ", paths)} to {Where(storage)}", null, ct);
            await http.RequestServices.GetRequiredService<IDeviceActions>().NotifyAgentAsync(dev.Id, "backupsync", planId.ToString(), ct);
            return Results.Ok(new { id = planId });
        }
        Admin(r.MapPost("/api/backup/plans", (JsonElement b, HttpContext http, ITenantContext t, IDataProtectionProvider dp, IAuditLog audit, CancellationToken ct) => Save(null, b, http, t, dp, audit, ct)));
        Admin(r.MapPut("/api/backup/plans/{id:long}", (long id, JsonElement b, HttpContext http, ITenantContext t, IDataProtectionProvider dp, IAuditLog audit, CancellationToken ct) => Save(id, b, http, t, dp, audit, ct)));

        Admin(r.MapDelete("/api/backup/plans/{id:long}", async (long id, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await BackupModule.Open(t, ct);
            var p = await One(http, db, id, Acts.Read, ct); if (p == null) return Results.NotFound();
            await using (var c = new NpgsqlCommand("delete from backup.plans where id = @i", db)) { BackupModule.P(c, "i", id); await c.ExecuteNonQueryAsync(ct); }
            await audit.WriteAsync("backup.plan_deleted", p.Name, $"{p.AssetName}; the backups stay in {p.StorageWhere}", null, ct);
            await http.RequestServices.GetRequiredService<IDeviceActions>().NotifyAgentAsync(p.AssetId, "backupsync", id.ToString(), ct);
            return Results.Ok(new { ok = true });
        }));

        Modify(r.MapPost("/api/backup/plans/{id:long}/run", async (long id, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await BackupModule.Open(t, ct);
            var p = await One(http, db, id, Acts.Modify, ct); if (p == null) return Results.NotFound();
            if (!await http.RequestServices.GetRequiredService<IDeviceActions>().NotifyAgentAsync(p.AssetId, "backuprun", id.ToString(), ct))
                return BackupModule.Bad("no_agent", "The device has no working agent.");
            await audit.WriteAsync("backup.plan_run", p.Name, p.AssetName, null, ct);
            return Results.Ok(new { ok = true });
        }));

        Modify(r.MapPost("/api/backup/plans/{id:long}/restore", async (long id, JsonElement b, HttpContext http, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await BackupModule.Open(t, ct);
            var p = await One(http, db, id, Acts.Modify, ct); if (p == null) return Results.NotFound();
            var snap = S(b, "snapshot");
            await using (var c = new NpgsqlCommand("select count(*) from backup.snapshots where plan_id = @p and id = @s", db))
            { BackupModule.P(c, "p", id); BackupModule.P(c, "s", snap); if ((long)(await c.ExecuteScalarAsync(ct))! == 0) return BackupModule.Bad("bad_input", "Choose a backup to restore."); }
            var include = Lines(b, "include", 100, 400);
            var target = BackupModule.Clip(S(b, "target"), 400);
            if (target == "" && !Bool(b, "replace", false)) return BackupModule.Bad("bad_input", "Give a folder to restore into, or confirm that the files are put back where they were (replacing what is there).");
            long rid;
            await using (var c = new NpgsqlCommand("insert into backup.restores (plan_id, snapshot_id, include, target, by_name) values (@p, @s, @i, @t, @b) returning id", db))
            { BackupModule.P(c, "p", id); BackupModule.P(c, "s", snap); BackupModule.P(c, "i", include); BackupModule.P(c, "t", target); BackupModule.P(c, "b", BackupModule.Actor(http.User)); rid = (long)(await c.ExecuteScalarAsync(ct))!; }
            await audit.WriteAsync("backup.restore_asked", p.Name, $"{p.AssetName}: backup {snap}, {(include.Length == 0 ? "everything" : string.Join(", ", include))} to {(target == "" ? "the original places" : target)}", null, ct);
            if (!await http.RequestServices.GetRequiredService<IDeviceActions>().NotifyAgentAsync(p.AssetId, "backuprestore", rid.ToString(), ct))
            {
                await using var u = new NpgsqlCommand("update backup.restores set state = 'failed', message = 'The device has no working agent.', done_at = now() where id = @i", db);
                BackupModule.P(u, "i", rid); await u.ExecuteNonQueryAsync(ct);
                return BackupModule.Bad("no_agent", "The device has no working agent.");
            }
            return Results.Ok(new { id = rid });
        }));

        // the backup password, for restoring with DeskAsk's tools if this server is lost; every reveal is recorded
        Admin(r.MapPost("/api/backup/plans/{id:long}/password", async (long id, HttpContext http, ITenantContext t, IDataProtectionProvider dp, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = await BackupModule.Open(t, ct);
            var p = await One(http, db, id, Acts.Read, ct); if (p == null) return Results.NotFound();
            await audit.WriteAsync("backup.password_shown", p.Name, p.AssetName, null, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { password = dp.CreateProtector(Purpose + ":" + t.Require().Id).Unprotect(p.PasswordSealed), folder = "deskask-plan-" + p.Id });
        }));
    }
}

/// <summary>What the device's agent asks for: its plans (with the secrets), and it tells the snapshots it made and how restores went.</summary>
public sealed class BackupAgentHandler : IBackupAgent
{
    readonly ITenantContext tenant; readonly IDataProtectionProvider dp; readonly IAuditLog audit;
    public BackupAgentHandler(ITenantContext t, IDataProtectionProvider d, IAuditLog a) { tenant = t; dp = d; audit = a; }

    object Full(PlansApi.Plan p, IDataProtector pr) => new
    {
        id = p.Id, name = p.Name, paths = p.Paths, excludes = p.Excludes, everyHours = p.EveryHours, atHour = p.AtHour, keepLast = p.KeepLast, keepDays = p.KeepDays,
        useVss = p.UseVss, enabled = p.Enabled, storage = JsonNode.Parse(pr.Unprotect(p.StorageSealed)), password = pr.Unprotect(p.PasswordSealed),
    };

    public async Task<(int Status, object? Body)> HandleAsync(long assetId, string method, string action, string id, JsonElement body, CancellationToken ct = default)
    {
        var pr = dp.CreateProtector(PlansApi.Purpose + ":" + tenant.Require().Id);
        await using var db = await BackupModule.Open(tenant, ct);
        switch (method, action)
        {
            case ("GET", "plans"):
                var plans = await PlansApi.Load(db, "asset_id = @a", c => BackupModule.P(c, "a", assetId), ct);
                return (200, plans.Select(p => Full(p, pr)).ToList());

            case ("POST", "snapshot"):
            {
                if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("plan", out var pv) || pv.ValueKind != JsonValueKind.Number || !pv.TryGetInt64(out var planId)) return (400, new { error = "bad_input" });
                var plan = (await PlansApi.Load(db, "id = @i and asset_id = @a", c => { BackupModule.P(c, "i", planId); BackupModule.P(c, "a", assetId); }, ct)).FirstOrDefault();
                if (plan == null) return (404, new { error = "not_found" });
                string Str(string n) => body.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                long Num(string n) => body.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? Math.Max(0, l) : 0;
                var sid = BackupModule.Clip(Str("id"), 80); if (sid == "") return (400, new { error = "bad_input" });
                var at = DateTime.TryParse(Str("at"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t0) ? t0 : DateTime.UtcNow;
                await using (var c = new NpgsqlCommand(@"insert into backup.snapshots (plan_id, id, at, files, bytes, new_bytes, warnings) values (@p, @i, @t, @f, @b, @n, @w)
                    on conflict (plan_id, id) do nothing", db))
                {
                    BackupModule.P(c, "p", planId); BackupModule.P(c, "i", sid); BackupModule.P(c, "t", at); BackupModule.P(c, "f", (int)Math.Min(int.MaxValue, Num("files")));
                    BackupModule.P(c, "b", Num("bytes")); BackupModule.P(c, "n", Num("newBytes")); BackupModule.P(c, "w", (int)Math.Min(int.MaxValue, Num("warnings"))); await c.ExecuteNonQueryAsync(ct);
                }
                if (body.TryGetProperty("removed", out var rm) && rm.ValueKind == JsonValueKind.Array)
                {
                    var ids = rm.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Take(10000).ToArray();
                    await using var c = new NpgsqlCommand("delete from backup.snapshots where plan_id = @p and id = any(@i)", db);
                    BackupModule.P(c, "p", planId); BackupModule.P(c, "i", ids); await c.ExecuteNonQueryAsync(ct);
                }
                return (200, new { ok = true });
            }

            case ("GET", "restore"):
            {
                if (!long.TryParse(id, out var rid)) return (400, new { error = "bad_input" });
                long planId; string snap; string[] include; string target;
                await using (var c = new NpgsqlCommand(@"update backup.restores r set state = 'running' from backup.plans p
                    where r.id = @i and r.plan_id = p.id and p.asset_id = @a and r.state = 'queued' returning r.plan_id, r.snapshot_id, r.include, r.target", db))
                {
                    BackupModule.P(c, "i", rid); BackupModule.P(c, "a", assetId);
                    await using var rd = await c.ExecuteReaderAsync(ct);
                    if (!await rd.ReadAsync(ct)) return (404, new { error = "gone", detail = "This restore was already taken or never existed." });
                    planId = rd.GetInt64(0); snap = rd.GetString(1); include = (string[])rd.GetValue(2); target = rd.GetString(3);
                }
                var plan = (await PlansApi.Load(db, "id = @i", c => BackupModule.P(c, "i", planId), ct)).First();
                return (200, new { plan = Full(plan, pr), snapshot = snap, include, target });
            }

            case ("POST", "restore"):
            {
                if (!long.TryParse(id, out var rid) || body.ValueKind != JsonValueKind.Object) return (400, new { error = "bad_input" });
                var ok = body.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
                var msg = body.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? BackupModule.Clip(m.GetString(), 2000) : "";
                long files = body.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Number && f.TryGetInt64(out var fl) ? fl : 0, bytes = body.TryGetProperty("bytes", out var bb) && bb.ValueKind == JsonValueKind.Number && bb.TryGetInt64(out var bl) ? bl : 0;
                await using var c = new NpgsqlCommand(@"update backup.restores r set state = @s, message = @m, files = @f, bytes = @b, done_at = now() from backup.plans p
                    where r.id = @i and r.plan_id = p.id and p.asset_id = @a and r.state = 'running' returning p.name", db);
                BackupModule.P(c, "s", ok ? "done" : "failed"); BackupModule.P(c, "m", msg); BackupModule.P(c, "f", (int)Math.Clamp(files, 0, int.MaxValue)); BackupModule.P(c, "b", Math.Max(0, bytes));
                BackupModule.P(c, "i", rid); BackupModule.P(c, "a", assetId);
                if (await c.ExecuteScalarAsync(ct) is string name) await audit.WriteAsync(ok ? "backup.restore_done" : "backup.restore_failed", name, msg, "agent", ct);
                return (200, new { ok = true });
            }
        }
        return (404, new { error = "unknown" });
    }
}
