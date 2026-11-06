using PlcMcp.EngineeringService.Configuration;
using PlcMcp.EngineeringService.Exchange;
using PlcMcp.Services.Contracts.Exchange;
using PlcMcp.Services.Contracts.Configuration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.EngineeringService.Execution;

public sealed class EngineeringJobs(IJobStore store, IWorkspaceFiles files, IEngineeringExecutor executor)
{
    public async Task<Result<JobView>> SubmitAsync(Submission submission, CancellationToken cancellationToken)
    {
        if (!EngineeringOperations.Contains(submission.Operation) || string.IsNullOrWhiteSpace(submission.IdempotencyKey)
            || submission.IdempotencyKey.Length > 128 || submission.TimeoutSeconds is < 1 or > 600
            || submission.Input.MaxObjects is < 1 or > 2000
            || submission.ExpectedRevision is { Length: > 128 }
            || submission.Operation is EngineeringOperations.ReadProgram or EngineeringOperations.ReadGvl or EngineeringOperations.ReadDut && !Guid.TryParse(submission.Input.ObjectId, out _)
            || submission.Operation == EngineeringOperations.Compile && !Guid.TryParse(submission.Input.ApplicationId, out _))
            return Result<JobView>.Failure("invalid_request", "Invalid operation, identity or limits.");
        if (ConfigurationOperations.IsRead(submission.Operation) && ConfigurationValidation.Read(submission.Operation, submission.Input.ObjectId, submission.Input.Query) is { } validation)
            return Result<JobView>.Failure(validation, "Invalid configuration query.");
        if (ExchangeOperations.IsRead(submission.Operation) && ExchangeValidation.Read(submission.Operation, submission.Input) is { } exchangeError)
            return Result<JobView>.Failure(exchangeError, "Invalid exchange selection.");
        var path = files.NormalizePath(submission.Input.ProjectPath);
        if (!path.IsSuccess) return Result<JobView>.Failure(path.Error!.Code, path.Error.Message);
        var normalized = submission with { Input = submission.Input with { ProjectPath = path.Value!,
            ObjectId = Guid.TryParse(submission.Input.ObjectId, out var objectId) ? objectId.ToString() : submission.Input.ObjectId,
            Exchange = submission.Input.Exchange is { } exchange ? exchange with { ObjectIds = exchange.ObjectIds.Select(id => Guid.TryParse(id, out var guid) ? guid.ToString() : id).ToArray() } : null,
            ApplicationId = Guid.TryParse(submission.Input.ApplicationId, out var appId) ? appId.ToString() : submission.Input.ApplicationId } };
        // File revision is deliberately not added to the fingerprint: retrying the same
        // command after execution must return its durable job, even if the file changed.
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(normalized, WorkerJson.Options));
        var fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
        var existing = await store.FindSubmissionAsync(path.Value!, normalized.Operation, normalized.IdempotencyKey, fingerprint, cancellationToken);
        if (existing is not null) return existing;
        if (executor.CheckAvailability(submission.Operation) is { } error)
            return Result<JobView>.Failure(error.Code, error.Message);
        var resolved = await files.ResolveAsync(path.Value!, cancellationToken);
        if (!resolved.IsSuccess) return Result<JobView>.Failure(resolved.Error!.Code, resolved.Error.Message);
        return await store.SubmitAsync(normalized, resolved.Value!, fingerprint, executor.ExecutionMode, cancellationToken);
    }

    public async Task<Result<JobView>> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await store.GetAsync(id, cancellationToken) is { } job ? Result<JobView>.Success(job)
            : Result<JobView>.Failure("job_not_found", "No such job.");
    public Task<Result<JobView>> CancelAsync(Guid id, CancellationToken cancellationToken) => store.CancelAsync(id, cancellationToken);
}
