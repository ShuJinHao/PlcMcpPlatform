using System.Text.Json;
using PlcMcp.Services.Contracts.Incremental;
using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Editing;

namespace PlcMcp.Services.Contracts.Batch;

public static class BatchOperations
{
    public const string Apply = "project.batch";
    public static readonly string[] Steps = [IncrementalOperations.Patch, EditOperations.CreateDut, EditOperations.UpdateDut, EditOperations.CreateGvl,
        EditOperations.UpdateGvl, EditOperations.CreatePou, EditOperations.UpdatePou, "pou.graph.write",
        ConfigurationOperations.TaskConfigurationCreate, ConfigurationOperations.TaskCreate, ConfigurationOperations.TaskUpdate, ConfigurationOperations.TaskCallsSet];
}
public sealed record BatchChange(string? ParentId = null, string? ObjectId = null, string? Name = null,
    string? Declaration = null, string? Implementation = null, string? PouType = null, string? Language = null,
    string? DutType = null, string? BaseType = null, ConfigurationChange? Configuration = null,
    string? ArtifactId = null, string? ExpectedGraphSha256 = null, TextPatch? Patch = null);
public sealed record BatchStep(string Id, string Operation, BatchChange Change);
public sealed record BatchPlan(string ApplicationId, BatchStep[] Steps);
public sealed record BatchPreview(string WorkspaceName, string BaseRevision, string PlanSha256, string ApplicationId,
    BatchStep[] Steps, string[] RequiredOperations, string[] Limitations);
public sealed record BatchStepResult(string Id, string Operation, string Status, string? ObjectId, string? ErrorCode,
    string? Revision, JsonElement? Details);
public sealed record BatchJournal(Guid JobId, string State, string? ActiveStep, BatchStepResult[] Steps, string? ErrorCode = null);
public sealed record BatchProgress(PlcMcp.Services.Contracts.Execution.JobView Job, BatchJournal? Journal);
public interface IBatchJournal
{
    Task WriteAsync(BatchJournal journal, CancellationToken token);
    Task<BatchJournal?> ReadAsync(Guid jobId, CancellationToken token);
}
