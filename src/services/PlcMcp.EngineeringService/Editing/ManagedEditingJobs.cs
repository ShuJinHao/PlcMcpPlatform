using System.Security.Cryptography;
using System.Text.Json;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.EngineeringService.Editing;

public sealed class ManagedEditingJobs(IJobStore jobs, IProjectChangeStore changes, IManagedProjectFiles files, IEngineeringExecutor executor) : IProjectReconciliation
{
    public async Task<Result<JobView>> SubmitAsync(string operation, EditInput input, string key, string? expectedRevision, int timeoutSeconds, CancellationToken token)
    {
        try
        {
            var validation = Validate(operation, input, key, expectedRevision, timeoutSeconds);
            if (validation is not null) return Result<JobView>.Failure(validation, "Invalid editing request.");
            var workspace = files.WorkspacePath(input.WorkspaceName);
            var edit = input with { ParentId = CanonicalGuid(input.ParentId), ObjectId = CanonicalGuid(input.ObjectId) };
            var submission = new Submission(operation, new(workspace, Edit: edit), key, expectedRevision, timeoutSeconds);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(submission, WorkerJson.Options);
            if (bytes.Length > WorkerJson.MaxRequestBytes - 16384) return Result<JobView>.Failure("request_too_large", "Split the source into smaller explicit edits.");
            var fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
            var cached = await jobs.FindSubmissionAsync(workspace, operation, key, fingerprint, token);
            if (cached is not null) return cached;
            if (executor.Unavailability is { } unavailable) return Result<JobView>.Failure(unavailable.Code, unavailable.Message);
            var head = await changes.GetHeadAsync(workspace, token);
            ProjectSnapshot source;
            if (operation == EditOperations.CreateProject)
            {
                if (head is not null) return Result<JobView>.Failure("workspace_exists", "Choose another workspace name.");
                source = await files.ReadTemplateAsync(edit.TemplateId!, executor.ExecutionMode, token);
            }
            else
            {
                if (head is null) return Result<JobView>.Failure("workspace_not_found", "Create a managed workspace first.");
                source = head.Snapshot;
                if (source.Template.ExecutionMode != executor.ExecutionMode) return Result<JobView>.Failure("execution_mode_mismatch", "Workspace mode mismatch.");
                await files.VerifyAsync(source, token);
            }
            if (bytes.Length + JsonSerializer.SerializeToUtf8Bytes(source.Template, WorkerJson.Options).Length > WorkerJson.MaxRequestBytes - 16384)
                return Result<JobView>.Failure("request_too_large", "Source and template requirements exceed the worker limit.");
            return await jobs.SubmitAsync(submission, new(workspace, source.Revision), fingerprint, executor.ExecutionMode, token);
        }
        catch (ProjectEditException error) { return Result<JobView>.Failure(error.Code, "The workspace or template does not satisfy this operation."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return Result<JobView>.Failure("project_unavailable", "Workspace or template data is unavailable or invalid."); }
    }
    public async Task<Result<WorkspaceInfo>> GetWorkspaceAsync(string name, CancellationToken token)
    {
        try
        {
            var head = await changes.GetHeadAsync(files.WorkspacePath(name), token);
            if (head is null) return Result<WorkspaceInfo>.Failure("workspace_not_found", "No managed workspace with this name.");
            await files.VerifyAsync(head.Snapshot, token);
            return Result<WorkspaceInfo>.Success(new(name, head.Snapshot.Revision,
                Path.Combine(head.Snapshot.Directory, head.Snapshot.ProjectFile), head.IsDelivery, head.Snapshot.Template, head.Snapshot.Files));
        }
        catch (ProjectEditException error) { return Result<WorkspaceInfo>.Failure(error.Code, "Workspace verification failed."); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return Result<WorkspaceInfo>.Failure("project_unavailable", "Workspace data is unavailable."); }
    }
    public async Task<Result<JobView>> ReconcileAsync(Guid jobId, CancellationToken token)
    {
        var job = await jobs.GetAsync(jobId, token);
        if (job is null) return Result<JobView>.Failure("job_not_found", "No such job.");
        if (job.Status != "OutcomeUnknown") return Result<JobView>.Success(job);
        var evidence = await changes.GetChangeAsync(jobId, token);
        if (evidence is { ExecutorStopped: true } && evidence.Stage is "preparing" or "prepared" or "verified")
        {
            var request = await jobs.GetRequestAsync(jobId, token);
            if (request is not null)
            {
                var interrupted = WorkerJson.Failed(request, "interrupted_before_publication");
                var previousStage = evidence.Stage;
                evidence = evidence with { Stage = "failed", ErrorCode = interrupted.ErrorCode, Response = interrupted };
                try { await changes.RecordAsync(evidence, token, previousStage); }
                catch (ProjectEditException) { return Result<JobView>.Failure("reconciliation_raced", "Evidence changed; inspect the job again."); }
            }
        }
        if (evidence?.Response is not { } response || !evidence.ExecutorStopped || evidence.Stage is not ("published" or "failed" or "cancelled"))
            return Result<JobView>.Failure("reconciliation_unproven", "No durable proof that the executor stopped and the change completed. The slot remains blocked.");
        try
        {
            var head = await changes.GetHeadAsync(evidence.WorkspacePath, token);
            if (evidence.Stage == "published" && (head?.JobId != jobId || head.Snapshot.Revision != response.Payload!.Value.GetProperty("revision").GetString()))
                throw new ProjectEditException("reconciliation_conflict");
            if (head is not null)
            {
                if (evidence.Stage != "published" && head.JobId == jobId) throw new ProjectEditException("reconciliation_conflict");
                await files.VerifyAsync(head.Snapshot, token);
            }
            return await jobs.ReconcileAsync(response, token);
        }
        catch (Exception error) when (error is ProjectEditException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return Result<JobView>.Failure("reconciliation_conflict", "Saved evidence does not match current files. The slot remains blocked."); }
    }
    private static string? Validate(string op, EditInput input, string key, string? revision, int timeout)
    {
        if (!EditOperations.Contains(op) || string.IsNullOrWhiteSpace(key) || key.Length > 128 || timeout is < 1 or > 600
            || revision is { Length: > 128 } || string.IsNullOrWhiteSpace(input.WorkspaceName)) return "invalid_request";
        if (op == EditOperations.CreateProject)
            return string.IsNullOrWhiteSpace(input.TemplateId) ? "template_required" : null;
        if (string.IsNullOrWhiteSpace(revision)) return "expected_revision_required";
        if (op == EditOperations.SaveProject) return null;
        if (EditOperations.CreatesObject(op) && (!ValidGuid(input.ParentId) || string.IsNullOrWhiteSpace(input.Name)
            || input.Name.Length > 128 || !System.Text.RegularExpressions.Regex.IsMatch(input.Name, "^[A-Za-z_][A-Za-z0-9_]*$"))) return "invalid_object_identity";
        if (EditOperations.UpdatesObject(op) && !ValidGuid(input.ObjectId)) return "invalid_object_identity";
        if (input.Declaration is { Length: > 65536 } || input.Implementation is { Length: > 65536 }
            || input.Declaration?.Contains('\0') == true || input.Implementation?.Contains('\0') == true || input.BaseType is { Length: > 256 }) return "invalid_source";
        if (EditOperations.CreatesObject(op) && string.IsNullOrWhiteSpace(input.Declaration)
            || EditOperations.UpdatesObject(op) && input.Declaration is null && input.Implementation is null) return "source_required";
        if (op == EditOperations.CreatePou && (input.PouType is not ("Program" or "FunctionBlock" or "Function")
            || input.Language is not ("ST" or "IL" or "LD" or "FBD" or "SFC" or "CFC"))) return "invalid_pou_definition";
        if (op == EditOperations.CreatePou && input.PouType == "Function" && input.Language == "SFC") return "language_not_applicable";
        if (op == EditOperations.CreatePou && input.PouType != "Function" && input.BaseType is not null) return "return_type_not_applicable";
        if (op == EditOperations.CreatePou && input.PouType == "Function" && string.IsNullOrWhiteSpace(input.BaseType)) return "return_type_required";
        if (op == EditOperations.CreatePou && input.Language is not ("ST" or "IL") && input.Implementation is not null) return "graphical_content_requires_specialized_editor";
        if (op is EditOperations.CreateGvl or EditOperations.UpdateGvl or EditOperations.CreateDut or EditOperations.UpdateDut && input.Implementation is not null) return "declaration_only_object";
        if (op == EditOperations.CreateDut && (input.DutType is not ("Structure" or "Enumeration" or "Alias" or "Union")
            || input.DutType == "Alias" && string.IsNullOrWhiteSpace(input.BaseType))) return "invalid_dut_definition";
        if (op == EditOperations.CreateDut && input.DutType == "Union" && input.BaseType is not null) return "base_type_not_applicable";
        return null;
    }
    private static bool ValidGuid(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty;
    private static string? CanonicalGuid(string? value) => Guid.TryParse(value, out var id) ? id.ToString() : value;
}
