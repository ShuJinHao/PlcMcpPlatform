using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlcMcp.EngineeringService.Batch;
using PlcMcp.EngineeringService.Delivery;
using PlcMcp.EngineeringService.Editing;
using PlcMcp.EngineeringService.Exchange;
using PlcMcp.EngineeringService.Execution;
using PlcMcp.Execution;
using PlcMcp.Services.Contracts.Batch;
using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Delivery;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Exchange;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;
using PlcMcp.TestHost;
using Xunit;

namespace PlcMcp.Tests;

public sealed class DeliveryTests
{
    private static BatchPlan Plan(string app, string implementation = "counter := counter + 1;") => new(app,
    [
        new("state", EditOperations.CreateDut, new(ParentId: app, Name: "MachineState", Declaration: "TYPE MachineState : (Idle, Running); END_TYPE", DutType: "Enumeration")),
        new("globals", EditOperations.CreateGvl, new(ParentId: app, Name: "GVL", Declaration: "VAR_GLOBAL\nEnabled : BOOL;\nEND_VAR")),
        new("program", EditOperations.CreatePou, new(ParentId: app, Name: "Main", PouType: "Program", Language: "ST", Declaration: "PROGRAM Main\nVAR\ncounter : INT;\nEND_VAR", Implementation: implementation)),
        new("configuration", ConfigurationOperations.TaskConfigurationCreate, new(ParentId: app)),
        new("task", ConfigurationOperations.TaskCreate, new(ParentId: "@configuration", Name: "MainTask", Configuration: new(Task: new("Cyclic", "1", "20", "ms")))),
        new("calls", ConfigurationOperations.TaskCallsSet, new(ObjectId: "@task", Configuration: new(Calls: [new("@program", "Main")])))
    ]);
    private static (IEngineeringExecutor Executor, BatchService Service, ManagedEditingJobs Edits, BatchJournalStore Journals, DeliveryArtifacts Packages) Compose(ManagedEditingTests.Harness h, IEngineeringExecutor? raw = null)
    {
        var artifacts = new ExchangeArtifacts(Path.Combine(h.Root, "exchange")); var journal = new BatchJournalStore(Path.Combine(h.Root, "batches"));
        var packages = new DeliveryArtifacts(Path.Combine(h.Root, "packages"));
        var managed = new ManagedProjectExecutor(new BatchExecutor(new ExchangeExecutor(raw ?? new FixtureExecutor(Path.Combine(h.Root, "batch-calls")), artifacts), journal), h.Files, h.Changes);
        var executor = new DeliveryExecutor(managed, h.Files, h.Changes, packages);
        var edits = new ManagedEditingJobs(h.Store, h.Changes, h.Files, executor);
        return (executor, new(edits, artifacts), edits, journal, packages);
    }
    [Fact] public async Task Batch_publishes_once_resolves_identities_and_packages_the_exact_revision()
    {
        await using var h = await ManagedEditingTests.Harness.Create();
        var created = await h.Complete(EditOperations.CreateProject, new("demo", TemplateId: "sample"));
        var revision = created.Result!.Value.GetProperty("revision").GetString()!; var app = h.Fixture.ApplicationId;
        var c = Compose(h); var plan = Plan(app); var preview = await c.Service.PreviewAsync("demo", revision, plan, default);
        var receipt = await c.Service.ApplyAsync("demo", revision, plan, preview.PlanSha256, "batch", 600, default);
        Assert.True(receipt.IsSuccess, receipt.Error?.Code);
        var dispatcher = new JobDispatcher(h.Store, c.Executor); await dispatcher.RunNextAsync(default);
        var result = (await h.Store.GetAsync(receipt.Value!.JobId, default))!;
        Assert.True(result.Status == "Succeeded", result.ErrorCode + ": " + result.Result);
        var batch = result.Result!.Value.GetProperty("engineeringResult").GetProperty("batch");
        Assert.Equal(6, batch.GetProperty("steps").GetArrayLength());
        var calls = batch.GetProperty("tasks").GetProperty("configuration").GetProperty("data").GetProperty("tasks")[0].GetProperty("calls");
        Assert.Equal(batch.GetProperty("aliases").GetProperty("program").GetString(), calls[0].GetProperty("pouId").GetString());
        Assert.Equal(2, (await h.ChangesList()).Count(x => x.Stage == "published"));
        Assert.Equal("verified", (await c.Journals.ReadAsync(result.JobId, default))!.State);
        Assert.Equal(result.JobId, (await c.Service.ApplyAsync("demo", revision, plan, preview.PlanSha256, "batch", 600, default)).Value!.JobId);

        var head = (await c.Edits.GetWorkspaceAsync("demo", default)).Value!;
        var before = await File.ReadAllBytesAsync(head.ProjectPath);
        var delivery = new DeliveryService(h.Store, h.Files, h.Changes, c.Executor);
        var packageJob = await delivery.CreateAsync("demo", head.Revision, app, "package", 600, default);
        Assert.True(packageJob.IsSuccess, packageJob.Error?.Code); await dispatcher.RunNextAsync(default);
        var packaged = (await h.Store.GetAsync(packageJob.Value!.JobId, default))!;
        Assert.True(packaged.Status == "Succeeded", packaged.ErrorCode + ": " + packaged.Result);
        var artifact = packaged.Result!.Value.GetProperty("artifact").Deserialize<DeliveryArtifact>(WorkerJson.Options)!;
        using var download = new MemoryStream(); long offset = 0;
        do
        {
            var chunk = await c.Packages.ReadAsync(artifact.ArtifactId, offset, 512, default); var bytes = Convert.FromBase64String(chunk.Base64);
            Assert.Equal(chunk.ChunkSha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            download.Write(bytes); offset = chunk.NextOffset;
        } while (offset < artifact.ByteLength);
        Assert.Equal(artifact.Sha256, Convert.ToHexString(SHA256.HashData(download.ToArray())).ToLowerInvariant());
        download.Position = 0; using var zip = new ZipArchive(download, ZipArchiveMode.Read);
        using var manifestStream = zip.GetEntry("manifest.json")!.Open();
        var manifest = await JsonSerializer.DeserializeAsync<DeliveryManifest>(manifestStream, WorkerJson.Options);
        Assert.Equal(head.Revision, manifest!.Revision); Assert.Equal("simulated", manifest.EvidenceKind); Assert.False(manifest.EngineeringReady);
        Assert.NotNull(zip.GetEntry("reports/tasks.json")); Assert.NotNull(zip.GetEntry("reports/compile.json"));
        foreach (var stamp in manifest.Files)
        {
            using var source = zip.GetEntry("project/" + stamp.Path)!.Open();
            Assert.Equal(stamp.Sha256, Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant());
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(head.ProjectPath));
        Assert.Equal(head.Revision, (await c.Edits.GetWorkspaceAsync("demo", default)).Value!.Revision);
        Assert.Equal(packaged.JobId, (await delivery.CreateAsync("demo", head.Revision, app, "package", 600, default)).Value!.JobId);
    }
    [Theory]
    [InlineData("duplicate", "object_name_conflict", 1)]
    [InlineData("compile", "compile_failed", 6)]
    [InlineData("scope", "batch_application_scope_mismatch", 0)]
    public async Task Failed_batch_preserves_the_published_head_and_completed_step_evidence(string kind, string error, int completed)
    {
        await using var h = await ManagedEditingTests.Harness.Create();
        var initial = await h.Complete(EditOperations.CreateProject, new("demo", TemplateId: "sample"));
        var revision = initial.Result!.Value.GetProperty("revision").GetString()!;
        var c = Compose(h); var plan = Plan(h.Fixture.ApplicationId, kind == "compile" ? "FIXTURE_ERROR" : "counter := 1;");
        if (kind == "duplicate") plan = plan with { Steps = [plan.Steps[0], plan.Steps[0] with { Id = "duplicate" }] };
        if (kind == "scope") plan = plan with { Steps = [plan.Steps[0] with { Change = plan.Steps[0].Change with { ParentId = FixtureConfiguration.ControllerId } }] };
        var preview = await c.Service.PreviewAsync("demo", revision, plan, default);
        var receipt = await c.Service.ApplyAsync("demo", revision, plan, preview.PlanSha256, "failure", 600, default);
        Assert.True(receipt.IsSuccess, receipt.Error?.Code); await new JobDispatcher(h.Store, c.Executor).RunNextAsync(default);
        var job = (await h.Store.GetAsync(receipt.Value!.JobId, default))!;
        Assert.Equal("Failed", job.Status); Assert.Equal(error, job.ErrorCode);
        Assert.Equal(revision, (await c.Edits.GetWorkspaceAsync("demo", default)).Value!.Revision);
        Assert.Equal(completed, (await c.Journals.ReadAsync(job.JobId, default))!.Steps.Count(s => s.Status == "succeeded"));
        Assert.False((await h.Store.GetSlotAsync(default)).Blocked);
    }
    [Fact] public async Task Unknown_batch_stops_before_later_steps_and_is_not_replayed()
    {
        await using var h = await ManagedEditingTests.Harness.Create();
        var initial = await h.Complete(EditOperations.CreateProject, new("demo", TemplateId: "sample"));
        var revision = initial.Result!.Value.GetProperty("revision").GetString()!;
        var raw = new InterruptingWorker(new FixtureExecutor(Path.Combine(h.Root, "fault-calls")));
        var c = Compose(h, raw); var plan = Plan(h.Fixture.ApplicationId); var preview = await c.Service.PreviewAsync("demo", revision, plan, default);
        var receipt = await c.Service.ApplyAsync("demo", revision, plan, preview.PlanSha256, "unknown", 600, default);
        await new JobDispatcher(h.Store, c.Executor).RunNextAsync(default);
        var job = (await h.Store.GetAsync(receipt.Value!.JobId, default))!;
        Assert.Equal("OutcomeUnknown", job.Status); Assert.True((await h.Store.GetSlotAsync(default)).Blocked);
        Assert.DoesNotContain(ConfigurationOperations.TaskCreate, raw.Calls);
        Assert.Equal("unknown", (await c.Journals.ReadAsync(job.JobId, default))!.State);
        Assert.Equal(job.JobId, (await c.Service.ApplyAsync("demo", revision, plan, preview.PlanSha256, "unknown", 600, default)).Value!.JobId);
        Assert.Equal(revision, (await c.Edits.GetWorkspaceAsync("demo", default)).Value!.Revision);
    }
    [Fact] public void Batch_rejects_future_references_and_duplicate_step_ids()
    {
        var app = Guid.NewGuid().ToString(); var plan = Plan(app);
        Assert.Equal("batch_reference_not_ready", BatchValidation.Validate("demo", plan with { Steps = [plan.Steps[^1], .. plan.Steps[..^1]] }));
        Assert.Equal("invalid_batch_step", BatchValidation.Validate("demo", plan with { Steps = [plan.Steps[0], plan.Steps[0]] }));
        Assert.NotEqual(BatchValidation.Hash("demo", "v1", plan), BatchValidation.Hash("demo", "v2", plan));
    }
    [Fact] public async Task Upload_resumes_after_restart_checks_offsets_and_deduplicates_chunks()
    {
        var root = Path.Combine(Path.GetTempPath(), "plc-upload-" + Guid.NewGuid().ToString("N"));
        try
        {
            var artifacts = new ExchangeArtifacts(Path.Combine(root, "artifacts"));
            async Task<ExchangeArtifact> Accept(string f, string xml, CancellationToken t) { _ = ExchangeXml.Inspect(f, xml); return await artifacts.PutAsync(f, xml, t); }
            var store = new ExchangeUploads(Path.Combine(root, "uploads"), Accept);
            var bytes = Encoding.UTF8.GetBytes("\uFEFF<Native><content>" + new string('测', 50000) + "</content></Native>");
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var start = await store.BeginAsync("large", "codesys-native", bytes.Length, sha, default);
            Assert.Equal("upload_incomplete", (await Assert.ThrowsAsync<ProjectEditException>(() => store.CompleteAsync(start.UploadId, default))).Code);
            var part = Convert.ToBase64String(bytes[..32768]);
            var first = await store.AppendAsync(start.UploadId, 0, part, default);
            Assert.Equal(first.NextOffset, (await store.AppendAsync(start.UploadId, 0, part, default)).NextOffset);
            Assert.Equal("upload_offset_conflict", (await Assert.ThrowsAsync<ProjectEditException>(() => store.AppendAsync(start.UploadId, 32769, part, default))).Code);
            var restarted = new ExchangeUploads(Path.Combine(root, "uploads"), Accept);
            Assert.Equal(32768, (await restarted.BeginAsync("large", "codesys-native", bytes.Length, sha, default)).NextOffset);
            for (var offset = 32768; offset < bytes.Length; offset += 32768)
                await restarted.AppendAsync(start.UploadId, offset, Convert.ToBase64String(bytes[offset..Math.Min(offset + 32768, bytes.Length)]), default);
            var completed = await restarted.CompleteAsync(start.UploadId, default);
            Assert.Equal(bytes.Length, completed.Artifact!.ByteLength); Assert.Equal(sha, completed.Artifact.Sha256);
            Assert.Equal(completed, await restarted.CompleteAsync(start.UploadId, default));
            await restarted.AbortAsync(start.UploadId, default);
            Assert.Equal(Encoding.UTF8.GetString(bytes), (await artifacts.GetAsync(completed.Artifact.ArtifactId, default)).Xml);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("hash", "upload_hash_mismatch")]
    [InlineData("xml", "invalid_exchange_xml")]
    [InlineData("utf8", "upload_requires_utf8")]
    public async Task Invalid_upload_never_becomes_an_artifact(string kind, string error)
    {
        var root = Path.Combine(Path.GetTempPath(), "plc-upload-" + Guid.NewGuid().ToString("N")); var accepted = false;
        try
        {
            var bytes = kind == "utf8" ? new byte[] { 0xff } : Encoding.UTF8.GetBytes("<!DOCTYPE n [<!ENTITY x 'bad'>]><n>&x;</n>");
            var hash = kind == "hash" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var store = new ExchangeUploads(root, (format, xml, token) => { _ = ExchangeXml.Inspect(format, xml); accepted = true; throw new InvalidOperationException(); });
            var session = await store.BeginAsync("invalid", "codesys-native", bytes.Length, hash, default);
            await store.AppendAsync(session.UploadId, 0, Convert.ToBase64String(bytes), default);
            Assert.Equal(error, (await Assert.ThrowsAsync<ProjectEditException>(() => store.CompleteAsync(session.UploadId, default))).Code);
            Assert.False(accepted);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact] public async Task Worker_exchange_files_roundtrip_and_reject_substituted_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "plc-transfer-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var xml = "<Native>" + new string('x', 100000) + "</Native>";
            var request = new WorkerRequest(1, Guid.NewGuid(), Guid.NewGuid(), "project.import", Guid.NewGuid(), "revision", DateTimeOffset.UtcNow.AddMinutes(1), "fixture",
                JsonSerializer.SerializeToElement(new EngineeringInput("unused", Edit: new("demo", Exchange: new("artifact", "codesys-native", xml)), Candidate: true), WorkerJson.Options));
            var transmitted = await WorkerExchangeFiles.PrepareRequestAsync(request, root, 200000, default);
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(transmitted, WorkerJson.Options).Length < 4096);
            var hydrated = await WorkerExchangeFiles.ReadRequestAsync(transmitted, root, 200000, default);
            Assert.Equal(xml, hydrated.Payload.GetProperty("edit").GetProperty("exchange").GetProperty("xml").GetString());
            await File.AppendAllTextAsync(Path.Combine(root, "exchange-input.xml"), "corrupt");
            Assert.Equal("invalid_exchange_file", (await Assert.ThrowsAsync<ProjectEditException>(() => WorkerExchangeFiles.ReadRequestAsync(transmitted, root, 200000, default))).Code);
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class InterruptingWorker(IEngineeringExecutor inner) : IEngineeringExecutor
    {
        public List<string> Calls { get; } = [];
        public string ExecutionMode => inner.ExecutionMode;
        public OperationError? Unavailability => null;
        public Task<WorkerResponse> ExecuteAsync(WorkerRequest request, CancellationToken token)
        { Calls.Add(request.Operation); return request.Operation == EditOperations.CreatePou ? Task.FromResult(WorkerJson.Unknown(request, "worker_exited_without_result")) : inner.ExecuteAsync(request, token); }
    }

    [Fact] public async Task Large_failure_evidence_is_preserved_as_an_artifact_without_inlining_xml()
    {
        var root = Path.Combine(Path.GetTempPath(), "plc-failed-exchange-" + Guid.NewGuid().ToString("N"));
        try
        {
            var xml = "<Native>" + new string('x', 100000) + "</Native>";
            var artifacts = new ExchangeArtifacts(root);
            var executor = new ExchangeExecutor(new FailedExchangeWorker(xml), artifacts);
            var request = new WorkerRequest(1, Guid.NewGuid(), Guid.NewGuid(), ExchangeOperations.Export, Guid.NewGuid(), new string('a', 64),
                DateTimeOffset.UtcNow.AddMinutes(1), "fixture", JsonSerializer.SerializeToElement(new EngineeringInput("unused"), WorkerJson.Options));
            var result = await executor.ExecuteAsync(request, default);
            Assert.Equal("failed", result.Outcome); Assert.Equal("exchange_reopen_mismatch", result.ErrorCode);
            var evidence = result.Payload!.Value.GetProperty("exchange");
            Assert.False(evidence.TryGetProperty("xml", out _));
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(result, WorkerJson.Options).Length < 4096);
            var stored = await artifacts.GetAsync(evidence.GetProperty("artifact").GetProperty("artifactId").GetString()!, default);
            Assert.Equal(xml, stored.Xml);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class FailedExchangeWorker(string xml) : IEngineeringExecutor
    {
        public string ExecutionMode => "fixture";
        public OperationError? Unavailability => null;
        public Task<WorkerResponse> ExecuteAsync(WorkerRequest request, CancellationToken token) => Task.FromResult(
            WorkerJson.Failed(request, "exchange_reopen_mismatch") with { Payload = JsonSerializer.SerializeToElement(new { exchange = new { format = "codesys-native", xml, report = Array.Empty<string>() } }, WorkerJson.Options) });
    }
}
