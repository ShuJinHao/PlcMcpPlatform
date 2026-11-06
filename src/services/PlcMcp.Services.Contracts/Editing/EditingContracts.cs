using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Exchange;
using PlcMcp.Services.Contracts.Batch;
using System.Text.Json;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.Services.Contracts.Editing;

public static class EditOperations
{
    public const string CreateProject = "project.create", SaveProject = "project.save", VerifyProject = "project.verify";
    public const string CreatePou = "pou.create", UpdatePou = "pou.update", CreateGvl = "gvl.create", UpdateGvl = "gvl.update", CreateDut = "dut.create", UpdateDut = "dut.update";
    public static bool Contains(string value) => value == BatchOperations.Apply || ExchangeOperations.IsWrite(value) || ConfigurationOperations.IsWrite(value) || value is CreateProject or SaveProject or CreatePou or UpdatePou or CreateGvl or UpdateGvl or CreateDut or UpdateDut;
    public static bool CreatesObject(string value) => value is CreatePou or CreateGvl or CreateDut;
    public static bool UpdatesObject(string value) => value is UpdatePou or UpdateGvl or UpdateDut;
}

public sealed record EditInput(string WorkspaceName, string? TemplateId = null, string? ParentId = null,
    string? ObjectId = null, string? Name = null, string? Declaration = null, string? Implementation = null,
    string? PouType = null, string? Language = null, string? DutType = null, string? BaseType = null, ConfigurationChange? Configuration = null, ExchangeChange? Exchange = null,
    BatchPlan? Batch = null, string? PlanSha256 = null);
public sealed record TemplateDefinition(string Id, string ProjectFile, string[] Files, string IdeProfile,
    string TargetController, string[] RequiredLibraries, string ExecutionMode, DeviceRequirement? Controller = null);
public sealed record DeviceRequirement(int Type, string Id, string Version);
public sealed record TemplateView(string Id, string? Revision, TemplateDefinition? Definition, string? ErrorCode);
public sealed record FileStamp(string Path, long Length, string Sha256);
public sealed record ProjectSnapshot(string Directory, string ProjectFile, string Revision, FileStamp[] Files, TemplateDefinition Template, DependencySnapshot? Dependencies = null);
public sealed record ManagedHead(string WorkspacePath, string WorkspaceName, ProjectSnapshot Snapshot, Guid JobId, bool IsDelivery);
public sealed record ChangeEvidence(Guid JobId, string WorkspacePath, string Stage, string BaseRevision,
    string? CandidatePath, string? BackupPath, string? VersionPath, string? ErrorCode, bool ExecutorStopped,
    WorkerResponse? Response, string[] Steps);
public sealed record EditResult(string WorkspaceName, string BaseRevision, string Revision, string RevisionScope,
    string ProjectPath, string? BackupPath, string? CandidatePath, bool Published, bool IsDelivery,
    string[] Steps, JsonElement? EngineeringResult, string DependencyValidation = "project_references_checked_installation_unverified",
    bool Simulated = false, DependencySnapshot? CurrentDependencies = null);
public sealed record WorkspaceInfo(string WorkspaceName, string Revision, string ProjectPath, bool IsDelivery,
    TemplateDefinition Template, FileStamp[] Files, string DependencyValidation = "declared_files_only", DependencySnapshot? CurrentDependencies = null);

public interface IManagedProjectFiles
{
    string WorkspacePath(string name);
    Task<TemplateView[]> ListTemplatesAsync(string mode, CancellationToken token);
    Task<ProjectSnapshot> ReadTemplateAsync(string templateId, string mode, CancellationToken token);
    Task VerifyAsync(ProjectSnapshot snapshot, CancellationToken token);
    Task<(ProjectSnapshot Candidate, string BackupPath)> PrepareAsync(string workspacePath, Guid jobId, ProjectSnapshot source, CancellationToken token);
    Task<ProjectSnapshot> SealAsync(string workspacePath, Guid jobId, ProjectSnapshot candidate, CancellationToken token);
    Task<string> ProjectFileRevisionAsync(ProjectSnapshot snapshot, CancellationToken token);
}
public interface IProjectChangeStore
{
    Task<ManagedHead?> GetHeadAsync(string workspacePath, CancellationToken token);
    Task<ChangeEvidence?> GetChangeAsync(Guid jobId, CancellationToken token);
    Task RecordAsync(ChangeEvidence evidence, CancellationToken token, string? expectedStage = null);
    Task<bool> PublishAsync(ManagedHead head, ChangeEvidence evidence, bool creating, CancellationToken token);
}
public sealed class ProjectEditException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
public interface IProjectReconciliation
{
    Task<Result<PlcMcp.Services.Contracts.Execution.JobView>> ReconcileAsync(Guid jobId, CancellationToken token);
}
