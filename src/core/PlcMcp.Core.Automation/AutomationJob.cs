using PlcMcp.SharedKernel.Domain;

namespace PlcMcp.Core.Automation;

public enum JobStatus { Queued, Running, CancelRequested, Succeeded, Failed, Cancelled, OutcomeUnknown }

/// <summary>持久化任务生命周期；不承担 Channel、进程启动或厂商调用。</summary>
public sealed class AutomationJob : IAggregateRoot
{
    private AutomationJob() { }
    public Guid Id { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public string Operation { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string RequestFingerprint { get; private set; } = string.Empty;
    public JobStatus Status { get; private set; }
    public string? OutcomeCode { get; private set; }
    public long Version { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static AutomationJob Queue(Guid workspaceId, string operation, string idempotencyKey,
        string requestFingerprint, DateTimeOffset now)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("Workspace is required.", nameof(workspaceId));
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestFingerprint);
        return new() { Id = Guid.NewGuid(), WorkspaceId = workspaceId, Operation = operation,
            IdempotencyKey = idempotencyKey, RequestFingerprint = requestFingerprint,
            Status = JobStatus.Queued, CreatedAt = now, UpdatedAt = now };
    }

    public void Start(DateTimeOffset now)
    {
        Require(JobStatus.Queued);
        MoveTo(JobStatus.Running, null, now);
    }

    public void RequestCancellation(DateTimeOffset now)
    {
        if (Status == JobStatus.Queued) MoveTo(JobStatus.Cancelled, "cancelled_before_execution", now);
        else if (Status == JobStatus.Running) MoveTo(JobStatus.CancelRequested, null, now);
        else if (Status != JobStatus.CancelRequested) throw new InvalidOperationException("job_not_cancellable");
    }

    public void ConfirmCancellation(DateTimeOffset now)
    {
        Require(JobStatus.CancelRequested);
        MoveTo(JobStatus.Cancelled, "executor_confirmed_cancellation", now);
    }

    public void CompleteVerified(DateTimeOffset now)
    {
        RequireExecuting();
        MoveTo(JobStatus.Succeeded, null, now);
    }

    public void FailVerified(string code, DateTimeOffset now)
    {
        RequireExecuting();
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        MoveTo(JobStatus.Failed, code, now);
    }

    public void MarkOutcomeUnknown(DateTimeOffset now)
    {
        RequireExecuting();
        MoveTo(JobStatus.OutcomeUnknown, "execution_outcome_unknown", now);
    }

    public void ReconcileVerified(JobStatus confirmed, string? errorCode, DateTimeOffset now)
    {
        Require(JobStatus.OutcomeUnknown);
        if (confirmed is not (JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled)) throw new InvalidOperationException("invalid_reconciliation");
        if (confirmed != JobStatus.Succeeded) ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        MoveTo(confirmed, confirmed == JobStatus.Succeeded ? null : errorCode, now);
    }

    private void Require(JobStatus expected)
    {
        if (Status != expected) throw new InvalidOperationException("invalid_job_transition");
    }
    private void RequireExecuting()
    {
        if (Status is not (JobStatus.Running or JobStatus.CancelRequested))
            throw new InvalidOperationException("invalid_job_transition");
    }
    private void MoveTo(JobStatus next, string? code, DateTimeOffset now)
    {
        if (now < UpdatedAt) throw new ArgumentOutOfRangeException(nameof(now));
        Status = next; OutcomeCode = code; UpdatedAt = now; Version++;
    }
}
