using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlcMcp.EntityFrameworkCore;
using PlcMcp.Execution;
using PlcMcp.Services.Contracts.Delivery;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Maintenance;
using PlcMcp.Services.Contracts.Workers;
using Xunit;

namespace PlcMcp.Tests;

public sealed class RetentionTests
{
    private sealed class StateSource : IRetentionStateSource
    {
        public RetentionState Value { get; set; } = new("unchanged", false, [], []);
        public Task<RetentionState> ReadAsync(CancellationToken token) => Task.FromResult(Value);
    }
    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "plcmcp-retention", Guid.NewGuid().ToString("N"));
        public string Data => Path.Combine(Root, "data");
        public string Workspace => Path.Combine(Root, "workspaces");
        public StateSource Source { get; } = new();
        public RetentionMaintenance Service { get; }
        public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        public Files() { Directory.CreateDirectory(Data); Directory.CreateDirectory(Workspace); Service = new(Data, Workspace, Source); }
        public static void Old(string path) => File.SetLastWriteTimeUtc(path, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        public async Task<string> Upload()
        {
            var uploads = new ExchangeUploads(Path.Combine(Data, "uploads"), (_, _, _) => throw new InvalidOperationException());
            var bytes = Encoding.UTF8.GetBytes("upload"); var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var session = await uploads.BeginAsync("upload", "plcopen", bytes.Length, digest, default);
            await uploads.AppendAsync(session.UploadId, 0, Convert.ToBase64String(bytes), default);
            foreach (var path in Directory.GetFiles(Path.Combine(Data, "uploads"))) Old(path);
            return session.UploadId;
        }
        public async Task<string> Package(string workspace = "demo", string revision = "old", string content = "historical archive")
        {
            var bytes = Encoding.UTF8.GetBytes(content); var id = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var root = Path.Combine(Data, "deliveries"); Directory.CreateDirectory(root);
            await File.WriteAllBytesAsync(Path.Combine(root, id + ".zip"), bytes);
            await File.WriteAllTextAsync(Path.Combine(root, id + ".json"), JsonSerializer.Serialize(new DeliveryArtifact(id, id, bytes.Length, workspace, revision, "project", "fixture", "simulated", false, 1), WorkerJson.Options));
            Old(Path.Combine(root, id + ".zip")); Old(Path.Combine(root, id + ".json")); return id;
        }
        public async Task<string> Candidate()
        {
            var id = Guid.NewGuid(); var path = Path.Combine(Workspace, "managed", "demo", "changes", id.ToString("N"), "candidate");
            Directory.CreateDirectory(path); await File.WriteAllTextAsync(Path.Combine(path, "program.project"), "failed candidate"); Old(Path.Combine(path, "program.project"));
            var before = Path.Combine(Path.GetDirectoryName(path)!, "before"); Directory.CreateDirectory(before);
            await File.WriteAllTextAsync(Path.Combine(before, "program.project"), "backup");
            Source.Value = Source.Value with { Failures = [new(new(id, Path.Combine(Workspace, "managed", "demo"), "failed", "old", path, before, null, "compile_failed", true, null, []), Now.AddDays(-100))] };
            return path;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
    [Fact] public async Task Preview_is_read_only_and_apply_expires_only_selected_files_idempotently()
    {
        using var f = new Files(); var upload = await f.Upload(); var package = await f.Package(); var candidate = await f.Candidate();
        Directory.CreateDirectory(Path.Combine(f.Data, "exchange")); var exchange = Path.Combine(f.Data, "exchange", "preserved.json"); await File.WriteAllTextAsync(exchange, "source XML");
        var plan = await f.Service.PreviewAsync(new(), f.Now, default);
        Assert.Equal(3, plan.Items.Length); Assert.True(Directory.Exists(candidate));
        var result = await f.Service.ApplyAsync(plan, f.Now, default);
        Assert.Equal("completed", result.State); Assert.Equal(3, result.CompletedItems.Length);
        Assert.False(Directory.Exists(candidate)); Assert.True(Directory.Exists(Path.Combine(Path.GetDirectoryName(candidate)!, "before")));
        Assert.True(File.Exists(exchange)); Assert.False(File.Exists(Path.Combine(f.Data, "deliveries", package + ".zip")));
        Assert.True(File.Exists(Path.Combine(f.Data, "deliveries", package + ".json")));
        var repeated = await f.Service.ApplyAsync(plan, f.Now.AddDays(2), default);
        Assert.Equal(result.PlanSha256, repeated.PlanSha256); Assert.Equal(result.CompletedItems, repeated.CompletedItems);
        Assert.Empty((await f.Service.PreviewAsync(new(), f.Now, default)).Items);
        var uploads = new ExchangeUploads(Path.Combine(f.Data, "uploads"), (_, _, _) => throw new InvalidOperationException());
        Assert.Equal("upload_expired", (await Assert.ThrowsAsync<ProjectEditException>(() => uploads.CompleteAsync(upload, default))).Code);
        Assert.Equal("delivery_expired", (await Assert.ThrowsAsync<ProjectEditException>(() => new DeliveryArtifacts(Path.Combine(f.Data, "deliveries")).ReadAsync(package, 0, 10, default))).Code);
        await uploads.AbortAsync(upload, default);
        Assert.False(File.Exists(Path.Combine(f.Data, "uploads", upload + ".expired.json")));
    }
    [Fact] public async Task Current_revision_and_pinned_deliveries_are_kept()
    {
        using var f = new Files(); var current = await f.Package(revision: "current"); var pinned = await f.Package(content: "pinned");
        var snapshot = new ProjectSnapshot("versions/current", "program.project", "current", [], new("sample", "program.project", [], "fixture", "test", [], "fixture"));
        f.Source.Value = f.Source.Value with { Heads = [new("workspace", "demo", snapshot, Guid.NewGuid(), true)] };
        Assert.Empty((await f.Service.PreviewAsync(new(PinnedDeliveryIds: [pinned]), f.Now, default)).Items);
        Assert.True(File.Exists(Path.Combine(f.Data, "deliveries", current + ".zip")));
    }
    [Theory][InlineData(true)][InlineData(false)]
    public async Task Changed_files_or_state_reject_entire_plan_before_deletion(bool fileChange)
    {
        using var f = new Files(); var upload = await f.Upload(); var package = await f.Package();
        var plan = await f.Service.PreviewAsync(new(), f.Now, default);
        if (fileChange) await File.AppendAllTextAsync(Path.Combine(f.Data, "uploads", upload + ".part"), "changed");
        else f.Source.Value = f.Source.Value with { Fingerprint = "new head" };
        Assert.Equal("maintenance_plan_stale", (await Assert.ThrowsAsync<ProjectEditException>(() => f.Service.ApplyAsync(plan, f.Now, default))).Code);
        Assert.True(File.Exists(Path.Combine(f.Data, "deliveries", package + ".zip")));
    }
    [Fact] public async Task Active_owner_prevents_preview_and_apply()
    {
        using var f = new Files(); await f.Upload(); var plan = await f.Service.PreviewAsync(new(), f.Now, default);
        using var owner = new InstanceOwnership(f.Data);
        await Assert.ThrowsAsync<IOException>(() => f.Service.PreviewAsync(new(), f.Now, default));
        await Assert.ThrowsAsync<IOException>(() => f.Service.ApplyAsync(plan, f.Now, default));
    }
    [Fact] public async Task Queued_job_in_real_store_blocks_maintenance_without_changing_job()
    {
        await using var h = await ManagedEditingTests.Harness.Create();
        var job = await h.Jobs.SubmitAsync(EditOperations.CreateProject, new("demo", TemplateId: "sample"), "pending", null, 120, default);
        Assert.True(job.IsSuccess);
        var source = new SqliteRetentionState(h.Options);
        Assert.True((await source.ReadAsync(default)).Blocked);
        var maintenance = new RetentionMaintenance(Path.Combine(h.Root, "data"), h.Root, source);
        Assert.Equal("maintenance_execution_unresolved", (await Assert.ThrowsAsync<ProjectEditException>(() => maintenance.PreviewAsync(new(), DateTimeOffset.UtcNow, default))).Code);
        Assert.Equal("Queued", (await h.Store.GetAsync(job.Value!.JobId, default))!.Status);
    }
    [Fact] public async Task Unsafe_candidate_path_and_unstopped_execution_are_never_deleted()
    {
        using var f = new Files(); var candidate = await f.Candidate(); var failed = f.Source.Value.Failures[0];
        f.Source.Value = f.Source.Value with { Failures = [failed with { Evidence = failed.Evidence with { ExecutorStopped = false } }] };
        Assert.Empty((await f.Service.PreviewAsync(new(), f.Now, default)).Items);
        f.Source.Value = f.Source.Value with { Failures = [failed with { Evidence = failed.Evidence with { CandidatePath = f.Workspace } }] };
        Assert.Equal("maintenance_path_not_allowed", (await Assert.ThrowsAsync<ProjectEditException>(() => f.Service.PreviewAsync(new(), f.Now, default))).Code);
        Assert.True(Directory.Exists(candidate));
    }
    [Fact] public async Task Linked_file_and_tampered_plan_are_rejected()
    {
        using var f = new Files(); var id = await f.Upload(); var plan = await f.Service.PreviewAsync(new(), f.Now, default);
        Assert.Equal("maintenance_plan_invalid", (await Assert.ThrowsAsync<ProjectEditException>(() => f.Service.ApplyAsync(plan with { WorkspaceRoot = f.Data }, f.Now, default))).Code);
        Assert.Equal("maintenance_plan_expired", (await Assert.ThrowsAsync<ProjectEditException>(() => f.Service.ApplyAsync(plan, f.Now.AddDays(2), default))).Code);
        if (OperatingSystem.IsWindows()) return; // Symlink creation needs extra rights on Windows; ownership checks run on every OS.
        var part = Path.Combine(f.Data, "uploads", id + ".part"); File.Delete(part);
        var outside = Path.Combine(f.Root, "outside"); await File.WriteAllTextAsync(outside, "must survive"); File.CreateSymbolicLink(part, outside);
        Assert.Equal("maintenance_link_rejected", (await Assert.ThrowsAsync<ProjectEditException>(() => f.Service.PreviewAsync(new(), f.Now, default))).Code);
        Assert.True(File.Exists(outside));
    }
}
