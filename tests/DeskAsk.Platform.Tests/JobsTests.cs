using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using DeskAsk.Platform;
using DeskAsk.Platform.Jobs;
using DeskAsk.Platform.Tenancy;
using Npgsql;
using Xunit;

public sealed class TestWorkHandler : IJobHandler
{
    public static Func<JobContext, Task> Behavior = _ => Task.CompletedTask;
    public static readonly ConcurrentBag<long> Ran = new();
    public string Type => "test.work";
    public Task RunAsync(JobContext job, CancellationToken ct) { Ran.Add(job.Id); return Behavior(job); }
}

public class JobsTests : IClassFixture<WebApplicationFactory<Program>>
{
    static readonly string Pg = Environment.GetEnvironmentVariable("TEST_PG_CONNECTION") ?? "";
    readonly WebApplicationFactory<Program> factory;

    public JobsTests(WebApplicationFactory<Program> f) =>
        factory = f.WithWebHostBuilder(TestSupport.Configure).WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IJobHandler, TestWorkHandler>()));

    static async Task Sql(string sql)
    {
        await using var db = new NpgsqlConnection(Pg); await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db); await cmd.ExecuteNonQueryAsync();
    }
    static async Task<T> Scalar<T>(string sql)
    {
        await using var db = new NpgsqlConnection(Pg); await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, db); return (T)(await cmd.ExecuteScalarAsync())!;
    }

    async Task<(TenantInfo T, JobRunner R)> Start(WebApplicationFactory<Program>? f = null)
    {
        f ??= factory; _ = f.Server;
        var t = (await f.Services.GetRequiredService<ITenantCatalog>().ListActiveAsync()).First();
        var r = f.Services.GetRequiredService<JobRunner>();
        await r.RunDueAsync(t, default);   // makes the schema, and queues + runs the recurring jobs once
        await Sql("truncate jobs.queue restart identity");
        TestWorkHandler.Behavior = _ => Task.CompletedTask;
        while (TestWorkHandler.Ran.TryTake(out _)) { }
        return (t, r);
    }

    static async Task<long> Enqueue(WebApplicationFactory<Program> f, TenantInfo t, string type = "test.work", DateTimeOffset? at = null, string? dedupe = null)
    {
        await using var scope = f.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Current = t;
        return await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(type, "{}", at, dedupe);
    }

    [PgFact]
    public async Task AJobIsRunOnce()
    {
        var (t, r) = await Start();
        var id = await Enqueue(factory, t);
        await r.RunDueAsync(t, default);
        await r.RunDueAsync(t, default);
        Assert.Equal(new[] { id }, TestWorkHandler.Ran.ToArray());
        Assert.Equal("done", await Scalar<string>($"select status from jobs.queue where id = {id}"));
    }

    [PgFact]
    public async Task ADelayedJobWaits()
    {
        var (t, r) = await Start();
        var id = await Enqueue(factory, t, at: DateTimeOffset.UtcNow.AddHours(1));
        await r.RunDueAsync(t, default);
        Assert.Empty(TestWorkHandler.Ran);
        Assert.Equal("queued", await Scalar<string>($"select status from jobs.queue where id = {id}"));
    }

    [PgFact]
    public async Task AFailingJobIsRetriedWithAPauseThenGivesUp()
    {
        var (t, r) = await Start();
        TestWorkHandler.Behavior = _ => throw new InvalidOperationException("boom");
        var id = await Enqueue(factory, t);
        await r.RunDueAsync(t, default);
        Assert.Equal("queued", await Scalar<string>($"select status from jobs.queue where id = {id}"));
        Assert.True(await Scalar<bool>($"select run_at > now() + interval '5 seconds' from jobs.queue where id = {id}"));   // paused
        Assert.Contains("boom", await Scalar<string>($"select last_error from jobs.queue where id = {id}"));
        for (var i = 0; i < 6; i++)
        {
            await Sql($"update jobs.queue set run_at = now() where id = {id} and status = 'queued'");
            await r.RunDueAsync(t, default);
        }
        Assert.Equal("failed", await Scalar<string>($"select status from jobs.queue where id = {id}"));
        Assert.Equal(5, await Scalar<int>($"select attempts from jobs.queue where id = {id}"));
        Assert.Equal(5, TestWorkHandler.Ran.Count);
    }

    [PgFact]
    public async Task TheSameDedupeKeyQueuesOnlyOneJob()
    {
        var (t, _) = await Start();
        var a = await Enqueue(factory, t, dedupe: "sync:1");
        var b = await Enqueue(factory, t, dedupe: "sync:1");
        var c = await Enqueue(factory, t, dedupe: "sync:2");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(2, await Scalar<long>("select count(*) from jobs.queue"));
    }

    [PgFact]
    public async Task AJobWhoseWorkerDiedIsTakenAgain()
    {
        var (t, r) = await Start();
        var id = await Enqueue(factory, t);
        await Sql($"update jobs.queue set status = 'running', attempts = 1, locked_until = now() - interval '1 minute' where id = {id}");
        await r.RunDueAsync(t, default);
        Assert.Equal(new[] { id }, TestWorkHandler.Ran.ToArray());
        Assert.Equal(2, await Scalar<int>($"select attempts from jobs.queue where id = {id}"));
        Assert.Equal("done", await Scalar<string>($"select status from jobs.queue where id = {id}"));
    }

    [PgFact]
    public async Task AJobStillLeasedIsLeftAlone()
    {
        var (t, r) = await Start();
        var id = await Enqueue(factory, t);
        await Sql($"update jobs.queue set status = 'running', attempts = 1, locked_until = now() + interval '10 minutes' where id = {id}");
        await r.RunDueAsync(t, default);
        Assert.Empty(TestWorkHandler.Ran);
    }

    [PgFact]
    public async Task WorkersSideBySideNeverTakeAJobTwice()
    {
        var (t, r) = await Start();
        TestWorkHandler.Behavior = _ => Task.Delay(20);
        var ids = new List<long>();
        for (var i = 0; i < 24; i++) ids.Add(await Enqueue(factory, t));
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => r.RunDueAsync(t, default))));
        var ran = TestWorkHandler.Ran.OrderBy(x => x).ToList();
        Assert.Equal(ids.OrderBy(x => x), ran);
    }

    [PgFact]
    public async Task AJobWithoutAHandlerFails()
    {
        var (t, r) = await Start();
        var id = await Enqueue(factory, t, "nobody.handles.this");
        await r.RunDueAsync(t, default);
        Assert.Equal("failed", await Scalar<string>($"select status from jobs.queue where id = {id}"));
        Assert.Contains("No handler", await Scalar<string>($"select last_error from jobs.queue where id = {id}"));
    }

    [PgFact]
    public async Task RecurringJobsAreQueuedOncePerInterval()
    {
        var (t, r) = await Start();
        await Sql("update jobs.recurring set next_run = now() - interval '1 second'");
        await r.RunDueAsync(t, default);
        await r.RunDueAsync(t, default);
        Assert.Equal(1, await Scalar<long>("select count(*) from jobs.queue where type = 'jobs.cleanup' and status = 'done'"));
        await Sql("update jobs.recurring set next_run = now() - interval '1 second' where name = 'jobs.cleanup'");
        await r.RunDueAsync(t, default);
        Assert.Equal(2, await Scalar<long>("select count(*) from jobs.queue where type = 'jobs.cleanup' and status = 'done'"));
        Assert.Equal(0, await Scalar<long>("select count(*) from jobs.queue where status = 'failed'"));   // the audit and token jobs ran clean too
    }

    [PgFact]
    public async Task TheAuditJobReportsABrokenChain()
    {
        var (t, r) = await Start();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Current = t;
            var audit = scope.ServiceProvider.GetRequiredService<IAuditLog>();
            await audit.WriteAsync("test.row", "a", "first");
            await audit.WriteAsync("test.row", "b", "second");
        }
        await Sql("update core.audit set detail = 'tampered' where id = (select min(id) from core.audit)");
        try
        {
            await Enqueue(factory, t, "core.audit_verify");
            await r.RunDueAsync(t, default);
            Assert.True(await Scalar<long>("select count(*) from core.audit where action = 'audit.verify_failed'") >= 1);
        }
        finally { await Sql("truncate core.audit restart identity"); }
    }

    [PgFact]
    public async Task TheWorkerRoleRunsJobsInTheBackground()
    {
        var f = factory.WithWebHostBuilder(b => { b.UseSetting("Platform:Roles", "all"); b.UseSetting("Jobs:PollSeconds", "1"); });
        var (t, _) = await Start(f);
        var id = await Enqueue(f, t);
        for (var i = 0; i < 30 && await Scalar<string>($"select status from jobs.queue where id = {id}") != "done"; i++) await Task.Delay(500);
        Assert.Equal("done", await Scalar<string>($"select status from jobs.queue where id = {id}"));
    }

    [PgFact]
    public async Task TheAdminApiListsAndRetries()
    {
        var anon = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/platform/jobs")).StatusCode);

        var (t, _) = await Start();
        var api = await TestSupport.AdminClient(factory);
        var id = await Enqueue(factory, t);
        await Sql($"update jobs.queue set status = 'failed', attempts = 5, last_error = 'x', finished_at = now() where id = {id}");

        var list = JsonDocument.Parse(await api.GetStringAsync("/api/platform/jobs?status=failed")).RootElement;
        Assert.Contains(list.EnumerateArray(), j => j.GetProperty("id").GetInt64() == id);
        Assert.False(list.EnumerateArray().First().TryGetProperty("payload", out _));
        var sum = JsonDocument.Parse(await api.GetStringAsync("/api/platform/jobs/summary")).RootElement;
        Assert.Equal(1, sum.GetProperty("failed").GetInt64());

        Assert.Equal(HttpStatusCode.OK, (await api.PostAsync($"/api/platform/jobs/{id}/retry", null)).StatusCode);
        Assert.Equal("queued", await Scalar<string>($"select status from jobs.queue where id = {id}"));
        Assert.Equal(HttpStatusCode.NotFound, (await api.PostAsync($"/api/platform/jobs/{id}/retry", null)).StatusCode);   // no longer failed
    }
}
