using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using PlcMcp.EngineeringService.Editing;
using PlcMcp.EngineeringService.Execution;
using PlcMcp.EntityFrameworkCore;
using PlcMcp.Execution;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;
using PlcMcp.TestHost;
using Xunit;

namespace PlcMcp.Tests;

public sealed class ManagedEditingTests
{
    [Fact] public async Task Create_edit_read_save_preserves_source_and_prior_versions_with_audited_manifests()
    {
        await using var h = await Harness.Create();
        var source = await File.ReadAllBytesAsync(h.TemplateFile);
        var created = await h.Complete(EditOperations.CreateProject, new("demo", TemplateId: "sample"));
        var first = created.Result!.Value.Deserialize<EditResult>(WorkerJson.Options)!;
        Assert.True(File.Exists(first.ProjectPath)); Assert.False(first.IsDelivery);
        var secondJob = await h.Complete(EditOperations.UpdatePou, new("demo", ObjectId: h.Fixture.ProgramId, Implementation: "counter := 42;\n"), first.Revision);
        var second = secondJob.Result!.Value.Deserialize<EditResult>(WorkerJson.Options)!;
        Assert.NotEqual(first.Revision, second.Revision); Assert.NotEqual(first.ProjectPath, second.ProjectPath);
        Assert.Equal(source, await File.ReadAllBytesAsync(h.TemplateFile));
        Assert.Equal(h.Fixture.Implementation, JsonSerializer.Deserialize<FixtureProject>(await File.ReadAllTextAsync(first.ProjectPath), WorkerJson.Options)!.Objects![0].Implementation);
        var read = await h.ReadJobs.SubmitAsync(new(EngineeringOperations.ReadProgram, new(second.ProjectPath, ObjectId: h.Fixture.ProgramId), "read-back"), default);
        await h.Dispatcher.RunNextAsync(default);
        Assert.Equal("counter := 42;\n", (await h.Store.GetAsync(read.Value!.JobId, default))!.Result!.Value.GetProperty("implementation").GetString());
        var delivery = (await h.Complete(EditOperations.SaveProject, new("demo"), second.Revision)).Result!.Value.Deserialize<EditResult>(WorkerJson.Options)!;
        Assert.True(delivery.IsDelivery); Assert.True(Directory.Exists(delivery.BackupPath));
        var head = (await h.Jobs.GetWorkspaceAsync("demo", default)).Value!;
        Assert.Equal(delivery.Revision, head.Revision); Assert.Equal(2, head.Files.Length);
        await using var db = new PlcMcpDbContext(h.Options);
        Assert.Equal(delivery.Revision, (await db.Workspaces.SingleAsync(w => w.ProjectPath == h.Files.WorkspacePath("demo"))).Revision);
        Assert.Equal(3, await db.ProjectChanges.CountAsync());
    }

    [Fact] public async Task Object_creation_and_updates_include_gvl_dut_and_empty_graphical_pous()
    {
        await using var h = await Harness.Create();
        var rev = (await h.Complete(EditOperations.CreateProject, new("demo", TemplateId: "sample"))).Result!.Value.GetProperty("revision").GetString();
        var operations = new[] {
            (EditOperations.CreatePou, new EditInput("demo", ParentId:h.Fixture.ApplicationId, Name:"Counter", Declaration:"FUNCTION_BLOCK Counter\nVAR\nvalue : INT;\nEND_VAR", Implementation:"value := 1;", PouType:"FunctionBlock", Language:"ST")),
            (EditOperations.CreateGvl, new EditInput("demo", ParentId:h.Fixture.ApplicationId, Name:"Globals", Declaration:"VAR_GLOBAL\nvalue : INT;\nEND_VAR")),
            (EditOperations.CreateDut, new EditInput("demo", ParentId:h.Fixture.ApplicationId, Name:"Data", Declaration:"TYPE Data : STRUCT\nvalue : INT;\nEND_STRUCT END_TYPE", DutType:"Structure")),
            (EditOperations.CreatePou, new EditInput("demo", ParentId:h.Fixture.ApplicationId, Name:"Graph", Declaration:"PROGRAM Graph", PouType:"Program", Language:"LD")) };
        foreach (var (op, input) in operations)
        {
            var result = (await h.Complete(op, input, rev)).Result!.Value;
            rev = result.GetProperty("revision").GetString();
            var objectId = result.GetProperty("engineeringResult").GetProperty("objectId").GetString();
            if (op is EditOperations.CreateGvl or EditOperations.CreateDut)
            {
                var update = op == EditOperations.CreateGvl ? EditOperations.UpdateGvl : EditOperations.UpdateDut;
                result = (await h.Complete(update, new("demo", ObjectId: objectId, Declaration: input.Declaration + "\n"), rev)).Result!.Value;
                rev = result.GetProperty("revision").GetString();
            }
        }
        Assert.Equal(7, (await h.ChangesList()).Count);
    }

