using System.Text.Json;
using System.Text.Json.Nodes;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Exchange;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.EngineeringService.Exchange;

/// <summary>Runs inside the candidate coordinator. Graph proofs must pass before a version can publish.</summary>
public sealed class ExchangeExecutor(IEngineeringExecutor worker, IExchangeArtifacts artifacts) : IEngineeringExecutor
{
    public string ExecutionMode => worker.ExecutionMode;
    public OperationError? Unavailability => worker.Unavailability;
    public OperationError? CheckAvailability(string operation) => worker.CheckAvailability(operation);
    public ValidationEvidence? Validation => worker.Validation;
    public async Task<WorkerResponse> ExecuteAsync(WorkerRequest request, CancellationToken token)
    {
        if (!ExchangeOperations.IsRead(request.Operation) && !ExchangeOperations.IsWrite(request.Operation)) return await worker.ExecuteAsync(request, token);
        JsonElement? evidence = null;
        try
        {
            var input = request.Payload.Deserialize<EngineeringInput>(WorkerJson.Options)!;
            var graphWrite = request.Operation == ExchangeOperations.WriteGraph;
            var expected = graphWrite ? PlcOpenGraphCodec.Decode(input.Edit!.Exchange!.Xml) : null;
            if (graphWrite && input.Edit!.ObjectId is { } id)
            {
                var probe = request with { RequestId = Guid.NewGuid(), Operation = ExchangeOperations.ReadGraph,
                    Payload = JsonSerializer.SerializeToElement(new EngineeringInput(input.ProjectPath, ObjectId: id, IdeProfile: input.IdeProfile), WorkerJson.Options) };
                var before = await worker.ExecuteAsync(probe, token);
                if (!WorkerJson.Matches(probe, before) || !WorkerResultValidation.IsValid(probe, before)) return WorkerJson.Unknown(request, "worker_protocol_error");
                if (before.Outcome != "succeeded") return before with { RequestId = request.RequestId };
                evidence = before.Payload;
                var observed = PlcOpenGraphCodec.Decode(before.Payload!.Value.GetProperty("exchange").GetProperty("xml").GetString()!);
                if (observed.SemanticSha256 != input.Edit.Exchange!.ExpectedGraphSha256) return WorkerJson.Failed(request, "graph_revision_conflict");
                if (observed.Program.Name != expected!.Program.Name || observed.Program.PouType != expected.Program.PouType || observed.Program.Language != expected.Program.Language)
                    return WorkerJson.Failed(request, "graph_identity_change_requires_explicit_object_operation");
            }
            var response = await worker.ExecuteAsync(request, token);
            if (!WorkerJson.Matches(request, response) || !WorkerResultValidation.IsValid(request, response)) return WorkerJson.Unknown(request, "worker_protocol_error");
            if (response.Outcome != "succeeded") return response;
            evidence = response.Payload;
            var payload = JsonNode.Parse(response.Payload!.Value.GetRawText())!.AsObject();
            var exchange = payload["exchange"]!.AsObject(); var xml = exchange["xml"]!.GetValue<string>(); var format = exchange["format"]!.GetValue<string>();
            _ = ExchangeXml.Inspect(format, xml);
            if (graphWrite || request.Operation == ExchangeOperations.ReadGraph)
            {
                var graph = PlcOpenGraphCodec.Decode(xml);
                if (expected is not null && graph.SemanticSha256 != expected.SemanticSha256)
                    return WorkerJson.Failed(request, "graph_semantic_readback_mismatch") with { Payload = evidence };
                exchange["graph"] = JsonSerializer.SerializeToNode(graph, WorkerJson.Options);
            }
            exchange["artifact"] = JsonSerializer.SerializeToNode(await artifacts.PutAsync(format, xml, token), WorkerJson.Options);
            exchange.Remove("xml");
            return response with { Payload = JsonSerializer.SerializeToElement(payload, WorkerJson.Options) };
        }
        catch (ProjectEditException error) { return WorkerJson.Failed(request, error.Code) with { Payload = evidence }; }
        catch (IOException) { return WorkerJson.Failed(request, "exchange_artifact_failed") with { Payload = evidence }; }
    }
}
