using PlcMcp.Services.Contracts.Incremental;
using PlcMcp.Services.Contracts.Configuration;
using System.Text.Json;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.EngineeringService.Editing;

/// <summary>Immutable candidate execution, followed by a short metadata publication transaction.</summary>
public sealed class ManagedProjectExecutor(IEngineeringExecutor worker, IManagedProjectFiles files, IProjectChangeStore changes) : IEngineeringExecutor
{
    public string ExecutionMode => worker.ExecutionMode;
    public OperationError? Unavailability => worker.Unavailability;
    public OperationError? CheckAvailability(string operation) => worker.CheckAvailability(operation);
    public ValidationEvidence? Validation => worker.Validation;
    public async Task<WorkerResponse> ExecuteAsync(WorkerRequest request, CancellationToken token)
    {
        if (CheckAvailability(request.Operation) is { } unavailable) return WorkerJson.Failed(request, unavailable.Code);
        if (!EditOperations.Contains(request.Operation)) return await worker.ExecuteAsync(request, token);
        var input = request.Payload.Deserialize<EngineeringInput>(WorkerJson.Options)!;
        var edit = input.Edit!; var creating = request.Operation is EditOperations.CreateProject or IncrementalOperations.Adopt;
        var steps = new List<string>();
        var evidence = new ChangeEvidence(request.JobId, input.ProjectPath, "preparing", request.ExpectedRevision, null, null, null, null, true, null, []);
        var launched = false; var stopped = true;
        try
        {
            if (request.ExecutionMode != ExecutionMode || files.WorkspacePath(edit.WorkspaceName) != input.ProjectPath)
                throw new ProjectEditException("execution_mode_or_workspace_mismatch");
            if (await changes.GetChangeAsync(request.JobId, token) is not null) return WorkerJson.Unknown(request, "change_requires_reconciliation");
            await changes.RecordAsync(evidence, token);
            var head = await changes.GetHeadAsync(input.ProjectPath, token);
            if (creating && head is not null) throw new ProjectEditException("workspace_exists");
            var current = creating ? request.Operation == IncrementalOperations.Adopt
                ? await files.ReadAdoptionAsync(edit.Adoption!, ExecutionMode, token) : await files.ReadTemplateAsync(edit.TemplateId!, ExecutionMode, token)
                : head?.Snapshot ?? throw new ProjectEditException("workspace_not_found");
            if (current.Revision != request.ExpectedRevision) throw new ProjectEditException("workspace_revision_conflict");
            await files.VerifyAsync(current, token);
            var source = current;
            if (request.Operation == IncrementalOperations.Restore)
            {
                var record = await ((IProjectHistoryStore)changes).GetAsync(input.ProjectPath, edit.RestoreRevision!, token) ?? throw new ProjectEditException("version_not_found");
                source = await files.ReadVersionAsync(record, current.Template, token);
            }
            if (source.Template.ExecutionMode != ExecutionMode) throw new ProjectEditException("execution_mode_mismatch");
            await files.VerifyAsync(source, token); steps.Add("source_verified");
            var prepared = await files.PrepareAsync(input.ProjectPath, request.JobId, source, token);
            var candidate = prepared.Candidate; steps.Add("backup_verified");
            evidence = evidence with { Stage = "prepared", CandidatePath = candidate.Directory, BackupPath = prepared.BackupPath, Steps = steps.ToArray() };
            await changes.RecordAsync(evidence, token);
            if (DateTimeOffset.UtcNow >= request.DeadlineUtc) throw new ProjectEditException("deadline_expired");
            var workerRequest = request with
            {
                Operation = creating || request.Operation == EditOperations.SaveProject ? EditOperations.VerifyProject : request.Operation,
                ExpectedRevision = await files.ProjectFileRevisionAsync(candidate, token),
                Payload = JsonSerializer.SerializeToElement(input with { ProjectPath = Path.Combine(candidate.Directory, candidate.ProjectFile), Candidate = true, IdeProfile = source.Template.IdeProfile, Requirements = source.Template, CurrentDependencies = source.Dependencies }, WorkerJson.Options)
            };
            evidence = evidence with { Stage = "executing", ExecutorStopped = false };
            await changes.RecordAsync(evidence, token);
            token.ThrowIfCancellationRequested(); launched = true; stopped = false;
            var result = await worker.ExecuteAsync(workerRequest, token);
            if (!WorkerJson.Matches(workerRequest, result) || !WorkerResultValidation.IsValid(workerRequest, result)) result = WorkerJson.Unknown(workerRequest, "worker_protocol_error");
            if (result.Outcome == "unknown")
            {
                evidence = evidence with { Stage = "unknown", ErrorCode = result.ErrorCode };
                await changes.RecordAsync(evidence, CancellationToken.None);
                return WithFailureEvidence(request, result.Outcome, result.ErrorCode!, evidence, result.Payload);
            }
            stopped = true;
            if (result.Outcome != "succeeded")
            {
                var failure = WithFailureEvidence(request, result.Outcome, result.ErrorCode!, evidence, result.Payload);
                await changes.RecordAsync(evidence with { Stage = result.Outcome == "cancelled" ? "cancelled" : "failed", ErrorCode = result.ErrorCode,
                    ExecutorStopped = true, Response = failure }, CancellationToken.None);
                return failure;
            }
            steps.Add("saved_and_reopened");
            if (await files.ProjectFileRevisionAsync(candidate, token) != result.Payload!.Value.GetProperty("revision").GetString())
                throw new ProjectEditException("candidate_revision_conflict");
            await files.VerifyAsync(source, token);
            await files.VerifyAsync(current, token);
            token.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= request.DeadlineUtc) throw new ProjectEditException("deadline_expired");
            var dependencies = result.Payload!.Value.TryGetProperty("dependencySnapshot", out var observed)
                ? observed.Deserialize<DependencySnapshot>(WorkerJson.Options) : source.Dependencies;
            candidate = candidate with { Dependencies = dependencies };
            var sealedVersion = await files.SealAsync(input.ProjectPath, request.JobId, candidate, token);
            await files.VerifyAsync(sealedVersion, token); steps.Add("version_verified");
            evidence = evidence with { Stage = "verified", VersionPath = sealedVersion.Directory, ExecutorStopped = true, Steps = steps.ToArray() };
            await changes.RecordAsync(evidence, token);
            var payload = new EditResult(edit.WorkspaceName, request.ExpectedRevision, sealedVersion.Revision, "managed_project_manifest_v2",
                Path.Combine(sealedVersion.Directory, sealedVersion.ProjectFile), evidence.BackupPath, sealedVersion.Directory,
                true, request.Operation == EditOperations.SaveProject, [.. steps, "published"], result.Payload, Simulated: ExecutionMode == "fixture", CurrentDependencies: dependencies);
            var response = new WorkerResponse(1, request.RequestId, request.JobId, "succeeded", null, ExecutionMode, JsonSerializer.SerializeToElement(payload, WorkerJson.Options));
            var published = evidence with { Stage = "published", Steps = payload.Steps, Response = response, Snapshot = sealedVersion };
            // Once publication starts, cancellation cannot turn a committed version into a cancelled job.
            if (!await changes.PublishAsync(new(input.ProjectPath, edit.WorkspaceName, sealedVersion, request.JobId, payload.IsDelivery), published, creating, CancellationToken.None))
                throw new ProjectEditException("workspace_revision_conflict");
            return response;
        }
        catch (Exception error)
        {
            // A database commit can succeed before its acknowledgement is lost. Preserve published evidence.
            var recorded = await changes.GetChangeAsync(request.JobId, CancellationToken.None);
            if (recorded?.Stage == "published") return WorkerJson.Unknown(request, "publication_requires_reconciliation");
            if (recorded?.Stage is "failed" or "cancelled") return recorded.Response ?? WorkerJson.Failed(request, "job_execution_ownership_lost");
            if (recorded?.Stage == "unknown") return WorkerJson.Unknown(request, recorded.ErrorCode ?? "execution_outcome_unknown");
            var code = error is ProjectEditException known ? known.Code : error is OperationCanceledException ? "editing_cancelled" : "editing_failed";
            var outcome = launched && !stopped ? "unknown" : error is OperationCanceledException ? "cancelled" : "failed";
            var response = WithFailureEvidence(request, outcome, code, evidence);
            await changes.RecordAsync(evidence with { Stage = outcome, ExecutorStopped = stopped, ErrorCode = code, Steps = steps.ToArray(), Response = response }, CancellationToken.None);
            return response;
        }
    }
    private static WorkerResponse WithFailureEvidence(WorkerRequest request, string outcome, string code, ChangeEvidence evidence, JsonElement? vendorDetails = null) =>
        new(1, request.RequestId, request.JobId, outcome, code, request.ExecutionMode,
            JsonSerializer.SerializeToElement(new { published = false, baseRevision = request.ExpectedRevision,
                candidatePath = evidence.VersionPath ?? evidence.CandidatePath, backupPath = evidence.BackupPath,
                steps = evidence.Steps, vendorDetails, candidateMayContainChanges = evidence.Stage is "executing" or "verified" or "unknown",
                simulated = request.ExecutionMode == "fixture" }, WorkerJson.Options));
}
