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
    public RuntimeReadiness? Readiness => worker.Readiness;
    public Task<OperationError?> CheckRequestAsync(string operation, EngineeringInput input, CancellationToken token) => worker.CheckRequestAsync(operation, input, token);
    public async Task<WorkerResponse> ExecuteAsync(WorkerRequest request, CancellationToken token)
    {
        if (!ExchangeOperations.IsRead(request.Operation) && !ExchangeOperations.IsWrite(request.Operation)) return await worker.ExecuteAsync(request, token);
        JsonElement? evidence = null;
        try
        {
            var input = request.Payload.Deserialize<EngineeringInput>(WorkerJson.Options)!;
            if (input.Edit?.Exchange is { } change)
            {
                var stored = await artifacts.GetAsync(change.ArtifactId, token);
                if (stored.Artifact.Format != change.Format || change.Xml is not null && change.Xml != stored.Xml)
                    throw new ProjectEditException("artifact_integrity_mismatch");
                input = input with { Edit = input.Edit with { Exchange = change with { Xml = stored.Xml } } };
                if (ExchangeValidation.Write(request.Operation, input.Edit) is { } invalid) throw new ProjectEditException(invalid);
                request = request with { Payload = JsonSerializer.SerializeToElement(input, WorkerJson.Options) };
            }
            var graphWrite = request.Operation == ExchangeOperations.WriteGraph;
            var expected = graphWrite ? PlcOpenGraphCodec.Decode(input.Edit!.Exchange!.Xml!) : null;
            if (graphWrite && input.Edit!.ObjectId is { } id)
            {
                var probe = request with { RequestId = Guid.NewGuid(), Operation = ExchangeOperations.ReadGraph,
                    Payload = JsonSerializer.SerializeToElement(new EngineeringInput(input.ProjectPath, ObjectId: id, IdeProfile: input.IdeProfile), WorkerJson.Options) };
                var before = await worker.ExecuteAsync(probe, token);
                if (!WorkerJson.Matches(probe, before) || !WorkerResultValidation.IsValid(probe, before)) return WorkerJson.Unknown(request, "worker_protocol_error");
                if (before.Outcome != "succeeded") return before with { RequestId = request.RequestId, Payload = await CompactEvidence(before.Payload, token) };
                evidence = before.Payload;
                var observed = PlcOpenGraphCodec.Decode(before.Payload!.Value.GetProperty("exchange").GetProperty("xml").GetString()!);
                if (observed.SemanticSha256 != input.Edit.Exchange!.ExpectedGraphSha256) return WorkerJson.Failed(request, "graph_revision_conflict");
                if (observed.Program.Name != expected!.Program.Name || observed.Program.PouType != expected.Program.PouType || observed.Program.Language != expected.Program.Language)
                    return WorkerJson.Failed(request, "graph_identity_change_requires_explicit_object_operation");
            }
            var response = await worker.ExecuteAsync(request, token);
            if (!WorkerJson.Matches(request, response) || !WorkerResultValidation.IsValid(request, response)) return WorkerJson.Unknown(request, "worker_protocol_error");
            if (response.Outcome != "succeeded") return response with { Payload = await CompactEvidence(response.Payload, token) };
            evidence = response.Payload;
            var payload = JsonNode.Parse(response.Payload!.Value.GetRawText())!.AsObject();
            var exchange = payload["exchange"]!.AsObject(); var xml = exchange["xml"]!.GetValue<string>(); var format = exchange["format"]!.GetValue<string>();
            _ = ExchangeXml.Inspect(format, xml);
            if (graphWrite || request.Operation == ExchangeOperations.ReadGraph)
            {
                var graph = PlcOpenGraphCodec.Decode(xml);
                if (expected is not null && graph.SemanticSha256 != expected.SemanticSha256)
                    return WorkerJson.Failed(request, "graph_semantic_readback_mismatch") with { Payload = await CompactEvidence(evidence, token) };
                exchange["graph"] = JsonSerializer.SerializeToNode(graph, WorkerJson.Options);
            }
            exchange["artifact"] = JsonSerializer.SerializeToNode(await artifacts.PutAsync(format, xml, token), WorkerJson.Options);
            exchange.Remove("xml");
            return response with { Payload = JsonSerializer.SerializeToElement(payload, WorkerJson.Options) };
        }
        catch (ProjectEditException error) { return WorkerJson.Failed(request, error.Code) with { Payload = await CompactEvidence(evidence, token) }; }
        catch (IOException) { return WorkerJson.Failed(request, "exchange_artifact_failed") with { Payload = await CompactEvidence(evidence, token) }; }
    }

    // Failure reports must not copy multi-megabyte XML into job rows or MCP replies.
    // Preserve the bytes as an artifact when possible, otherwise retain an explicit digest/error.
    private async Task<JsonElement?> CompactEvidence(JsonElement? data, CancellationToken token)
    {
        if (data is not { ValueKind: JsonValueKind.Object } value) return data;
        var node = JsonNode.Parse(value.GetRawText())!.AsObject();
        if (node["exchange"] is not JsonObject exchange || exchange["xml"] is not JsonValue xmlValue || !xmlValue.TryGetValue<string>(out var xml)) return data;
        var bytes = System.Text.Encoding.UTF8.GetBytes(xml);
        exchange["xmlSha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        exchange["xmlByteLength"] = bytes.Length;
        try { exchange["artifact"] = JsonSerializer.SerializeToNode(await artifacts.PutAsync(exchange["format"]!.GetValue<string>(), xml, token), WorkerJson.Options); }
        catch (Exception error) when (error is ProjectEditException or IOException or UnauthorizedAccessException)
        { exchange["xmlArtifactError"] = error is ProjectEditException known ? known.Code : "exchange_artifact_failed"; }
        exchange.Remove("xml");
        return JsonSerializer.SerializeToElement(node, WorkerJson.Options);
    }
}
