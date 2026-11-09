using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DeskAsk.Platform.Tenancy;
using Npgsql;
using NpgsqlTypes;

namespace DeskAsk.Platform.Jobs;

/// <summary>Schema "jobs" in every customer database: the queue and the recurring schedule.</summary>
public sealed class JobsSchema : ITenantSchema
{
    public string Name => "jobs";
    public async Task EnsureAsync(IServiceProvider services, TenantInfo tenant, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(tenant.ConnectionString);
        await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"
            select pg_advisory_lock(727004);
            create schema if not exists jobs;
            create table if not exists jobs.queue (
              id bigserial primary key, type text not null, payload text not null default '{}', run_at timestamptz not null default now(),
              attempts int not null default 0, max_attempts int not null default 5, status text not null default 'queued',
              locked_until timestamptz, last_error text not null default '', dedupe_key text,
              created_at timestamptz not null default now(), started_at timestamptz, finished_at timestamptz);
            create index if not exists queue_due on jobs.queue (run_at) where status in ('queued','running');
            create unique index if not exists queue_dedupe on jobs.queue (dedupe_key) where dedupe_key is not null and status in ('queued','running');
            create table if not exists jobs.recurring (
              name text primary key, type text not null, interval_seconds int not null, next_run timestamptz not null default now());
            select pg_advisory_unlock(727004);", db);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public sealed class JobQueue : IJobQueue
{
    readonly ITenantContext tenant; readonly IConfiguration cfg;
    public JobQueue(ITenantContext tenant, IConfiguration cfg) { this.tenant = tenant; this.cfg = cfg; }

    public async Task<long> EnqueueAsync(string type, string payloadJson = "{}", DateTimeOffset? runAt = null, string? dedupeKey = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(type) || type.Length > 100) throw new ArgumentException("Bad job type.", nameof(type));
        if (payloadJson.Length > 65536) throw new ArgumentException("The payload is larger than 64 KB.", nameof(payloadJson));
        await using var db = new NpgsqlConnection(tenant.Require().ConnectionString); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(@"insert into jobs.queue (type, payload, run_at, max_attempts, dedupe_key)
            values (@t, @p, coalesce(@r, now()), @m, @d)
            on conflict (dedupe_key) where dedupe_key is not null and status in ('queued','running') do nothing returning id", db);
        cmd.Parameters.AddWithValue("t", type); cmd.Parameters.AddWithValue("p", payloadJson);
        cmd.Parameters.Add(new NpgsqlParameter("r", NpgsqlDbType.TimestampTz) { Value = runAt?.UtcDateTime is { } u ? u : DBNull.Value });
        cmd.Parameters.AddWithValue("m", cfg.GetValue("Jobs:MaxAttempts", 5));
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Text) { Value = (object?)dedupeKey ?? DBNull.Value });
        if (await cmd.ExecuteScalarAsync(ct) is long id) return id;
        await using var find = new NpgsqlCommand("select id from jobs.queue where dedupe_key = @d and status in ('queued','running') limit 1", db);
        find.Parameters.AddWithValue("d", dedupeKey!);
        return (long)(await find.ExecuteScalarAsync(ct))!;
    }
}

/// <summary>
/// Takes due jobs of one customer at a time. A job is claimed with FOR UPDATE SKIP LOCKED, so any number of workers can run side by side
/// and a job is never taken twice. A job whose worker died is taken again when its lease ran out. A failing job is retried with a growing
/// pause (10 s, 20 s, 40 s ... up to an hour) and marked failed after Jobs:MaxAttempts.
/// </summary>
public sealed class JobRunner
{
    readonly IServiceProvider sp; readonly TenantSchemas schemas; readonly Dictionary<string, IJobHandler> handlers;
    readonly IReadOnlyList<RecurringJob> recurring; readonly IConfiguration cfg; readonly ILogger<JobRunner> log;

    public JobRunner(IServiceProvider sp, TenantSchemas schemas, IEnumerable<IJobHandler> handlers, IEnumerable<RecurringJob> recurring, IConfiguration cfg, ILogger<JobRunner> log)
    {
        this.sp = sp; this.schemas = schemas; this.recurring = recurring.ToList(); this.cfg = cfg; this.log = log;
        this.handlers = handlers.ToDictionary(h => h.Type);
    }

    /// <summary>Runs what is due for this customer now (at most Jobs:BatchSize, default 50). Returns how many jobs were run.</summary>
    public async Task<int> RunDueAsync(TenantInfo tenant, CancellationToken ct)
    {
        await using (var scope = sp.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Current = tenant;
            await schemas.EnsureAsync(scope.ServiceProvider, tenant, ct);
        }
        await ScheduleRecurringAsync(tenant, ct);
        var max = cfg.GetValue("Jobs:BatchSize", 50); var n = 0;
        while (n < max && await RunOneAsync(tenant, ct)) n++;
        return n;
    }

