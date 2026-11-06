using System.Text.Json;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;

namespace PlcMcp.EngineeringService.Incremental;

internal sealed class ChildExecutionException(WorkerResponse response) : Exception(response.ErrorCode)
{
    public WorkerResponse Response { get; } = response;
}
internal sealed class WorkerSession(IEngineeringExecutor worker, WorkerRequest parent, string revision)
{
    public string Revision { get; set; } = revision;
    public bool Executing { get; private set; }
    public string? Operation { get; private set; }
    public string? ObjectId { get; private set; }
    public async Task<JsonElement> Invoke(string operation, EngineeringInput input, CancellationToken token)
    {
        if (worker.CheckAvailability(operation) is { } unavailable) throw new ProjectEditException(unavailable.Code);
        token.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow >= parent.DeadlineUtc) throw new ProjectEditException("deadline_expired");
        Operation = operation; ObjectId = input.ObjectId ?? input.Edit?.ObjectId ?? input.ApplicationId;
        var request = parent with { RequestId = Guid.NewGuid(), Operation = operation, ExpectedRevision = Revision,
            Payload = JsonSerializer.SerializeToElement(input, WorkerJson.Options) };
        Executing = true;
        var response = await worker.ExecuteAsync(request, token);
        if (!WorkerJson.Matches(request, response) || !WorkerResultValidation.IsValid(request, response)) response = WorkerJson.Unknown(request, "worker_protocol_error");
        Executing = response.Outcome == "unknown";
        if (response.Outcome != "succeeded") throw new ChildExecutionException(response);
        return response.Payload!.Value;
    }
    public WorkerResponse Failure(Exception error)
    {
        var child = (error as ChildExecutionException)?.Response;
        var code = child?.ErrorCode ?? (error as ProjectEditException)?.Code ?? (error is OperationCanceledException ? "incremental_cancelled" : "incremental_failed");
        var outcome = child?.Outcome ?? (Executing ? "unknown" : error is OperationCanceledException ? "cancelled" : "failed");
        return new(1, parent.RequestId, parent.JobId, outcome, code, parent.ExecutionMode,
            JsonSerializer.SerializeToElement(new { jobId = parent.JobId, operation = Operation, objectId = ObjectId, details = child?.Payload }, WorkerJson.Options));
    }
}
