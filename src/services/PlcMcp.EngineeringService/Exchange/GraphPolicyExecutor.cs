using System.Text.Json;
using PlcMcp.EngineeringService.Batch;
using PlcMcp.Services.Contracts.Batch;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Exchange;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.EngineeringService.Exchange;

/// <summary>Rechecks immutable graph artifacts before the candidate coordinator, including recovered jobs.</summary>
public sealed class GraphPolicyExecutor(IEngineeringExecutor worker, IExchangeArtifacts artifacts, IPlcGraphPolicy policy) : IEngineeringExecutor
{
    public string ExecutionMode => worker.ExecutionMode;
    public OperationError? Unavailability => worker.Unavailability;
    public OperationError? CheckAvailability(string operation) => worker.CheckAvailability(operation);
    public ValidationEvidence? Validation => worker.Validation;
    public RuntimeReadiness? Readiness => worker.Readiness;
    public async Task<OperationError?> CheckRequestAsync(string operation, EngineeringInput input, CancellationToken token)
    {
        if (await worker.CheckRequestAsync(operation, input, token) is { } unavailable) return unavailable;
        try
        {
            async Task Check(string id, string? xml = null)
            {
                var document = await artifacts.GetAsync(id, token);
                if (document.Artifact.Format != "plcopen" || xml is not null && xml != document.Xml)
                    throw new ProjectEditException("artifact_integrity_mismatch");
                if (policy.Validate(PlcOpenGraphCodec.Decode(document.Xml).Program) is { } error) throw new ProjectEditException(error);
            }
            if (operation == ExchangeOperations.WriteGraph)
            {
                if (input.Edit?.Exchange is not { } change) throw new ProjectEditException("invalid_graph_request");
                await Check(change.ArtifactId, change.Xml);
            }
            if (operation == BatchOperations.Apply)
            {
                if (input.Edit is not { Batch: { } plan } edit) throw new ProjectEditException("invalid_batch_plan");
                if (BatchValidation.Validate(edit.WorkspaceName, plan) is { } error) throw new ProjectEditException(error);
                foreach (var step in plan.Steps.Where(s => s.Operation == ExchangeOperations.WriteGraph)) await Check(step.Change.ArtifactId!);
            }
        }
        catch (ProjectEditException error) { return new(error.Code, "Graph input is outside the target declaration profile."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return new("artifact_unavailable", "Graph artifacts could not be checked."); }
        return null;
    }
    public async Task<WorkerResponse> ExecuteAsync(WorkerRequest request, CancellationToken token)
    {
        if (request.Operation is ExchangeOperations.WriteGraph or BatchOperations.Apply)
        {
            EngineeringInput? input;
            try { input = request.Payload.Deserialize<EngineeringInput>(WorkerJson.Options); }
            catch (JsonException) { return WorkerJson.Failed(request, "invalid_graph_request"); }
            if (input is null) return WorkerJson.Failed(request, "invalid_graph_request");
            if (await CheckRequestAsync(request.Operation, input, token) is { } error) return WorkerJson.Failed(request, error.Code);
        }
        return await worker.ExecuteAsync(request, token);
    }
}
