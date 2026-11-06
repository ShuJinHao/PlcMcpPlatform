using System.Text.Json;
using PlcMcp.EngineeringService.Batch;
using PlcMcp.EngineeringService.Incremental;
using PlcMcp.Services.Contracts.Batch;
using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Delivery;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Exchange;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Incremental;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.EngineeringService.Readiness;

/// <summary>One production policy for discovery, admission and dispatch. Composite dependencies
/// whose actual targets are discovered by the IDE require operation-wide review before any write.</summary>
public sealed class AcceptedExecution(IEngineeringExecutor inner, IExecutionAcceptance policy) : IEngineeringExecutor
{
    public string ExecutionMode => inner.ExecutionMode;
    public ValidationEvidence? Validation => inner.Validation;
    public RuntimeReadiness? Readiness => policy.GetReadiness();
    public OperationError? Unavailability => Readiness?.State == "accepted" ? null : inner.Unavailability;
    public static string[] Dependencies(string operation) => CompositeRequirements.Dependencies(operation);
    public OperationError? CheckAvailability(string operation) => AcceptanceRules.Check(policy.GetReadiness(), operation);
    public IReadOnlyDictionary<string, OperationError?> InspectAvailability(IEnumerable<string> operations)
    {
        var state = policy.GetReadiness();
        return operations.Distinct(StringComparer.Ordinal).ToDictionary(op => op, op => AcceptanceRules.Check(state, op), StringComparer.Ordinal);
    }
    public Task<OperationError?> CheckRequestAsync(string operation, EngineeringInput input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(CheckPlan(operation, input, policy.GetReadiness()));
    }
    private static OperationError? CheckPlan(string operation, EngineeringInput input, RuntimeReadiness state)
    {
        if (AcceptanceRules.Check(state, operation, JsonSerializer.SerializeToElement(input, WorkerJson.Options)) is { } denied) return denied;
        if (operation == ExchangeOperations.WriteGraph && input.Edit?.ObjectId is { } objectId)
        {
            var probe = new EngineeringInput(input.ProjectPath, ObjectId: objectId);
            if (AcceptanceRules.Check(state, ExchangeOperations.ReadGraph, JsonSerializer.SerializeToElement(probe, WorkerJson.Options)) is { } readDenied) return readDenied;
        }
        if (input.Edit?.Batch is { } batch)
        {
            // Unknown aliases stay symbolic: exact object conditions cannot accidentally match a future GUID.
            var aliases = batch.Steps.ToDictionary(s => s.Id, s => "@" + s.Id, StringComparer.Ordinal);
            foreach (var required in BatchValidation.Required(batch))
                if (AcceptanceRules.Check(state, required) is { } missing) return missing;
            // Readback and patch helpers discover their own targets. Do not fail after earlier steps mutated the candidate.
            foreach (var required in BatchValidation.Required(batch).Where(x => !batch.Steps.Any(s => s.Operation == x) && Dependencies(x).Length == 0))
                if (!AcceptanceRules.Unrestricted(state, required)) return new("dependency_scope_not_accepted", $"Batch dependency {required} needs operation-wide review.");
            foreach (var step in batch.Steps)
            {
                var edit = BatchValidation.Resolve(input.Edit.WorkspaceName, step, aliases);
                if (CheckPlan(step.Operation, input with { Edit = edit }, state) is { } error) return error;
            }
        }
        return null;
    }
    public async Task<WorkerResponse> ExecuteAsync(WorkerRequest request, CancellationToken token)
    {
        var input = request.Payload.Deserialize<EngineeringInput>(WorkerJson.Options);
        if (input is null) return WorkerJson.Failed(request, "invalid_request");
        token.ThrowIfCancellationRequested();
        var accepted = policy.GetReadiness();
        if (CheckPlan(request.Operation, input, accepted) is { } denied) return WorkerJson.Failed(request, denied.Code);
        var result = await inner.ExecuteAsync(request, token);
        if (result.Payload is { ValueKind: JsonValueKind.Object } payload)
        {
            var enriched = System.Text.Json.Nodes.JsonNode.Parse(payload.GetRawText())!.AsObject();
            enriched["acceptance"] = JsonSerializer.SerializeToNode(accepted, WorkerJson.Options);
            result = result with { Payload = JsonSerializer.SerializeToElement(enriched, WorkerJson.Options) };
        }
        return result;
    }
}
