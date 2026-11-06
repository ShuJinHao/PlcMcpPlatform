using System.Text.Json;
using PlcMcp.SharedKernel.Results;
using PlcMcp.Services.Contracts.Workers;

namespace PlcMcp.Services.Contracts.Execution;

public static class EngineeringOperations
{
    public const string Open = "project.open";
    public const string Structure = "project.tree.read";
    public const string ReadProgram = "pou.read";
    public const string Compile = "project.compile";
    public static bool Contains(string value) => value is Open or Structure or ReadProgram or Compile;
}

public sealed record EngineeringInput(string ProjectPath, string? ObjectId = null,
    string? ApplicationId = null, int MaxObjects = 256);
public sealed record Submission(string Operation, EngineeringInput Input, string IdempotencyKey,
    string? ExpectedRevision = null, int TimeoutSeconds = 60);
public sealed record ResolvedProject(string Path, string Revision);
public sealed record JobView(Guid JobId, Guid WorkspaceId, string Operation, string Status,
    string ExecutionMode, string EvidenceKind, bool EngineeringReady, string? ErrorCode,
    JsonElement? Result, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record ExecutionSlot(bool Blocked, string? Reason);
public sealed record ClaimedJob(WorkerRequest Request);

public interface IWorkspaceFiles
{
    Task<Result<ResolvedProject>> ResolveAsync(string path, CancellationToken cancellationToken);
}
public interface IEngineeringExecutor
{
    string ExecutionMode { get; }
    // Real IDE support is deliberately not enabled by configuration alone.
    OperationError? Unavailability { get; }
    Task<WorkerResponse> ExecuteAsync(WorkerRequest request, CancellationToken cancellationToken);
}
public interface IJobStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<Result<JobView>> SubmitAsync(Submission submission, ResolvedProject project,
        string fingerprint, string executionMode, CancellationToken cancellationToken);
    Task<JobView?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<Result<JobView>> CancelAsync(Guid id, CancellationToken cancellationToken);
    Task<ClaimedJob?> ClaimAsync(CancellationToken cancellationToken);
    Task FinishAsync(WorkerResponse response, CancellationToken cancellationToken);
    Task<ExecutionSlot> GetSlotAsync(CancellationToken cancellationToken);
}