    [Fact] public async Task Duplicate_writes_return_old_job_and_do_not_depend_on_current_revision_or_existing_template()
    {
        await using var h = await Harness.Create();
        var input = new EditInput("demo", TemplateId:"sample");
        var first = await h.Jobs.SubmitAsync(EditOperations.CreateProject, input, "create", null, 120, default);
        await h.Dispatcher.RunNextAsync(default);
        File.Delete(h.TemplateFile);
        Assert.Equal(first.Value!.JobId, (await h.Jobs.SubmitAsync(EditOperations.CreateProject, input, "create", null, 120, default)).Value!.JobId);
        var info = (await h.Jobs.GetWorkspaceAsync("demo", default)).Value!;
        var edit = new EditInput("demo", ObjectId:h.Fixture.ProgramId, Implementation:"changed");
        var request = await h.Jobs.SubmitAsync(EditOperations.UpdatePou, edit, "write", info.Revision, 120, default);
        await h.Dispatcher.RunNextAsync(default);
        Assert.Equal(request.Value!.JobId, (await h.Jobs.SubmitAsync(EditOperations.UpdatePou, edit, "write", info.Revision, 120, default)).Value!.JobId);
        Assert.Equal("idempotency_conflict", (await h.Jobs.SubmitAsync(EditOperations.UpdatePou, edit with { Implementation="different" }, "write", info.Revision, 120, default)).Error?.Code);
    }

    [Fact] public async Task Two_queued_writes_on_the_same_revision_publish_only_one_version()
    {
        await using var h = await Harness.Create();
        var revision = (await h.Complete(EditOperations.CreateProject, new("demo", TemplateId:"sample"))).Result!.Value.GetProperty("revision").GetString();
        var input = new EditInput("demo", ObjectId:h.Fixture.ProgramId, Implementation:"changed");
        var a = (await h.Jobs.SubmitAsync(EditOperations.UpdatePou, input, "a", revision, 120, default)).Value!;
        var b = (await h.Jobs.SubmitAsync(EditOperations.UpdatePou, input with { Implementation="also changed" }, "b", revision, 120, default)).Value!;
        await h.Dispatcher.RunNextAsync(default); await h.Dispatcher.RunNextAsync(default);
        var states = new[] { (await h.Store.GetAsync(a.JobId, default))!, (await h.Store.GetAsync(b.JobId, default))! };
        Assert.Single(states, j => j.Status == "Succeeded");
        Assert.Equal("workspace_revision_conflict", states.Single(j => j.Status == "Failed").ErrorCode);
        Assert.False((await h.Store.GetSlotAsync(default)).Blocked);
    }

