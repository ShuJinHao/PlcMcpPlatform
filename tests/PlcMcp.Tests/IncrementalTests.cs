using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlcMcp.Core.Workspaces;
using PlcMcp.EngineeringService.Batch;
using PlcMcp.EngineeringService.Editing;
using PlcMcp.EngineeringService.Exchange;
using PlcMcp.EngineeringService.Execution;
using PlcMcp.EngineeringService.Incremental;
using PlcMcp.Execution;
using PlcMcp.Services.Contracts.Batch;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Incremental;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;
using PlcMcp.TestHost;
using Xunit;

namespace PlcMcp.Tests;

public sealed class IncrementalTests
{
    private sealed record Services(ManagedEditingJobs Edits, IncrementalService Reads, BatchService Batches, JobDispatcher Dispatcher);
    private static Services Compose(ManagedEditingTests.Harness h, IEngineeringExecutor? raw = null)
    {
        var artifacts = new ExchangeArtifacts(Path.Combine(h.Root, "exchange"));
        var managed = new ManagedProjectExecutor(new BatchExecutor(new IncrementalEditExecutor(new ExchangeExecutor(
            raw ?? new FixtureExecutor(Path.Combine(h.Root, "incremental-calls")), artifacts)), new BatchJournalStore(Path.Combine(h.Root, "batch-journals"))), h.Files, h.Changes);
        var executor = new IncrementalReadExecutor(managed, h.Files, h.Changes, h.Changes);
        var edits = new ManagedEditingJobs(h.Store, h.Changes, h.Files, executor);
        return new(edits, new(h.Store, h.Files, h.Changes, h.Changes, executor), new(edits, artifacts), new(h.Store, executor));
    }
    private static async Task<JobView> Finish(ManagedEditingTests.Harness h, Services c, Result<JobView> receipt, string status = "Succeeded")
    {
        Assert.True(receipt.IsSuccess, receipt.Error?.Code);
        Assert.True(await c.Dispatcher.RunNextAsync(default));
        var job = (await h.Store.GetAsync(receipt.Value!.JobId, default))!;
        Assert.True(job.Status == status, job.ErrorCode + ": " + job.Result); return job;
    }
    private static async Task<AdoptionSource> Source(ManagedEditingTests.Harness h)
    {
        var path = Path.Combine(h.Root, "imports", "existing"); Directory.CreateDirectory(Path.Combine(path, "deps"));
        File.Copy(h.TemplateFile, Path.Combine(path, "plant.fixture.json"));
        await File.WriteAllTextAsync(Path.Combine(path, "deps", "companion.txt"), "declared saved file");
        await File.WriteAllTextAsync(Path.Combine(path, "not-declared.txt"), "do not copy");
        return new("existing", "plant.fixture.json", ["plant.fixture.json", "deps/companion.txt"], "fixture", "test-controller", []);
    }
    [Fact] public async Task Adopt_context_patch_diff_history_restore_preserve_source_and_publish_new_versions()
    {
        await using var h = await ManagedEditingTests.Harness.Create(); var c = Compose(h); var source = await Source(h);
        var original = await File.ReadAllBytesAsync(Path.Combine(h.Root, "imports", "existing", source.ProjectFile));
        var preview = await c.Reads.PreviewAdoptionAsync(source, default);
        var adoption = await c.Edits.SubmitAsync(IncrementalOperations.Adopt, new("plant", Adoption: source), "adopt", preview.SourceRevision, 600, default);
        var adopted = await Finish(h, c, adoption); var first = adopted.Result!.Value.GetProperty("revision").GetString()!;
        var context = await Finish(h, c, await c.Reads.ReadAsync(IncrementalOperations.Context, first,
            new("plant", first, h.Fixture.ApplicationId, h.Fixture.ProgramId, PageSize: 1, MaxTextChars: 8), "context", 600, default));
        var page = context.Result!.Value.Deserialize<ContextPage>(WorkerJson.Options)!;
        var obj = Assert.Single(page.Objects); Assert.Equal(h.Fixture.ProgramId, obj.ObjectId);
        Assert.Equal(TextPatchRules.Hash(h.Fixture.Implementation), obj.Implementation!.Sha256);
        Assert.NotNull(page.NextOffset); Assert.NotNull(obj.Implementation.NextOffset);
        var plan = new BatchPlan(h.Fixture.ApplicationId, [new("interlock", IncrementalOperations.Patch,
            new(ObjectId: h.Fixture.ProgramId, Patch: new("implementation", obj.Implementation.Sha256, h.Fixture.Implementation, "IF enabled THEN\n counter := 1;\nEND_IF;")))]);
        var patchPreview = await c.Batches.PreviewAsync("plant", first, plan, default);
        var patched = await Finish(h, c, await c.Batches.ApplyAsync("plant", first, plan, patchPreview.PlanSha256, "patch", 600, default));
        var second = patched.Result!.Value.GetProperty("revision").GetString()!;
        var diff = await Finish(h, c, await c.Reads.ReadAsync(IncrementalOperations.Diff, second,
            new("plant", first, h.Fixture.ApplicationId, h.Fixture.ProgramId, CompareRevision: second), "diff", 600, default));
        var difference = diff.Result!.Value.Deserialize<ProjectDiff>(WorkerJson.Options)!;
        Assert.Single(difference.Files); Assert.True(difference.ObjectDifference!.Value.GetProperty("implementationChanged").GetBoolean());
        var restored = await Finish(h, c, await c.Edits.SubmitAsync(IncrementalOperations.Restore, new("plant", RestoreRevision: first), "restore", second, 600, default));
        var third = restored.Result!.Value.GetProperty("revision").GetString()!;
        Assert.NotEqual(first, third); Assert.NotEqual(second, third);
        var history = await c.Reads.HistoryAsync("plant", third, 0, 2, default); Assert.Equal(2, history.Versions.Length); Assert.Equal(2, history.NextOffset);
        Assert.True(Assert.Single((await c.Reads.HistoryAsync("plant", third, 2, 2, default)).Versions).IsCurrent);
        var restoredFile = restored.Result.Value.GetProperty("projectPath").GetString()!;
        var project = JsonSerializer.Deserialize<FixtureProject>(await File.ReadAllTextAsync(restoredFile), WorkerJson.Options)!;
        Assert.Equal(h.Fixture.Implementation, project.Objects!.Single(x => x.Id == h.Fixture.ProgramId).Implementation);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(restoredFile)!, "not-declared.txt")));
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(h.Root, "imports", "existing", source.ProjectFile)));
        File.Delete(Path.Combine(h.Root, "imports", "existing", source.ProjectFile));
        Assert.Equal(adoption.Value!.JobId, (await c.Edits.SubmitAsync(IncrementalOperations.Adopt, new("plant", Adoption: source), "adopt", preview.SourceRevision, 600, default)).Value!.JobId);
        Assert.Equal("workspace_revision_conflict", (await c.Reads.ReadAsync(IncrementalOperations.Context, first,
            new("plant", first, h.Fixture.ApplicationId), "stale", 600, default)).Error?.Code);
    }
    [Fact] public async Task Adoption_source_changes_after_enqueue_and_unsafe_paths_never_publish()
    {
        await using var h = await ManagedEditingTests.Harness.Create(); var c = Compose(h); var source = await Source(h);
        var preview = await c.Reads.PreviewAdoptionAsync(source, default);
        var job = await c.Edits.SubmitAsync(IncrementalOperations.Adopt, new("plant", Adoption: source), "adopt", preview.SourceRevision, 600, default);
        await File.AppendAllTextAsync(Path.Combine(h.Root, "imports", "existing", "deps", "companion.txt"), "changed");
        Assert.Equal("workspace_revision_conflict", (await Finish(h, c, job, "Failed")).ErrorCode);
        Assert.Null(await h.Changes.GetHeadAsync(h.Files.WorkspacePath("plant"), default));
        Assert.Equal("path_not_allowed", (await Assert.ThrowsAsync<ProjectEditException>(() => c.Reads.PreviewAdoptionAsync(source with { Files = [source.ProjectFile, "../escape"] }, default))).Code);
        Assert.Equal("invalid_source_name", (await Assert.ThrowsAsync<ProjectEditException>(() => c.Reads.PreviewAdoptionAsync(source with { SourceName = "../../templates/sample" }, default))).Code);
        if (!OperatingSystem.IsWindows())
        {
            var path = Path.Combine(h.Root, "imports", "existing", "deps", "companion.txt"); File.Delete(path); File.CreateSymbolicLink(path, h.TemplateFile);
            Assert.Equal("path_not_allowed", (await Assert.ThrowsAsync<ProjectEditException>(() => c.Reads.PreviewAdoptionAsync(source, default))).Code);
        }
    }
    [Theory]
    [InlineData("hash", "source_hash_conflict")]
    [InlineData("missing", "patch_old_text_missing")]
    [InlineData("ambiguous", "patch_ambiguous")]
    [InlineData("compile", "compile_failed")]
    public async Task Conflicting_or_uncompilable_patches_preserve_current_head(string scenario, string code)
    {
        await using var h = await ManagedEditingTests.Harness.Create(); var c = Compose(h);
        var initial = (await h.Complete(EditOperations.CreateProject, new("demo", TemplateId: "sample"))).Result!.Value;
        var revision = initial.GetProperty("revision").GetString()!;
        var source = h.Fixture.Implementation;
        var patch = new TextPatch("implementation", scenario == "hash" ? new string('0', 64) : TextPatchRules.Hash(source),
            scenario == "missing" ? "not present" : scenario == "ambiguous" ? "counter" : source, scenario == "compile" ? "FIXTURE_ERROR" : "counter := 1;");
        var plan = new BatchPlan(h.Fixture.ApplicationId, [new("patch", IncrementalOperations.Patch, new(ObjectId: h.Fixture.ProgramId, Patch: patch))]);
        var preview = await c.Batches.PreviewAsync("demo", revision, plan, default);
        var failed = await Finish(h, c, await c.Batches.ApplyAsync("demo", revision, plan, preview.PlanSha256, "bad", 600, default), "Failed");
        Assert.Equal(code, failed.ErrorCode); Assert.Equal(revision, (await c.Edits.GetWorkspaceAsync("demo", default)).Value!.Revision);
        Assert.False((await h.Store.GetSlotAsync(default)).Blocked);
    }
    [Fact] public async Task Legacy_versions_use_the_original_manifest_and_reject_tampered_history()
    {
        await using var h = await ManagedEditingTests.Harness.Create(); var c = Compose(h);
        var initial = await h.Complete(EditOperations.CreateProject, new("demo", TemplateId: "sample"));
        var first = initial.Result!.Value.GetProperty("revision").GetString()!;
        await using (var db = new PlcMcp.EntityFrameworkCore.PlcMcpDbContext(h.Options))
        {
            var row = await db.ProjectChanges.SingleAsync(x => x.JobId == initial.JobId);
            var evidence = JsonSerializer.Deserialize<ChangeEvidence>(row.EvidenceJson, WorkerJson.Options)!;
            row.EvidenceJson = JsonSerializer.Serialize(evidence with { Snapshot = null }, WorkerJson.Options); await db.SaveChangesAsync();
        }
        var second = (await h.Complete(EditOperations.UpdatePou, new("demo", ObjectId: h.Fixture.ProgramId, Implementation: "counter := 2;"), first)).Result!.Value.GetProperty("revision").GetString()!;
        var read = await c.Reads.ReadAsync(IncrementalOperations.Context, second, new("demo", first, h.Fixture.ApplicationId), "legacy", 600, default);
        await Finish(h, c, read);
        await File.AppendAllTextAsync(initial.Result.Value.GetProperty("projectPath").GetString()!, "tampered");
        Assert.Equal("workspace_revision_conflict", (await c.Edits.SubmitAsync(IncrementalOperations.Restore, new("demo", RestoreRevision: first), "restore", second, 600, default)).Error?.Code);
        Assert.Equal(second, (await c.Edits.GetWorkspaceAsync("demo", default)).Value!.Revision);
    }
    [Theory]
    [InlineData("failed", "Failed")]
    [InlineData("unknown", "OutcomeUnknown")]
    public async Task Restore_failure_or_uncertain_worker_does_not_advance_history(string outcome, string status)
    {
        await using var h = await ManagedEditingTests.Harness.Create();
        var first = (await h.Complete(EditOperations.CreateProject, new("demo", TemplateId: "sample"))).Result!.Value.GetProperty("revision").GetString()!;
        var c = Compose(h, new FaultWorker(new FixtureExecutor(Path.Combine(h.Root, "fault-calls")), outcome));
        var result = await Finish(h, c, await c.Edits.SubmitAsync(IncrementalOperations.Restore, new("demo", RestoreRevision: first), "restore", first, 600, default), status);
        Assert.Equal(first, (await c.Edits.GetWorkspaceAsync("demo", default)).Value!.Revision);
        Assert.Equal(outcome == "unknown", (await h.Store.GetSlotAsync(default)).Blocked);
        if (outcome == "unknown") Assert.Equal("reconciliation_unproven", (await c.Edits.ReconcileAsync(result.JobId, default)).Error?.Code);
    }
    [Theory]
    [InlineData("aaaa", "aa", "patch_ambiguous")]
    [InlineData("one", "two", "patch_old_text_missing")]
    [InlineData("one", "", "patch_old_text_required")]
    public void Exact_patch_rules_reject_overlaps_absence_and_empty_search(string source, string old, string code)
        => Assert.Equal(code, TextPatchRules.Apply(source, TextPatchRules.Hash(source), old, "new").Error);
    [Fact] public void Patch_hash_and_replacement_use_documented_line_ending_normalization()
    {
        Assert.Equal(TextPatchRules.Hash("a\nb\n"), TextPatchRules.Hash("a\r\nb\r"));
        Assert.Equal("a\nc\n", TextPatchRules.Apply("a\r\nb\r\n", TextPatchRules.Hash("a\nb\n"), "b\r\n", "c\n").Source);
    }
    private sealed class FaultWorker(IEngineeringExecutor inner, string outcome) : IEngineeringExecutor
    {
        public string ExecutionMode => inner.ExecutionMode;
        public OperationError? Unavailability => null;
        public Task<WorkerResponse> ExecuteAsync(WorkerRequest request, CancellationToken token) => request.Operation == EditOperations.VerifyProject
            ? Task.FromResult(outcome == "unknown" ? WorkerJson.Unknown(request, "injected_interruption") : WorkerJson.Failed(request, "injected_failure")) : inner.ExecuteAsync(request, token);
    }
}
