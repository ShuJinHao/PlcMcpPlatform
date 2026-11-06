using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlcMcp.Core.Automation;
using PlcMcp.Core.Workspaces;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.EntityFrameworkCore;

/// <summary>Singleton within the exclusive data-directory owner. All transactions are short.</summary>
public sealed class SqliteJobStore(DbContextOptions<PlcMcpDbContext> options, string mode, int maxPendingJobs = 256) : IJobStore, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<Result<JobView>?> FindSubmissionAsync(string projectPath, string operation, string idempotencyKey,
        string fingerprint, CancellationToken cancellationToken)
    {
        await using var db = new PlcMcpDbContext(options);
        var query = from job in db.Jobs.AsNoTracking() join workspace in db.Workspaces on job.WorkspaceId equals workspace.Id
                    where workspace.ProjectPath == projectPath && job.Operation == operation && job.IdempotencyKey == idempotencyKey select job;
        var existing = await query.SingleOrDefaultAsync(cancellationToken);
        if (existing is null) return null;
        return existing.RequestFingerprint == fingerprint
            ? Result<JobView>.Success(View(existing, await db.JobRecords.AsNoTracking().SingleAsync(x => x.JobId == existing.Id, cancellationToken)))
            : Result<JobView>.Failure("idempotency_conflict", "This key belongs to a different request.");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = new PlcMcpDbContext(options);
            await db.Database.MigrateAsync(cancellationToken);
            var state = await db.ExecutionStates.SingleOrDefaultAsync(cancellationToken);
            if (state is null) { state = new() { ExecutionMode = mode }; db.ExecutionStates.Add(state); }
            if (state.ExecutionMode != mode) throw new InvalidOperationException("execution_mode_database_mismatch");
            var abandoned = await db.Jobs.Where(x => x.Status == JobStatus.Running || x.Status == JobStatus.CancelRequested).ToListAsync(cancellationToken);
            foreach (var job in abandoned) job.MarkOutcomeUnknown(Now(job));
            if (abandoned.Count != 0) { state.Blocked = true; state.Reason = "recovery_requires_reconciliation"; }
            await db.SaveChangesAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task<Result<JobView>> SubmitAsync(Submission submission, ResolvedProject project,
        string fingerprint, string executionMode, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = new PlcMcpDbContext(options);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            if (executionMode != mode) return Result<JobView>.Failure("execution_mode_mismatch", "Execution mode mismatch.");
            var workspace = await db.Workspaces.SingleOrDefaultAsync(x => x.ProjectPath == project.Path, cancellationToken);
            if (workspace is not null)
            {
                var existing = await db.Jobs.SingleOrDefaultAsync(x => x.WorkspaceId == workspace.Id && x.Operation == submission.Operation && x.IdempotencyKey == submission.IdempotencyKey, cancellationToken);
                if (existing is not null)
                    return existing.RequestFingerprint == fingerprint
                        ? Result<JobView>.Success(View(existing, await db.JobRecords.SingleAsync(x => x.JobId == existing.Id, cancellationToken)))
                        : Result<JobView>.Failure("idempotency_conflict", "This key belongs to a different request.");
            }
            if ((await db.ExecutionStates.SingleAsync(cancellationToken)).Blocked)
                return Result<JobView>.Failure("execution_outcome_unknown", "The execution slot requires reconciliation.");
            if (await db.Jobs.CountAsync(x => x.Status == JobStatus.Queued || x.Status == JobStatus.Running || x.Status == JobStatus.CancelRequested, cancellationToken) >= maxPendingJobs)
                return Result<JobView>.Failure("queue_full", "The execution queue is full; retry later with the same request key.", true);
            if (submission.ExpectedRevision is not null && submission.ExpectedRevision != project.Revision)
                return Result<JobView>.Failure("workspace_revision_conflict", "Project file changed; read the current revision.");
            if (workspace is null)
            {
                workspace = ProjectWorkspace.Register(mode == "fixture" ? "fixture" : "inovance", project.Path, project.Revision);
                db.Workspaces.Add(workspace);
            }
            var job = AutomationJob.Queue(workspace.Id, submission.Operation, submission.IdempotencyKey, fingerprint, DateTimeOffset.UtcNow);
            var record = new JobRecord { JobId = job.Id, RequestId = Guid.NewGuid(), ExecutionMode = mode,
                InputJson = JsonSerializer.Serialize(submission.Input, WorkerJson.Options), ExpectedRevision = project.Revision,
                TimeoutSeconds = submission.TimeoutSeconds };
            db.Jobs.Add(job); db.JobRecords.Add(record);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result<JobView>.Success(View(job, record));
        }
        finally { gate.Release(); }
    }

    public async Task<JobView?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = new PlcMcpDbContext(options);
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return job is null ? null : View(job, await db.JobRecords.AsNoTracking().SingleAsync(x => x.JobId == id, cancellationToken));
    }

    public async Task<Result<JobView>> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = new PlcMcpDbContext(options);
            var job = await db.Jobs.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (job is null) return Result<JobView>.Failure("job_not_found", "No such job.");
            if (job.Status is JobStatus.Queued or JobStatus.Running) job.RequestCancellation(Now(job));
            await db.SaveChangesAsync(cancellationToken);
            return Result<JobView>.Success(View(job, await db.JobRecords.SingleAsync(x => x.JobId == id, cancellationToken)));
        }
        finally { gate.Release(); }
    }

    public async Task<ClaimedJob?> ClaimAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = new PlcMcpDbContext(options);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            if ((await db.ExecutionStates.SingleAsync(cancellationToken)).Blocked
                || await db.Jobs.AnyAsync(x => x.Status == JobStatus.Running || x.Status == JobStatus.CancelRequested, cancellationToken)) return null;
            var job = await db.Jobs.Where(x => x.Status == JobStatus.Queued).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).FirstOrDefaultAsync(cancellationToken);
            if (job is null) return null;
            var record = await db.JobRecords.SingleAsync(x => x.JobId == job.Id, cancellationToken);
            var request = new WorkerRequest(1, record.RequestId, job.Id, job.Operation, job.WorkspaceId,
                record.ExpectedRevision, DateTimeOffset.UtcNow.AddSeconds(record.TimeoutSeconds), mode,
                JsonSerializer.Deserialize<JsonElement>(record.InputJson, WorkerJson.Options));
            record.RequestJson = JsonSerializer.Serialize(request, WorkerJson.Options);
            job.Start(Now(job));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(request);
        }
        finally { gate.Release(); }
    }

    public async Task FinishAsync(WorkerResponse response, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = new PlcMcpDbContext(options);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var job = await db.Jobs.SingleAsync(x => x.Id == response.JobId, cancellationToken);
            if (job.Status is not (JobStatus.Running or JobStatus.CancelRequested)) return;
            var record = await db.JobRecords.SingleAsync(x => x.JobId == job.Id, cancellationToken);
            var request = JsonSerializer.Deserialize<WorkerRequest>(record.RequestJson!, WorkerJson.Options)!;
            if (!WorkerJson.Matches(request, response) || !WorkerResultValidation.IsValid(request, response)) response = WorkerJson.Unknown(request, "worker_protocol_error");
            if (response.Outcome == "succeeded") job.CompleteVerified(Now(job));
            else if (response.Outcome == "failed") job.FailVerified(response.ErrorCode!, Now(job));
            else if (response.Outcome == "cancelled" && job.Status == JobStatus.CancelRequested) job.ConfirmCancellation(Now(job));
            else
            {
                job.MarkOutcomeUnknown(Now(job));
                var state = await db.ExecutionStates.SingleAsync(cancellationToken);
                state.Blocked = true; state.Reason = response.ErrorCode ?? "execution_outcome_unknown";
            }
            record.ResponseJson = JsonSerializer.Serialize(response, WorkerJson.Options);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally { gate.Release(); }
    }

    public async Task<WorkerRequest?> GetRequestAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = new PlcMcpDbContext(options);
        var record = await db.JobRecords.AsNoTracking().SingleOrDefaultAsync(x => x.JobId == id, cancellationToken);
        return record?.RequestJson is null ? null : JsonSerializer.Deserialize<WorkerRequest>(record.RequestJson, WorkerJson.Options);
    }

    public async Task<Result<JobView>> ReconcileAsync(WorkerResponse evidence, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = new PlcMcpDbContext(options);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            var job = await db.Jobs.SingleOrDefaultAsync(x => x.Id == evidence.JobId, cancellationToken);
            if (job is null) return Result<JobView>.Failure("job_not_found", "No such job.");
            var record = await db.JobRecords.SingleAsync(x => x.JobId == job.Id, cancellationToken);
            if (job.Status != JobStatus.OutcomeUnknown) return Result<JobView>.Success(View(job, record));
            var request = JsonSerializer.Deserialize<WorkerRequest>(record.RequestJson!, WorkerJson.Options)!;
            if (evidence.Outcome is not ("succeeded" or "failed" or "cancelled") || !WorkerJson.Matches(request, evidence) || !WorkerResultValidation.IsValid(request, evidence))
                return Result<JobView>.Failure("reconciliation_unproven", "Evidence does not establish a terminal outcome.");
            job.ReconcileVerified(evidence.Outcome == "succeeded" ? JobStatus.Succeeded : evidence.Outcome == "cancelled" ? JobStatus.Cancelled : JobStatus.Failed, evidence.ErrorCode, Now(job));
            record.ResponseJson = JsonSerializer.Serialize(evidence, WorkerJson.Options);
            await db.SaveChangesAsync(cancellationToken);
            if (!await db.Jobs.AnyAsync(x => x.Status == JobStatus.OutcomeUnknown || x.Status == JobStatus.Running || x.Status == JobStatus.CancelRequested, cancellationToken))
            {
                var state = await db.ExecutionStates.SingleAsync(cancellationToken);
                state.Blocked = false; state.Reason = null;
                await db.SaveChangesAsync(cancellationToken);
            }
            await tx.CommitAsync(cancellationToken);
            return Result<JobView>.Success(View(job, record));
        }
        finally { gate.Release(); }
    }

    public async Task<ExecutionSlot> GetSlotAsync(CancellationToken cancellationToken)
    {
        await using var db = new PlcMcpDbContext(options);
        var state = await db.ExecutionStates.AsNoTracking().SingleAsync(cancellationToken);
        return new(state.Blocked, state.Reason);
    }
    private static DateTimeOffset Now(AutomationJob job) => DateTimeOffset.UtcNow < job.UpdatedAt ? job.UpdatedAt : DateTimeOffset.UtcNow;
    private static JobView View(AutomationJob job, JobRecord record) => new(job.Id, job.WorkspaceId, job.Operation,
        job.Status.ToString(), record.ExecutionMode, record.ExecutionMode == "fixture" ? "simulated" : "ide_execution",
        false, job.OutcomeCode, record.ResponseJson is null ? null : JsonSerializer.Deserialize<WorkerResponse>(record.ResponseJson, WorkerJson.Options)?.Payload,
        job.CreatedAt, job.UpdatedAt);
    public void Dispose() => gate.Dispose();
}
