using System.Security.Cryptography;
using System.Text.Json;
using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Delivery;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.EngineeringService.Delivery;

public sealed class DeliveryService(IJobStore jobs, IManagedProjectFiles files, IProjectChangeStore changes, IEngineeringExecutor executor)
{
    public static readonly string[] RequiredOperations = [DeliveryOperations.Create, ConfigurationOperations.TaskList, EngineeringOperations.Compile];
    public async Task<Result<JobView>> CreateAsync(string workspace, string revision, string applicationId, string key, int timeout, CancellationToken token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || timeout is < 1 or > 600 || revision is not { Length: 100 }
                || !Guid.TryParse(applicationId, out var app) || app == Guid.Empty) throw new ProjectEditException("invalid_delivery_request");
            var path = files.WorkspacePath(workspace);
            var submission = new Submission(DeliveryOperations.Create, new(path, Delivery: new(workspace, revision, app.ToString())), key, revision, timeout);
            var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(submission, WorkerJson.Options)));
            var existing = await jobs.FindSubmissionAsync(path, submission.Operation, key, fingerprint, token);
            if (existing is not null) return existing;
            foreach (var operation in RequiredOperations)
                if (executor.CheckAvailability(operation) is { } denied) return Result<JobView>.Failure(denied.Code, denied.Message);
            var head = await changes.GetHeadAsync(path, token) ?? throw new ProjectEditException("workspace_not_found");
            if (head.Snapshot.Revision != revision) throw new ProjectEditException("workspace_revision_conflict");
            if (head.Snapshot.Template.ExecutionMode != executor.ExecutionMode) throw new ProjectEditException("execution_mode_mismatch");
            await files.VerifyAsync(head.Snapshot, token);
            if (await executor.CheckRequestAsync(submission.Operation, submission.Input, token) is { } scoped)
                return Result<JobView>.Failure(scoped.Code, scoped.Message);
            return await jobs.SubmitAsync(submission, new(path, revision), fingerprint, executor.ExecutionMode, token);
        }
        catch (ProjectEditException e) { return Result<JobView>.Failure(e.Code, "Delivery requires a matching managed revision and application."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { return Result<JobView>.Failure("project_unavailable", "The delivery source is unavailable."); }
    }
}
