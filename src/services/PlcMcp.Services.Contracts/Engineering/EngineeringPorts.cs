using PlcMcp.SharedKernel.Results;

namespace PlcMcp.Services.Contracts.Engineering;

// P1 契约草案：尚无生产实现，未注册为 MCP 工具。
public sealed record EngineeringContext(Guid WorkspaceId, string IdeVersion, string ExpectedRevision);
public sealed record ProjectObject(string Id, string Name, string Kind, string? ParentId, string? Language);
public sealed record ProjectSnapshot(string Revision, IReadOnlyList<ProjectObject> Objects, bool Truncated);
public sealed record ProgramSource(string ObjectId, string Language, string Declaration, string Implementation, string Revision);
public sealed record CompileDiagnostic(string Severity, string Code, string Message, string? ObjectId, int? Line, int? Column);
public sealed record CompileReport(bool Succeeded, string ProjectRevision, IReadOnlyList<CompileDiagnostic> Diagnostics);
public interface IPlcProjectReader
{
    Task<Result<ProjectSnapshot>> ReadAsync(EngineeringContext context, CancellationToken cancellationToken);
}
public interface IPlcProgramReader
{
    Task<Result<ProgramSource>> ReadAsync(EngineeringContext context, string objectId, CancellationToken cancellationToken);
}
public interface IPlcCompiler
{
    Task<Result<CompileReport>> CompileAsync(EngineeringContext context, CancellationToken cancellationToken);
}