    [Theory]
    [InlineData("save_failure")]
    [InlineData("reopen_mismatch")]
    public async Task Failed_candidates_never_become_the_current_workspace(string fault)
    {
        await using var h = await Harness.Create(fault);
        var bytes = await File.ReadAllBytesAsync(h.TemplateFile);
        var receipt = (await h.Jobs.SubmitAsync(EditOperations.CreateProject, new("demo", TemplateId:"sample"), "failure", null, 120, default)).Value!;
        await h.Dispatcher.RunNextAsync(default);
        var result = (await h.Store.GetAsync(receipt.JobId, default))!;
        Assert.Equal("Failed", result.Status);
        Assert.Equal(fault == "save_failure" ? "save_failed" : "reopen_readback_mismatch", result.ErrorCode);
        Assert.False(result.Result!.Value.GetProperty("published").GetBoolean());
        Assert.Equal("workspace_not_found", (await h.Jobs.GetWorkspaceAsync("demo", default)).Error?.Code);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(h.TemplateFile));
        var evidence = (await h.Changes.GetChangeAsync(receipt.JobId, default))!;
        Assert.True(evidence.ExecutorStopped); Assert.True(Directory.Exists(evidence.BackupPath));
    }

    [Fact] public async Task Crash_after_publication_reconciles_from_files_and_journal_without_reexecution()
    {
        await using var h = await Harness.Create();
        var accepted = (await h.Jobs.SubmitAsync(EditOperations.CreateProject, new("demo", TemplateId:"sample"), "create", null, 120, default)).Value!;
        var request = (await h.Store.ClaimAsync(default))!.Request;
        var response = await h.Executor.ExecuteAsync(request, default);
        Assert.Equal("succeeded", response.Outcome);
        await h.Store.InitializeAsync(default); // crash before FinishAsync, metadata publication already committed
        Assert.Equal("OutcomeUnknown", (await h.Store.GetAsync(accepted.JobId, default))!.Status);
        Assert.True((await h.Store.GetSlotAsync(default)).Blocked);
        var reconciled = await h.Jobs.ReconcileAsync(accepted.JobId, default);
        Assert.True(reconciled.IsSuccess, reconciled.Error?.Code); Assert.Equal("Succeeded", reconciled.Value!.Status);
        Assert.False((await h.Store.GetSlotAsync(default)).Blocked);
        Assert.Single(await h.ChangesList());
        Assert.Equal("Succeeded", (await h.Jobs.ReconcileAsync(accepted.JobId, default)).Value!.Status);
    }

    [Fact] public async Task Reconciliation_refuses_tampered_published_files()
    {
        await using var h = await Harness.Create();
        var accepted = (await h.Jobs.SubmitAsync(EditOperations.CreateProject, new("demo", TemplateId:"sample"), "create", null, 120, default)).Value!;
        var response = await h.Executor.ExecuteAsync((await h.Store.ClaimAsync(default))!.Request, default);
        var payload = response.Payload!.Value.Deserialize<EditResult>(WorkerJson.Options)!;
        await File.AppendAllTextAsync(payload.ProjectPath, "tampered");
        await h.Store.InitializeAsync(default);
        Assert.Equal("reconciliation_conflict", (await h.Jobs.ReconcileAsync(accepted.JobId, default)).Error?.Code);
        Assert.True((await h.Store.GetSlotAsync(default)).Blocked);
    }

    [Fact] public async Task Interrupted_prelaunch_preparation_is_failed_without_replaying_or_launching_a_worker()
    {
        await using var h = await Harness.Create();
        var accepted = (await h.Jobs.SubmitAsync(EditOperations.CreateProject, new("demo", TemplateId:"sample"), "create", null, 120, default)).Value!;
        var request = (await h.Store.ClaimAsync(default))!.Request;
        await h.Changes.RecordAsync(new(request.JobId, h.Files.WorkspacePath("demo"), "preparing", request.ExpectedRevision, null, null, null, null, true, null, []), default);
        await h.Store.InitializeAsync(default);
        var result = await h.Jobs.ReconcileAsync(accepted.JobId, default);
        Assert.Equal("Failed", result.Value!.Status); Assert.Equal("interrupted_before_publication", result.Value.ErrorCode);
        Assert.False((await h.Store.GetSlotAsync(default)).Blocked);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Changes.RecordAsync(new(request.JobId, h.Files.WorkspacePath("demo"), "executing", request.ExpectedRevision, null, null, null, null, false, null, []), default));
    }

    [Theory]
    [InlineData("after_save_exit")]
    [InlineData("false_save")]
    [InlineData("wrong_mode")]
    public async Task Unknown_or_invalid_write_results_are_quarantined_and_cannot_be_force_unlocked(string fault)
    {
        await using var h = await Harness.Create(fault);
        var job = (await h.Jobs.SubmitAsync(EditOperations.CreateProject, new("demo", TemplateId:"sample"), "unknown", null, 120, default)).Value!;
        await h.Dispatcher.RunNextAsync(default);
        Assert.Equal("OutcomeUnknown", (await h.Store.GetAsync(job.JobId, default))!.Status);
        Assert.Equal("reconciliation_unproven", (await h.Jobs.ReconcileAsync(job.JobId, default)).Error?.Code);
        Assert.True((await h.Store.GetSlotAsync(default)).Blocked);
        Assert.Null(await h.Changes.GetHeadAsync(h.Files.WorkspacePath("demo"), default));
    }

    [Fact] public async Task A_modified_dependency_blocks_edits_even_if_project_bytes_are_unchanged()
    {
        await using var h = await Harness.Create();
        var result = (await h.Complete(EditOperations.CreateProject, new("demo", TemplateId:"sample"))).Result!.Value.Deserialize<EditResult>(WorkerJson.Options)!;
        await File.AppendAllTextAsync(Path.Combine(Path.GetDirectoryName(result.ProjectPath)!, "deps", "device.txt"), "changed dependency");
        Assert.Equal("workspace_revision_conflict", (await h.Jobs.GetWorkspaceAsync("demo", default)).Error?.Code);
        var edit = new EditInput("demo", ObjectId:h.Fixture.ProgramId, Implementation:"new");
        Assert.Equal("workspace_revision_conflict", (await h.Jobs.SubmitAsync(EditOperations.UpdatePou, edit, "write", result.Revision, 120, default)).Error?.Code);
    }

    [Fact] public async Task Template_mode_paths_missing_files_and_missing_revisions_are_rejected()
    {
        await using var h = await Harness.Create();
        Assert.Equal("invalid_workspace_name", (await h.Jobs.SubmitAsync(EditOperations.CreateProject, new("../escape", TemplateId:"sample"), "x", null, 120, default)).Error?.Code);
        Assert.Equal("expected_revision_required", (await h.Jobs.SubmitAsync(EditOperations.SaveProject, new("demo"), "save", null, 120, default)).Error?.Code);
        var template = h.Template with { ExecutionMode="real" };
        await File.WriteAllTextAsync(h.Descriptor, JsonSerializer.Serialize(template, WorkerJson.Options));
        Assert.Equal("invalid_template", (await h.Jobs.SubmitAsync(EditOperations.CreateProject, new("demo", TemplateId:"sample"), "x", null, 120, default)).Error?.Code);
        await File.WriteAllTextAsync(h.Descriptor, JsonSerializer.Serialize(h.Template with { Files=["project.fixture.json", "../escape"] }, WorkerJson.Options));
        Assert.Equal("path_not_allowed", (await h.Jobs.SubmitAsync(EditOperations.CreateProject, new("demo", TemplateId:"sample"), "x", null, 120, default)).Error?.Code);
        await File.WriteAllTextAsync(h.Descriptor, JsonSerializer.Serialize(h.Template, WorkerJson.Options));
        File.Delete(h.TemplateFile);
        Assert.Equal("project_unavailable", (await h.Jobs.SubmitAsync(EditOperations.CreateProject, new("demo", TemplateId:"sample"), "x", null, 120, default)).Error?.Code);
    }

    [Fact] public async Task Linked_template_files_are_rejected_and_template_changes_after_enqueue_do_not_publish()
    {
        await using var h = await Harness.Create();
        var receipt = (await h.Jobs.SubmitAsync(EditOperations.CreateProject, new("demo", TemplateId:"sample"), "x", null, 120, default)).Value!;
        await File.AppendAllTextAsync(Path.Combine(Path.GetDirectoryName(h.TemplateFile)!, "deps", "device.txt"), "changed");
        await h.Dispatcher.RunNextAsync(default);
        Assert.Equal("workspace_revision_conflict", (await h.Store.GetAsync(receipt.JobId, default))!.ErrorCode);
        if (!OperatingSystem.IsWindows())
        {
            File.Delete(h.TemplateFile); File.CreateSymbolicLink(h.TemplateFile, h.Descriptor);
            Assert.Equal("path_not_allowed", (await h.Jobs.SubmitAsync(EditOperations.CreateProject, new("other", TemplateId:"sample"), "link", null, 120, default)).Error?.Code);
        }
    }

    [Fact] public async Task Cancelled_queued_edit_has_no_candidate_or_workspace()
    {
        await using var h = await Harness.Create();
        var job = (await h.Jobs.SubmitAsync(EditOperations.CreateProject, new("demo", TemplateId:"sample"), "cancel", null, 120, default)).Value!;
        await h.Store.CancelAsync(job.JobId, default);
        Assert.False(await h.Dispatcher.RunNextAsync(default));
        Assert.Null(await h.Changes.GetChangeAsync(job.JobId, default));
        Assert.False(Directory.Exists(h.Files.WorkspacePath("demo")));
    }

    [Fact] public async Task Migration_upgrades_an_existing_execution_database_without_losing_jobs()
    {
        await using var h = await Harness.Create();
        var job = (await h.Jobs.SubmitAsync(EditOperations.CreateProject, new("demo", TemplateId:"sample"), "persist", null, 120, default)).Value!;
        await using var db = new PlcMcpDbContext(h.Options);
        var migrator = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        // Retain the already-shipped legacy ID. EF derives its lookup name from that ID.
        var legacyName = db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrationsIdGenerator>().GetName("202611060001_InitialExecution");
        await migrator.MigrateAsync(legacyName);
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.NotNull(await h.Store.GetAsync(job.JobId, default));
        Assert.Equal(2, (await db.Database.GetAppliedMigrationsAsync()).Count());
    }

    [Fact] public async Task Publication_acknowledgement_loss_is_reconciled_from_the_committed_journal()
    {
        await using var h = await Harness.Create();
        var journal = new PublicationProbe(h.Changes, loseAcknowledgement: true);
        var executor = new ManagedProjectExecutor(new FixtureExecutor(Path.Combine(h.Root, "other-calls")), h.Files, journal);
        var job = (await h.Jobs.SubmitAsync(EditOperations.CreateProject, new("demo", TemplateId:"sample"), "ack-loss", null, 120, default)).Value!;
        await new JobDispatcher(h.Store, executor).RunNextAsync(default);
        Assert.Equal("OutcomeUnknown", (await h.Store.GetAsync(job.JobId, default))!.Status);
        Assert.Equal("published", (await h.Changes.GetChangeAsync(job.JobId, default))!.Stage);
        Assert.Equal("Succeeded", (await h.Jobs.ReconcileAsync(job.JobId, default)).Value!.Status);
        Assert.False((await h.Store.GetSlotAsync(default)).Blocked);
    }

    [Fact] public async Task A_late_executor_cannot_publish_after_reconciliation_revokes_ownership()
    {
        await using var h = await Harness.Create();
        var journal = new PublicationProbe(h.Changes, loseAcknowledgement: false);
        var executor = new ManagedProjectExecutor(new FixtureExecutor(Path.Combine(h.Root, "other-calls")), h.Files, journal);
        var job = (await h.Jobs.SubmitAsync(EditOperations.CreateProject, new("demo", TemplateId:"sample"), "late", null, 120, default)).Value!;
        var running = executor.ExecuteAsync((await h.Store.ClaimAsync(default))!.Request, default);
        await journal.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        try
        {
            await h.Store.InitializeAsync(default);
            Assert.Equal("Failed", (await h.Jobs.ReconcileAsync(job.JobId, default)).Value!.Status);
        }
        finally { journal.Release.TrySetResult(); }
        Assert.Equal("failed", (await running.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
        Assert.Null(await h.Changes.GetHeadAsync(h.Files.WorkspacePath("demo"), default));
        Assert.False((await h.Store.GetSlotAsync(default)).Blocked);
    }

    private sealed class PublicationProbe(IProjectChangeStore inner, bool loseAcknowledgement) : IProjectChangeStore
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ManagedHead?> GetHeadAsync(string path, CancellationToken token) => inner.GetHeadAsync(path, token);
        public Task<ChangeEvidence?> GetChangeAsync(Guid id, CancellationToken token) => inner.GetChangeAsync(id, token);
        public Task RecordAsync(ChangeEvidence evidence, CancellationToken token, string? expectedStage = null) => inner.RecordAsync(evidence, token, expectedStage);
        public async Task<bool> PublishAsync(ManagedHead head, ChangeEvidence evidence, bool creating, CancellationToken token)
        {
            Reached.TrySetResult();
            if (!loseAcknowledgement) await Release.Task;
            var published = await inner.PublishAsync(head, evidence, creating, token);
            if (published && loseAcknowledgement) throw new IOException("Injected acknowledgement loss after commit.");
            return published;
        }
    }

    internal sealed class Harness : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "plcmcp-edit-tests", Guid.NewGuid().ToString("N"));
        public string TemplateFile => Path.Combine(Root, "templates", "sample", "project.fixture.json");
        public string Descriptor => Path.Combine(Root, "templates", "sample", "template.json");
        public FixtureProject Fixture { get; private set; } = null!;
        public TemplateDefinition Template { get; private set; } = null!;
        public DbContextOptions<PlcMcpDbContext> Options { get; private set; } = null!;
        public SqliteJobStore Store { get; private set; } = null!;
        public SqliteProjectChangeStore Changes { get; private set; } = null!;
        public ManagedProjectFiles Files { get; private set; } = null!;
        public ManagedProjectExecutor Executor { get; private set; } = null!;
        public ManagedEditingJobs Jobs { get; private set; } = null!;
        public EngineeringJobs ReadJobs { get; private set; } = null!;
        public JobDispatcher Dispatcher { get; private set; } = null!;
        public static async Task<Harness> Create(string? fault = null)
        {
            var h = new Harness(); Directory.CreateDirectory(Path.Combine(h.Root, "templates", "sample", "deps"));
            h.Fixture = new(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Fault:fault);
            h.Template = new("sample", "project.fixture.json", ["project.fixture.json", "deps/device.txt"], "fixture", "test-controller", [], "fixture");
            await File.WriteAllTextAsync(h.TemplateFile, JsonSerializer.Serialize(h.Fixture, WorkerJson.Options));
            await File.WriteAllTextAsync(h.Descriptor, JsonSerializer.Serialize(h.Template, WorkerJson.Options));
            await File.WriteAllTextAsync(Path.Combine(h.Root, "templates", "sample", "deps", "device.txt"), "test dependency, not a vendor description");
            h.Options = new DbContextOptionsBuilder<PlcMcpDbContext>().UseSqlite("Data Source=" + Path.Combine(h.Root, "jobs.db") + ";Pooling=False").Options;
            h.Store = new(h.Options, "fixture"); await h.Store.InitializeAsync(default);
            h.Files = new(h.Root); h.Changes = new(h.Options);
            h.Executor = new(new FixtureExecutor(Path.Combine(h.Root, "invocations")), h.Files, h.Changes);
            h.Jobs = new(h.Store, h.Changes, h.Files, h.Executor);
            h.ReadJobs = new(h.Store, new WorkspaceFiles(h.Root), h.Executor);
            h.Dispatcher = new(h.Store, h.Executor);
            return h;
        }
        public async Task<JobView> Complete(string op, EditInput input, string? revision = null)
        {
            var result = await Jobs.SubmitAsync(op, input, Guid.NewGuid().ToString(), revision, 120, default);
            Assert.True(result.IsSuccess, result.Error?.Code);
            Assert.True(await Dispatcher.RunNextAsync(default));
            var job = (await Store.GetAsync(result.Value!.JobId, default))!;
            Assert.True(job.Status == "Succeeded", job.Status + ": " + job.ErrorCode + ": " + job.Result?.GetRawText());
            return job;
        }
        public async Task<List<ProjectChangeRow>> ChangesList() { await using var db = new PlcMcpDbContext(Options); return await db.ProjectChanges.ToListAsync(); }
        public ValueTask DisposeAsync() { Store.Dispose(); Directory.Delete(Root, true); return ValueTask.CompletedTask; }
    }
}
