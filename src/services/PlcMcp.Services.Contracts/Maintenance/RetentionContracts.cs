using PlcMcp.Services.Contracts.Editing;

namespace PlcMcp.Services.Contracts.Maintenance;

public sealed record RetentionPolicy(int UploadDays = 14, int FailedCandidateDays = 30, int DeliveryDays = 90, string[]? PinnedDeliveryIds = null);
public sealed record RetentionFailure(ChangeEvidence Evidence, DateTimeOffset FinishedAt);
public sealed record RetentionState(string Fingerprint, bool Blocked, ManagedHead[] Heads, RetentionFailure[] Failures);
public interface IRetentionStateSource
{
    Task<RetentionState> ReadAsync(CancellationToken token);
}
public sealed record RetentionFile(string Path, long Bytes, long LastWriteTicks, string Sha256);
public sealed record RetentionItem(string Kind, string Id, string Root, string Target, RetentionFile[] Files, string[] Directories);
public sealed record RetentionPlan(int SchemaVersion, string DataDirectory, string WorkspaceRoot, DateTimeOffset EvaluatedAt,
    RetentionPolicy Policy, string StateFingerprint, RetentionItem[] Items, string PlanSha256);
public sealed record RetentionReceipt(string PlanSha256, string State, string[] CompletedItems, DateTimeOffset UpdatedAt, string? ErrorCode = null);
