using System.Text.Json;
using PlcMcp.EngineeringService.Exchange;
using PlcMcp.Inovance;
using PlcMcp.Services.Contracts.Batch;
using PlcMcp.Services.Contracts.Exchange;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;
using Xunit;

namespace PlcMcp.Tests;

public sealed class GraphPreflightTests
{
    [Fact] public async Task A_late_invalid_graph_blocks_the_entire_batch_before_candidate_execution()
    {
        var artifacts = new MemoryArtifacts(); var worker = new RecordingExecutor();
        var good = await artifacts.PutAsync("plcopen", PlcOpenGraphCodec.Encode(GraphDeclarationTests.Sample()), default);
        var p = GraphDeclarationTests.Sample();
        var invalid = p with { Variables = p.Variables.Select(v => v.Name == "Count" ? v with { Address = "%MD0" } : v).ToArray() };
        var bad = await artifacts.PutAsync("plcopen", PlcOpenGraphCodec.Encode(invalid), default);
        var app = Guid.NewGuid().ToString();
        var plan = new BatchPlan(app, [new("first", ExchangeOperations.WriteGraph, new(ParentId: app, Name: p.Name, ArtifactId: good.ArtifactId)),
            new("last", ExchangeOperations.WriteGraph, new(ParentId: app, Name: p.Name, ArtifactId: bad.ArtifactId))]);
        var input = new EngineeringInput("x", Edit: new("w", Batch: plan));
        var executor = new GraphPolicyExecutor(worker, artifacts, new InovanceGraphPolicy());
        Assert.Equal("inovance_retain_address_conflict", (await executor.CheckRequestAsync(BatchOperations.Apply, input, default))!.Code);
        Assert.Equal("inovance_retain_address_conflict", (await executor.ExecuteAsync(Request(BatchOperations.Apply, input), default)).ErrorCode);
        Assert.Equal(0, worker.Calls); Assert.Equal(4, artifacts.Reads);
    }
    [Fact] public async Task Recovered_execution_rechecks_artifacts_and_does_not_trust_admission()
    {
        var artifacts = new MemoryArtifacts(); var worker = new RecordingExecutor();
        var good = await artifacts.PutAsync("plcopen", PlcOpenGraphCodec.Encode(GraphDeclarationTests.Sample()), default);
        var input = new EngineeringInput("x", Edit: new("w", Exchange: new(good.ArtifactId, "plcopen")));
        var executor = new GraphPolicyExecutor(worker, artifacts, new InovanceGraphPolicy());
        Assert.Null(await executor.CheckRequestAsync(ExchangeOperations.WriteGraph, input, default));
        artifacts.Unavailable = true;
        Assert.Equal("artifact_unavailable", (await executor.ExecuteAsync(Request(ExchangeOperations.WriteGraph, input), default)).ErrorCode);
        Assert.Equal(0, worker.Calls); Assert.Equal(2, artifacts.Reads);
    }
    [Fact] public async Task Valid_graph_reaches_worker_but_conflicting_inline_content_and_malformed_jobs_do_not()
    {
        var artifacts = new MemoryArtifacts(); var worker = new RecordingExecutor();
        var good = await artifacts.PutAsync("plcopen", PlcOpenGraphCodec.Encode(GraphDeclarationTests.Sample()), default);
        var input = new EngineeringInput("x", Edit: new("w", Exchange: new(good.ArtifactId, "plcopen")));
        var executor = new GraphPolicyExecutor(worker, artifacts, new InovanceGraphPolicy());
        await executor.ExecuteAsync(Request(ExchangeOperations.WriteGraph, input), default); Assert.Equal(1, worker.Calls);
        var conflicting = input with { Edit = input.Edit! with { Exchange = input.Edit!.Exchange! with { Xml = "<different/>" } } };
        Assert.Equal("artifact_integrity_mismatch", (await executor.ExecuteAsync(Request(ExchangeOperations.WriteGraph, conflicting), default)).ErrorCode);
        Assert.Equal("invalid_graph_request", (await executor.ExecuteAsync(Request(ExchangeOperations.WriteGraph, input) with { Payload = JsonSerializer.SerializeToElement("bad") }, default)).ErrorCode);
        Assert.Equal(1, worker.Calls);
    }
    private static WorkerRequest Request(string operation, EngineeringInput input) => new(1, Guid.NewGuid(), Guid.NewGuid(), operation,
        Guid.NewGuid(), "rev", DateTimeOffset.UtcNow.AddMinutes(1), "fixture", JsonSerializer.SerializeToElement(input, WorkerJson.Options));
    private sealed class RecordingExecutor : IEngineeringExecutor
    {
        public int Calls; public string ExecutionMode => "fixture"; public OperationError? Unavailability => null;
        public Task<WorkerResponse> ExecuteAsync(WorkerRequest request, CancellationToken token)
        { Calls++; return Task.FromResult(WorkerJson.Failed(request, "recorded_execution")); }
    }
    private sealed class MemoryArtifacts : IExchangeArtifacts
    {
        private readonly Dictionary<string, ExchangeDocument> documents = new(); public bool Unavailable; public int Reads;
        public Task<ExchangeArtifact> PutAsync(string format, string xml, CancellationToken token)
        {
            var id = new string((char)('a' + documents.Count), 64); var artifact = new ExchangeArtifact(id, id, format, System.Text.Encoding.UTF8.GetByteCount(xml));
            documents.Add(id, new(artifact, xml)); return Task.FromResult(artifact);
        }
        public Task<ExchangeDocument> GetAsync(string id, CancellationToken token)
        { Reads++; if (Unavailable) throw new IOException("unavailable"); return Task.FromResult(documents[id]); }
    }
}