    async Task ScheduleRecurringAsync(TenantInfo tenant, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(tenant.ConnectionString); await db.OpenAsync(ct);
        foreach (var r in recurring)
        {
            await using (var up = new NpgsqlCommand(@"insert into jobs.recurring (name, type, interval_seconds) values (@n, @t, @i)
                on conflict (name) do update set type = excluded.type, interval_seconds = excluded.interval_seconds", db))
            {
                up.Parameters.AddWithValue("n", r.Name); up.Parameters.AddWithValue("t", r.Type); up.Parameters.AddWithValue("i", (int)r.Every.TotalSeconds);
                await up.ExecuteNonQueryAsync(ct);
            }
            // whoever moves next_run forward owns this round, so two workers never queue the same recurring job twice
            await using var due = new NpgsqlCommand("update jobs.recurring set next_run = now() + make_interval(secs => interval_seconds) where name = @n and next_run <= now() returning type", db);
            due.Parameters.AddWithValue("n", r.Name);
            if (await due.ExecuteScalarAsync(ct) is string type)
            {
                await using var ins = new NpgsqlCommand(@"insert into jobs.queue (type, dedupe_key, max_attempts) values (@t, @d, @m)
                    on conflict (dedupe_key) where dedupe_key is not null and status in ('queued','running') do nothing", db);
                ins.Parameters.AddWithValue("t", type); ins.Parameters.AddWithValue("d", "recurring:" + r.Name); ins.Parameters.AddWithValue("m", cfg.GetValue("Jobs:MaxAttempts", 5));
                await ins.ExecuteNonQueryAsync(ct);
            }
        }
    }

    async Task<bool> RunOneAsync(TenantInfo tenant, CancellationToken ct)
    {
        long id; string type, payload; int attempt, maxAttempts;
        await using var db = new NpgsqlConnection(tenant.ConnectionString); await db.OpenAsync(ct);
        await using (var claim = new NpgsqlCommand(@"
            with c as (select id from jobs.queue where (status = 'queued' and run_at <= now()) or (status = 'running' and locked_until < now())
                       order by run_at, id for update skip locked limit 1)
            update jobs.queue q set status = 'running', attempts = q.attempts + 1, started_at = now(), locked_until = now() + make_interval(secs => @lease)
            from c where q.id = c.id returning q.id, q.type, q.payload, q.attempts, q.max_attempts", db))
        {
            claim.Parameters.AddWithValue("lease", cfg.GetValue("Jobs:LeaseSeconds", 900));
            await using var rd = await claim.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) return false;
            (id, type, payload, attempt, maxAttempts) = (rd.GetInt64(0), rd.GetString(1), rd.GetString(2), rd.GetInt32(3), rd.GetInt32(4));
        }
        if (attempt > maxAttempts) { await Finish(db, id, "failed", "Gave up: the worker stopped while running it too many times.", null); return true; }
        if (!handlers.TryGetValue(type, out var handler)) { await Finish(db, id, "failed", "No handler is registered for job type '" + type + "'.", null); return true; }

        try
        {
            await using var scope = sp.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().Current = tenant;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromMinutes(cfg.GetValue("Jobs:TimeoutMinutes", 10)));
            await handler.RunAsync(new JobContext(tenant, id, payload, attempt, scope.ServiceProvider), limit.Token);
            await Finish(db, id, "done", "", null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await Finish(db, id, "queued", "Stopped while running; will run again.", TimeSpan.Zero, attempt - 1);   // shutdown is not the job's fault
            throw;
        }
        catch (Exception e)
        {
            var text = e.GetType().Name + ": " + e.Message; if (text.Length > 2000) text = text[..2000];
            log.LogWarning(e, "Job {Id} ({Type}) for {Tenant} failed, attempt {Attempt} of {Max}", id, type, tenant.Id, attempt, maxAttempts);
            if (attempt >= maxAttempts) await Finish(db, id, "failed", text, null);
            else await Finish(db, id, "queued", text, TimeSpan.FromSeconds(Math.Min(10 * Math.Pow(2, attempt - 1), 3600)));
        }
        return true;
    }

    static async Task Finish(NpgsqlConnection db, long id, string status, string error, TimeSpan? retryIn, int? attempts = null)
    {
        await using var cmd = new NpgsqlCommand(@"update jobs.queue set status = @s, last_error = @e, locked_until = null,
            finished_at = case when @s in ('done','failed') then now() else null end,
            run_at = case when @s = 'queued' then now() + make_interval(secs => @w) else run_at end,
            attempts = coalesce(@a, attempts) where id = @id", db);
        cmd.Parameters.AddWithValue("s", status); cmd.Parameters.AddWithValue("e", error); cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("w", retryIn?.TotalSeconds ?? 0.0);
        cmd.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Integer) { Value = (object?)attempts ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }
}

/// <summary>The worker role: every few seconds, walks all active customers and runs their due jobs.</summary>
public sealed class JobWorker : BackgroundService
{
    readonly ITenantCatalog catalog; readonly JobRunner runner; readonly IConfiguration cfg; readonly ILogger<JobWorker> log;
    public JobWorker(ITenantCatalog catalog, JobRunner runner, IConfiguration cfg, ILogger<JobWorker> log) { this.catalog = catalog; this.runner = runner; this.cfg = cfg; this.log = log; }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        var every = TimeSpan.FromSeconds(Math.Max(1, cfg.GetValue("Jobs:PollSeconds", 5)));
        log.LogInformation("Job worker started, looking for work every {Every}", every);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                foreach (var t in await catalog.ListActiveAsync(stop))
                {
                    if (t.ConnectionString == "") continue;
                    try { await runner.RunDueAsync(t, stop); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                    catch (Exception e) { log.LogError(e, "Background jobs of {Tenant} could not run", t.Id); }   // one customer's problem never stops the others
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogError(e, "Job worker could not list the customers"); }
            try { await Task.Delay(every, stop); } catch (OperationCanceledException) { return; }
        }
    }
}

/// <summary>Daily: forgets finished jobs after 14 days and failed ones after 30.</summary>
public sealed class JobsCleanup : IJobHandler
{
    public const string JobType = "jobs.cleanup";
    public string Type => JobType;
    public async Task RunAsync(JobContext job, CancellationToken ct)
    {
        await using var db = new NpgsqlConnection(job.Tenant.ConnectionString); await db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("delete from jobs.queue where (status = 'done' and finished_at < now() - interval '14 days') or (status = 'failed' and finished_at < now() - interval '30 days')", db);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public sealed class JobsModule : IPlatformModule
{
    public string Key => "jobs";
    public string Schema => "jobs";

    public void AddServices(IServiceCollection s, IConfiguration cfg)
    {
        s.AddSingleton<ITenantSchema, JobsSchema>();
        s.AddScoped<IJobQueue, JobQueue>();
        s.AddSingleton<JobRunner>();
        s.AddSingleton<IJobHandler, JobsCleanup>();
        s.AddSingleton(new RecurringJob("jobs.cleanup", JobsCleanup.JobType, TimeSpan.FromHours(24)));
        if (PlatformRoles.Parse(cfg).HasFlag(PlatformRole.Worker)) s.AddHostedService<JobWorker>();
    }

    public void MapEndpoints(IEndpointRouteBuilder r)
    {
        r.MapGet("/api/platform/jobs", async (ITenantContext t, string? status, string? type, int? take, CancellationToken ct) =>
        {
            await using var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(@"select id, type, status, attempts, max_attempts, run_at, last_error, created_at, finished_at from jobs.queue
                where (@s = '' or status = @s) and (@t = '' or type = @t) order by id desc limit @n", db);
            cmd.Parameters.AddWithValue("s", status ?? ""); cmd.Parameters.AddWithValue("t", type ?? ""); cmd.Parameters.AddWithValue("n", Math.Clamp(take ?? 50, 1, 200));
            var rows = new List<object>();
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))   // the payload is not shown: it may hold personal data
                rows.Add(new { id = rd.GetInt64(0), type = rd.GetString(1), status = rd.GetString(2), attempts = rd.GetInt32(3), maxAttempts = rd.GetInt32(4), runAt = rd.GetDateTime(5), lastError = rd.GetString(6), createdAt = rd.GetDateTime(7), finishedAt = rd.IsDBNull(8) ? (DateTime?)null : rd.GetDateTime(8) });
            return Results.Ok(rows);
        }).RequireAuthorization(p => p.RequireRole("admin"));

        r.MapGet("/api/platform/jobs/summary", async (ITenantContext t, CancellationToken ct) =>
        {
            await using var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("select status, count(*) from jobs.queue group by status", db);
            var d = new Dictionary<string, long> { ["queued"] = 0, ["running"] = 0, ["done"] = 0, ["failed"] = 0 };
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) d[rd.GetString(0)] = rd.GetInt64(1);
            return Results.Ok(d);
        }).RequireAuthorization(p => p.RequireRole("admin"));

        r.MapPost("/api/platform/jobs/{id:long}/retry", async (long id, ITenantContext t, IAuditLog audit, CancellationToken ct) =>
        {
            await using var db = new NpgsqlConnection(t.Require().ConnectionString); await db.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("update jobs.queue set status = 'queued', attempts = 0, run_at = now(), last_error = '', finished_at = null where id = @i and status = 'failed'", db);
            cmd.Parameters.AddWithValue("i", id);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) return Results.NotFound();
            await audit.WriteAsync("job.retry", id.ToString(), ct: ct);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization(p => p.RequireRole("admin"));
    }
}
