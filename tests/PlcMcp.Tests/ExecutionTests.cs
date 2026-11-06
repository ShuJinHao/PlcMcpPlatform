using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlcMcp.EngineeringService.Execution;
using PlcMcp.EntityFrameworkCore;
using PlcMcp.Execution;
using PlcMcp.Inovance;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.TestHost;
using Xunit;

namespace PlcMcp.Tests;

public sealed class ExecutionTests
{
    [Fact] public async Task Durable_pipeline_reads_source_and_returns_compile_failure_diagnostics()
    {
        await using var h = await Harness.Create(new(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), CompileError: true));
        foreach (var operation in new[] { EngineeringOperations.Open, EngineeringOperations.Structure, EngineeringOperations.ReadProgram, EngineeringOperations.Compile })
        {
            var accepted = await h.Submit(operation);
            Assert.True(accepted.IsSuccess, accepted.Error?.Code);
            Assert.Equal("Queued", accepted.Value!.Status);
            Assert.True(await h.Dispatcher.RunNextAsync(default));
            var job = (await h.Store.GetAsync(accepted.Value.JobId, default))!;
            Assert.Equal(operation == EngineeringOperations.Compile ? "Failed" : "Succeeded", job.Status);
            Assert.Equal("fixture", job.ExecutionMode); Assert.Equal("simulated", job.EvidenceKind); Assert.False(job.EngineeringReady);
            if (operation == EngineeringOperations.Compile)
            {
                Assert.Equal("compile_failed", job.ErrorCode);
                Assert.Equal("FIXTURE0001", job.Result!.Value.GetProperty("diagnostics")[0].GetProperty("code").GetString());
                Assert.False((await h.Store.GetSlotAsync(default)).Blocked);
            }
        }
    }

    [Fact] public async Task Concurrent_duplicate_submissions_share_a_durable_identity_and_conflicting_payload_is_rejected()
    {
        await using var h = await Harness.Create();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => h.Submit(key: "same")));
        Assert.All(results, r => Assert.True(r.IsSuccess, r.Error?.Code));
        Assert.Single(results.Select(r => r.Value!.JobId).Distinct());
        var conflict = await h.Jobs.SubmitAsync(new(EngineeringOperations.Open, new(h.Path), "same", TimeoutSeconds: 61), default);
        Assert.Equal("idempotency_conflict", conflict.Error?.Code);
        Assert.NotNull(await h.Store.ClaimAsync(default));
        Assert.Null(await h.Store.ClaimAsync(default));
    }

    [Fact] public async Task Queued_cancellation_does_not_start_the_executor()
    {
        await using var h = await Harness.Create();
        var receipt = (await h.Submit()).Value!;
        Assert.Equal("Cancelled", (await h.Jobs.CancelAsync(receipt.JobId, default)).Value!.Status);
        Assert.False(await h.Dispatcher.RunNextAsync(default));
        Assert.Equal("Cancelled", (await h.Jobs.CancelAsync(receipt.JobId, default)).Value!.Status);
    }

    [Fact] public async Task Running_cancellation_waits_for_the_childless_test_process_to_stop()
    {
        await using var h = await Harness.Create(new(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), DelayMilliseconds: 5000));
        var receipt = (await h.Submit()).Value!;
        var execution = h.Dispatcher.RunNextAsync(default);
        await h.WaitFor(receipt.JobId, "Running");
        Assert.Equal("CancelRequested", (await h.Jobs.CancelAsync(receipt.JobId, default)).Value!.Status);
        Assert.True(await execution.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal("Cancelled", (await h.Store.GetAsync(receipt.JobId, default))!.Status);
        Assert.False((await h.Store.GetSlotAsync(default)).Blocked);
    }

    [Fact] public async Task Recovery_preserves_queued_work_without_a_channel_notification()
    {
        await using var h = await Harness.Create();
        var id = (await h.Submit()).Value!.JobId;
        using var restarted = new SqliteJobStore(h.Options, "fixture");
        await restarted.InitializeAsync(default);
        Assert.True(await new JobDispatcher(restarted, h.Executor).RunNextAsync(default));
        Assert.Equal("Succeeded", (await restarted.GetAsync(id, default))!.Status);
    }

    [Fact] public async Task Recovery_quarantines_inflight_work_and_does_not_claim_later_jobs()
    {
        await using var h = await Harness.Create();
        var first = (await h.Submit(key: "first")).Value!;
        var second = (await h.Submit(key: "second")).Value!;
        var claimed = (await h.Store.ClaimAsync(default))!;
        using var restarted = new SqliteJobStore(h.Options, "fixture");
        await restarted.InitializeAsync(default);
        Assert.Equal("OutcomeUnknown", (await restarted.GetAsync(claimed.Request.JobId, default))!.Status);
        var queued = claimed.Request.JobId == first.JobId ? second.JobId : first.JobId;
        Assert.Equal("Queued", (await restarted.GetAsync(queued, default))!.Status);
        Assert.True((await restarted.GetSlotAsync(default)).Blocked);
        Assert.Null(await restarted.ClaimAsync(default));
        Assert.Equal("execution_outcome_unknown", (await h.Submit(key: "third")).Error?.Code);
    }

    [Theory]
    [InlineData("wrong_id")]
    [InlineData("wrong_mode")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("exit")]
    public async Task Invalid_worker_results_block_the_slot_instead_of_reporting_success(string fault)
    {
        await using var h = await Harness.Create(new(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Fault: fault));
        var id = (await h.Submit()).Value!.JobId;
        Assert.True(await h.Dispatcher.RunNextAsync(default));
        Assert.Equal("OutcomeUnknown", (await h.Store.GetAsync(id, default))!.Status);
        Assert.True((await h.Store.GetSlotAsync(default)).Blocked);
    }

    [Fact] public async Task Timeout_is_not_reported_as_compilation_or_cancellation_success()
    {
        await using var h = await Harness.Create(new(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), DelayMilliseconds: 5000));
        var id = (await h.Jobs.SubmitAsync(new(EngineeringOperations.Open, new(h.Path), "timeout", TimeoutSeconds: 1), default)).Value!.JobId;
        await h.Dispatcher.RunNextAsync(default);
        Assert.Equal("OutcomeUnknown", (await h.Store.GetAsync(id, default))!.Status);
        Assert.Equal("execution_timeout", (await h.Store.GetSlotAsync(default)).Reason);
    }

    [Fact] public async Task Stdout_and_stderr_noise_are_drained_without_becoming_protocol_results()
    {
        await using var h = await Harness.Create(new(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Fault: "noise"));
        var id = (await h.Submit()).Value!.JobId;
        await h.Dispatcher.RunNextAsync(default);
        Assert.Equal("Succeeded", (await h.Store.GetAsync(id, default))!.Status);
    }

    [Fact] public async Task File_change_after_enqueue_is_detected_and_retry_still_returns_the_original_job()
    {
        await using var h = await Harness.Create();
        var first = (await h.Submit(key: "stable")).Value!;
        await File.WriteAllTextAsync(h.Path, JsonSerializer.Serialize(h.Fixture with { Implementation = "changed" }, WorkerJson.Options));
        await h.Dispatcher.RunNextAsync(default);
        Assert.Equal("workspace_revision_conflict", (await h.Store.GetAsync(first.JobId, default))!.ErrorCode);
        Assert.Equal(first.JobId, (await h.Submit(key: "stable")).Value!.JobId);
        File.Delete(h.Path);
        Assert.Equal(first.JobId, (await h.Submit(key: "stable")).Value!.JobId);
    }

    [Fact] public async Task Migration_snapshot_matches_the_runtime_model()
    {
        await using var h = await Harness.Create();
        await using var db = new PlcMcpDbContext(h.Options);
        Assert.False(db.Database.HasPendingModelChanges());
        await db.Database.MigrateAsync();
        Assert.Single(await db.ExecutionStates.ToListAsync());
    }

    [Fact] public async Task Backpressure_does_not_reject_a_retry_of_an_already_accepted_job()
    {
        await using var h = await Harness.Create();
        using var store = new SqliteJobStore(h.Options, "fixture", 1);
        var jobs = new EngineeringJobs(store, new WorkspaceFiles(h.Root), h.Executor);
        var request = new Submission(EngineeringOperations.Open, new(h.Path), "accepted");
        var first = await jobs.SubmitAsync(request, default);
        Assert.True(first.IsSuccess);
        Assert.Equal("queue_full", (await jobs.SubmitAsync(request with { IdempotencyKey = "new" }, default)).Error?.Code);
        Assert.Equal(first.Value!.JobId, (await jobs.SubmitAsync(request, default)).Value!.JobId);
    }

    [Fact] public void Production_host_has_no_fixture_assembly_dependency()
        => Assert.DoesNotContain(typeof(PlcMcp.McpHost.HostApplication).Assembly.GetReferencedAssemblies(), name => name.Name!.Contains("TestHost", StringComparison.Ordinal));

    [Fact] public async Task Workspace_access_rejects_sibling_prefix_and_linked_paths()
    {
        await using var h = await Harness.Create();
        var files = new WorkspaceFiles(h.Root);
        Assert.Equal("path_not_allowed", (await files.ResolveAsync(h.Root + "-other/sample", default)).Error?.Code);
        Assert.Equal("invalid_request", (await files.ResolveAsync("sample.fixture.json", default)).Error?.Code);
        if (!OperatingSystem.IsWindows())
        {
            var link = System.IO.Path.Combine(h.Root, "link.json"); File.CreateSymbolicLink(link, h.Path);
            Assert.Equal("path_not_allowed", (await files.ResolveAsync(link, default)).Error?.Code);
        }
    }

    [Fact] public async Task Ownership_and_execution_mode_prevent_sharing_the_same_runtime_database()
    {
        await using var h = await Harness.Create();
        using var owner = new InstanceOwnership(h.Root);
        Assert.Throws<IOException>(() => new InstanceOwnership(h.Root));
        using var wrongMode = new SqliteJobStore(h.Options, "real");
        await Assert.ThrowsAsync<InvalidOperationException>(() => wrongMode.InitializeAsync(default));
    }

    [Fact] public async Task Real_executor_does_not_fall_back_to_fixtures_when_IDE_is_missing()
    {
        await using var h = await Harness.Create();
        var jobs = new EngineeringJobs(h.Store, new WorkspaceFiles(h.Root), new InovanceExecutor(new("missing.exe", "", "", h.Root)));
        Assert.Equal("ide_unavailable", (await jobs.SubmitAsync(new(EngineeringOperations.Open, new(h.Path), "real"), default)).Error?.Code);
        Assert.Null(await h.Store.ClaimAsync(default));
    }

    private sealed class Harness : IAsyncDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "plcmcp-tests", Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Root, "sample.fixture.json");
        public FixtureProject Fixture { get; private set; } = null!;
        public DbContextOptions<PlcMcpDbContext> Options { get; private set; } = null!;
        public SqliteJobStore Store { get; private set; } = null!;
        public FixtureExecutor Executor { get; private set; } = null!;
        public EngineeringJobs Jobs { get; private set; } = null!;
        public JobDispatcher Dispatcher { get; private set; } = null!;
        public static async Task<Harness> Create(FixtureProject? fixture = null)
        {
            var h = new Harness(); Directory.CreateDirectory(h.Root);
            h.Fixture = fixture ?? new(Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
            await File.WriteAllTextAsync(h.Path, JsonSerializer.Serialize(h.Fixture, WorkerJson.Options));
            h.Options = new DbContextOptionsBuilder<PlcMcpDbContext>().UseSqlite("Data Source=" + System.IO.Path.Combine(h.Root, "jobs.db") + ";Pooling=False").Options;
            h.Store = new(h.Options, "fixture"); await h.Store.InitializeAsync(default);
            h.Executor = new(System.IO.Path.Combine(h.Root, "calls"));
            h.Jobs = new(h.Store, new WorkspaceFiles(h.Root), h.Executor);
            h.Dispatcher = new(h.Store, h.Executor);
            return h;
        }
        public Task<PlcMcp.SharedKernel.Results.Result<JobView>> Submit(string operation = EngineeringOperations.Open, string? key = null) =>
            Jobs.SubmitAsync(new(operation, new(Path, Fixture.ProgramId, Fixture.ApplicationId), key ?? Guid.NewGuid().ToString()), default);
        public async Task WaitFor(Guid id, string status)
        {
            var until = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < until)
            { if ((await Store.GetAsync(id, default))!.Status == status) return; await Task.Delay(20); }
            throw new TimeoutException("Job did not reach " + status);
        }
        public ValueTask DisposeAsync()
        {
            Store.Dispose(); Directory.Delete(Root, true); return ValueTask.CompletedTask;
        }
    }
}
