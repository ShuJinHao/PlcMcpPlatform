using System.Text.Json;
using PlcMcp.Inovance;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.EngineeringService.Readiness;
using PlcMcp.Services.Contracts.Batch;
using PlcMcp.SharedKernel.Results;
using PlcMcp.McpHost.Tools;
using Xunit;

namespace PlcMcp.Tests;

// Synthetic evidence exercises policy mechanics only; it is never shipped as real acceptance.
public sealed class AcceptanceTests
{
    [Fact] public void Missing_manifest_keeps_local_tools_and_disables_IDE()
    {
        var policy = new InovanceAcceptance(null, () => throw new Exception("must not observe"));
        Assert.Equal("unverified", policy.GetReadiness().State);
        Assert.Equal("acceptance_required", policy.CheckOperation("pou.create")!.Code);
        Assert.Equal(22, ToolAccessCatalog.Operations.Count(x => x.Value is null));
        Assert.Equal(74, ToolAccessCatalog.Operations.Count);
        var methods = typeof(PlatformTools).Assembly.GetTypes().SelectMany(t => t.GetMethods())
            .Select(m => m.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false).Cast<ModelContextProtocol.Server.McpServerToolAttribute>().SingleOrDefault())
            .Where(a => a is not null).Select(a => a!.Name).Order(StringComparer.Ordinal);
        Assert.Equal(ToolAccessCatalog.Operations.Keys.Order(StringComparer.Ordinal), methods);
    }
    [Fact] public void Partial_review_allows_exact_conditions_only()
    {
        using var set = new EvidenceSet(); var policy = set.Policy();
        Assert.Equal("accepted", policy.GetReadiness().State);
        Assert.Null(policy.CheckInput("pou.create", Element(new EngineeringInput("x", Edit: new("w", Language: "ST", PouType: "Program")))));
        Assert.Equal("operation_scope_not_accepted", policy.CheckInput("pou.create", Element(new EngineeringInput("x", Edit: new("w", Language: "LD", PouType: "Program"))))!.Code);
        Assert.Equal("operation_scope_not_accepted", policy.CheckInput("pou.create", Element(new EngineeringInput("x", Edit: new("w", Language: "ST", PouType: "Function"))))!.Code);
        Assert.Equal("operation_not_accepted", policy.CheckOperation("pou.update")!.Code);
        Assert.Equal("dependency_scope_not_accepted", policy.CheckOperation("project.batch")!.Code);
    }
    [Theory] [InlineData("engine")] [InlineData("executable")] [InlineData("scripts")] [InlineData("profile")] [InlineData("executor")]
    public void Environment_change_revokes_acceptance(string changed)
    {
        using var set = new EvidenceSet(); var original = set.Identity; var current = original;
        var policy = new InovanceAcceptance(set.Path, () => current);
        Assert.Equal("accepted", policy.GetReadiness().State);
        current = changed switch {
            "engine" => original with { Engine = original.Engine with { Sha256 = new('a', 64) } },
            "executable" => original with { Executable = original.Executable with { Sha256 = new('a', 64) } },
            "scripts" => original with { ScriptSha256 = new('a', 64) },
            "profile" => original with { Profile = "other" },
            _ => original with { ExecutorSha256 = new('a', 64) }
        };
        Assert.Equal("acceptance_identity_mismatch", policy.CheckOperation("pou.create")!.Code);
    }
    [Theory] [InlineData("fixture")] [InlineData("simulated")] [InlineData("failed")] [InlineData("runtime")] [InlineData("conditions")] [InlineData("evidence_hash")]
    public void Invalid_evidence_never_promotes_capability(string change)
    {
        using var set = new EvidenceSet();
        var evidence = set.Run;
        if (change == "fixture") evidence = evidence with { ExecutionMode = "fixture" };
        if (change == "simulated") evidence = evidence with { Simulated = true };
        if (change == "failed") evidence = evidence with { Response = evidence.Response with { Outcome = "failed", ErrorCode = "compile_failed" } };
        if (change == "runtime") evidence = evidence with { RuntimeIdentity = Element(new { status = "unverified" }) };
        if (change == "conditions") evidence = evidence with { Request = evidence.Request with { Payload = Element(new EngineeringInput("x", Candidate: true, Edit: new("w", Language: "FBD", PouType: "Program"))) } };
        set.Write(evidence);
        if (change == "evidence_hash") File.AppendAllText(System.IO.Path.Combine(set.Directory, "run.json"), " ");
        Assert.NotEqual("accepted", set.Policy().GetReadiness().State);
    }
    [Fact] public void Changed_manifest_requires_restart_and_unknown_condition_is_rejected()
    {
        using var set = new EvidenceSet(); var policy = set.Policy();
        Assert.Equal("accepted", policy.GetReadiness().State); File.AppendAllText(set.Path, " ");
        Assert.Equal("acceptance_changed_restart_required", policy.GetReadiness().ErrorCode);
        set.Write(set.Run, new Dictionary<string, string> { ["edit.languge"] = "ST" });
        Assert.Equal("acceptance_grant_invalid", set.Policy().GetReadiness().ErrorCode);
    }
    [Fact] public async Task Batch_denies_missing_compile_and_out_of_scope_step_before_executor()
    {
        var worker = new RecordingExecutor();
        var policy = new MemoryPolicy(["project.verify", "project.tree.read", "task.list", "pou.create"]);
        var executor = new AcceptedExecution(worker, policy);
        var plan = new BatchPlan(Guid.NewGuid().ToString(), [new("p", "pou.create", new(ParentId: Guid.NewGuid().ToString(), Language: "LD", PouType: "Program"))]);
        var input = new EngineeringInput("x", Edit: new("w", Batch: plan));
        var request = new WorkerRequest(1, Guid.NewGuid(), Guid.NewGuid(), "project.batch", Guid.NewGuid(), "rev", DateTimeOffset.UtcNow.AddMinutes(1), "real", Element(input));
        Assert.Equal("dependency_scope_not_accepted", (await executor.ExecuteAsync(request, default)).ErrorCode); Assert.Equal(0, worker.Calls);
        policy.Operations.Add("project.compile");
        Assert.Equal("operation_scope_not_accepted", (await executor.ExecuteAsync(request, default)).ErrorCode); Assert.Equal(0, worker.Calls);
        var allowed = input with { Edit = input.Edit! with { Batch = plan with { Steps = [plan.Steps[0] with { Change = plan.Steps[0].Change with { Language = "ST" } }] } } };
        Assert.Null(await executor.CheckRequestAsync("project.batch", allowed, default));
        // A queued request is checked again when the environment/grants change.
        policy.Operations.Remove("project.compile");
        Assert.Equal("dependency_scope_not_accepted", (await executor.ExecuteAsync(request with { Payload = Element(allowed) }, default)).ErrorCode);
        Assert.Equal(0, worker.Calls);
    }
    [Fact] public void Batch_without_any_accepted_step_is_not_advertised()
    {
        var executor = new AcceptedExecution(new RecordingExecutor(), new MemoryPolicy(["project.verify", "project.tree.read", "task.list", "project.compile"]));
        Assert.Equal("operation_not_accepted", executor.CheckAvailability("project.batch")!.Code);
    }
    [Fact] public async Task Graph_replacement_requires_accepted_readback_before_candidate_execution()
    {
        var worker = new RecordingExecutor(); var policy = new MemoryPolicy(["pou.graph.write"]);
        var executor = new AcceptedExecution(worker, policy);
        var input = new EngineeringInput("x", Edit: new("w", ObjectId: Guid.NewGuid().ToString()));
        Assert.Equal("operation_not_accepted", (await executor.CheckRequestAsync("pou.graph.write", input, default))!.Code);
        policy.Operations.Add("pou.graph.read");
        Assert.Null(await executor.CheckRequestAsync("pou.graph.write", input, default)); Assert.Equal(0, worker.Calls);
    }
    [Fact] public void Missing_profile_or_invalid_hash_is_not_an_identity()
    {
        using var set = new EvidenceSet();
        var record = JsonSerializer.Deserialize<InovanceAcceptanceRecord>(File.ReadAllText(set.Path), WorkerJson.Options)!;
        foreach (var identity in new[] { record.Identity with { Profile = "" }, record.Identity with { ScriptSha256 = "unverified" } })
        {
            var error = Assert.Throws<InvalidOperationException>(() => InovanceAcceptance.Validate(record with { Identity = identity }, set.Path));
            Assert.Equal("acceptance_invalid", error.Message);
        }
    }
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, WorkerJson.Options);
    private sealed class RecordingExecutor : IEngineeringExecutor
    {
        public int Calls; public string ExecutionMode => "real"; public OperationError? Unavailability => null;
        public Task<WorkerResponse> ExecuteAsync(WorkerRequest request, CancellationToken token) { Calls++; return Task.FromResult(WorkerJson.Failed(request, "unexpected_execution")); }
    }
    private sealed class MemoryPolicy(string[] operations) : IExecutionAcceptance
    {
        public HashSet<string> Operations { get; } = new(operations);
        public RuntimeReadiness GetReadiness() => new("accepted", "test", "test", Operations.Select(op => new OperationAcceptance(op, op == "pou.create" ? "constrained" : "operation",
            op == "pou.create" ? new() { ["edit.language"] = "ST" } : new(), ["test"])).ToArray(), null);
        public bool HasUnrestrictedGrant(string op) => Operations.Contains(op) && op != "pou.create";
        public OperationError? CheckOperation(string op) => Operations.Contains(op) ? null : new("operation_not_accepted", op);
        public OperationError? CheckInput(string op, JsonElement input) => op == "pou.create" && input.GetProperty("edit").GetProperty("language").GetString() != "ST" ? new("operation_scope_not_accepted", op) : null;
    }
    private sealed class EvidenceSet : IDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "plcmcp-acceptance-test-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Directory, "acceptance.json");
        public InovanceIdentity Identity { get; } = new("test-profile", "test-strategy", new("ide.exe", new('1',64), "1", "1"), new("engine.dll", new('2',64), "2", "2"), [], new('3',64), new('4',64));
        public InovanceRunEvidence Run { get; }
        public EvidenceSet()
        {
            System.IO.Directory.CreateDirectory(Directory);
            var input = new EngineeringInput("x", Edit: new("w", Name: "Main", Language: "ST", PouType: "Program", Declaration: "PROGRAM Main"), Candidate: true);
            var request = new WorkerRequest(1, Guid.NewGuid(), Guid.NewGuid(), "pou.create", Guid.NewGuid(), new('a',64), DateTimeOffset.UtcNow.AddMinutes(1), "real", Element(input));
            var runtime = Element(new { status = "matched", engineAssemblyVersion = "2.0.0.0", executableSha256 = Identity.Executable.Sha256, engineSha256 = Identity.Engine.Sha256 });
            var payload = Element(new { baseRevision = request.ExpectedRevision, revision = new string('b',64), saved = true, reopened = true, readBackVerified = true, sessionClosedAfterOperation = true,
                dependencies = new { controllerMatched = true, libraryReferencesMatched = true, installedDependenciesVerified = false }, objectId = Guid.NewGuid().ToString(), name = "Main", declaration = "PROGRAM Main", implementation = (string?)null, runtimeIdentity = runtime });
            Run = new(1, "real", false, DateTimeOffset.UtcNow.AddMinutes(-1), Identity, runtime, request, new(1, request.RequestId, request.JobId, "succeeded", null, "real", payload));
            Write(Run);
        }
        public void Write(InovanceRunEvidence run, Dictionary<string,string>? conditions = null)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(run, WorkerJson.Options); File.WriteAllBytes(System.IO.Path.Combine(Directory,"run.json"), bytes);
            var record = new InovanceAcceptanceRecord(1, "synthetic-unit-test", "unit-test", DateTimeOffset.UtcNow, "Synthetic policy test; never a real IDE acceptance.", Identity,
                [new("r", "run.json", InovanceIdentity.Hash(bytes))], [new("pou.create", "constrained", conditions ?? new() { ["edit.language"] = "ST", ["edit.pouType"] = "Program" }, ["r"])]);
            File.WriteAllBytes(Path, JsonSerializer.SerializeToUtf8Bytes(record, WorkerJson.Options));
        }
        public InovanceAcceptance Policy() => new(Path, () => Identity);
        public void Dispose() => System.IO.Directory.Delete(Directory,true);
    }
}
