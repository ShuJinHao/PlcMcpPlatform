using System.Text.Json;
using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Delivery;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.EngineeringService.Delivery;

public sealed class DeliveryExecutor(IEngineeringExecutor worker, IManagedProjectFiles files, IProjectChangeStore changes, IDeliveryArtifacts artifacts) : IEngineeringExecutor
{
    public string ExecutionMode => worker.ExecutionMode;
    public OperationError? Unavailability => worker.Unavailability;
    public ValidationEvidence? Validation => worker.Validation;
    public OperationError? CheckAvailability(string op) => worker.CheckAvailability(op);
    public async Task<WorkerResponse> ExecuteAsync(WorkerRequest request, CancellationToken token)
    {
        if (request.Operation != DeliveryOperations.Create) return await worker.ExecuteAsync(request, token);
        var executing = false; string? candidate = null;
        try
        {
            var input = request.Payload.Deserialize<EngineeringInput>(WorkerJson.Options)!;
            var delivery = input.Delivery ?? throw new ProjectEditException("invalid_delivery_request");
            if (request.ExpectedRevision != delivery.Revision || input.ProjectPath != files.WorkspacePath(delivery.WorkspaceName)) throw new ProjectEditException("invalid_delivery_request");
            foreach (var op in DeliveryService.RequiredOperations)
                if (worker.CheckAvailability(op) is { } denied) return WorkerJson.Failed(request, denied.Code);
            var head = await changes.GetHeadAsync(input.ProjectPath, token) ?? throw new ProjectEditException("workspace_not_found");
            var source = head.Snapshot;
            if (source.Revision != delivery.Revision || source.Template.ExecutionMode != ExecutionMode) throw new ProjectEditException("workspace_revision_conflict");
            await files.VerifyAsync(source, token);
            // Compile a private copy. IDE cache writes must never alter an already published version.
            var prepared = await files.PrepareAsync(input.ProjectPath, request.JobId, source, token);
            candidate = prepared.Candidate.Directory;
            var project = Path.Combine(candidate, source.ProjectFile);
            var sha = await files.ProjectFileRevisionAsync(prepared.Candidate, token);
            async Task<WorkerResponse> Invoke(string op, EngineeringInput data)
            {
                token.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow >= request.DeadlineUtc) throw new ProjectEditException("deadline_expired");
                var call = request with { RequestId = Guid.NewGuid(), Operation = op, ExpectedRevision = sha, Payload = JsonSerializer.SerializeToElement(data, WorkerJson.Options) };
                executing = true; var response = await worker.ExecuteAsync(call, token);
                if (!WorkerJson.Matches(call, response) || !WorkerResultValidation.IsValid(call, response)) return WorkerJson.Unknown(call, "worker_protocol_error");
                executing = response.Outcome == "unknown"; return response;
            }
            WorkerResponse Failure(WorkerResponse failed) => failed with { RequestId = request.RequestId,
                Payload = JsonSerializer.SerializeToElement(new { packaged = false, revision = source.Revision, candidatePath = candidate, details = failed.Payload }, WorkerJson.Options) };
            var tasks = await Invoke(ConfigurationOperations.TaskList, new(project, ObjectId: delivery.ApplicationId, MaxObjects: 2000, IdeProfile: source.Template.IdeProfile));
            if (tasks.Outcome != "succeeded") return Failure(tasks);
            if (tasks.Payload!.Value.GetProperty("configuration").GetProperty("data").GetProperty("truncated").GetBoolean()) throw new ProjectEditException("task_inventory_incomplete");
            if (!ConfigurationResultValidation.CompleteTaskBindings(tasks.Payload!.Value)) throw new ProjectEditException("task_binding_unresolved");
            var compile = await Invoke(EngineeringOperations.Compile, new(project, ApplicationId: delivery.ApplicationId, IdeProfile: source.Template.IdeProfile));
            if (compile.Outcome != "succeeded") return Failure(compile);
            await files.VerifyAsync(prepared.Candidate, token);
            await files.VerifyAsync(source, token);
            if ((await changes.GetHeadAsync(input.ProjectPath, token))?.Snapshot.Revision != source.Revision) throw new ProjectEditException("workspace_revision_conflict");
            var manifest = new DeliveryManifest("1.4", delivery.WorkspaceName, source.Revision, source.ProjectFile, sha, source.Files, source.Template,
                source.Dependencies, ExecutionMode, ExecutionMode == "fixture" ? "simulated" : "unverified", false, Validation);
            var artifact = await artifacts.CreateAsync(source, manifest, compile.Payload!.Value, tasks.Payload!.Value, token);
            token.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= request.DeadlineUtc) throw new ProjectEditException("deadline_expired");
            await files.VerifyAsync(source, token);
            if ((await changes.GetHeadAsync(input.ProjectPath, token))?.Snapshot.Revision != source.Revision) throw new ProjectEditException("workspace_revision_conflict");
            return new(1, request.RequestId, request.JobId, "succeeded", null, ExecutionMode,
                JsonSerializer.SerializeToElement(new { packaged = true, revision = source.Revision, artifact, compile = compile.Payload, tasks = tasks.Payload, validation = Validation }, WorkerJson.Options));
        }
        catch (Exception e) when (e is ProjectEditException or OperationCanceledException or IOException or UnauthorizedAccessException or JsonException)
        {
            var code = e is ProjectEditException known ? known.Code : e is OperationCanceledException ? "delivery_cancelled" : "delivery_failed";
            return new(1, request.RequestId, request.JobId, executing ? "unknown" : e is OperationCanceledException ? "cancelled" : "failed", code, ExecutionMode,
                JsonSerializer.SerializeToElement(new { packaged = false, candidatePath = candidate }, WorkerJson.Options));
        }
    }
}
