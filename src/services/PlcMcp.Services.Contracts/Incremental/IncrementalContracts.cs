using System.Text.Json;
using PlcMcp.Services.Contracts.Editing;

namespace PlcMcp.Services.Contracts.Incremental;

public static class IncrementalOperations
{
    public const string Adopt = "project.adopt", Restore = "project.restore", Context = "project.context", Diff = "project.diff", Patch = "text.patch", Source = "object.source.read";
    public static bool IsWrite(string op) => op is Adopt or Restore or Patch;
    public static bool IsRead(string op) => op is Context or Diff or Source;
}
// SourceName addresses only WorkspaceRoot/imports/<sourceName>. Files are explicit, relative paths.
public sealed record AdoptionSource(string SourceName, string ProjectFile, string[] Files, string IdeProfile,
    string TargetController, string[] RequiredLibraries, DeviceRequirement? Controller = null);
public sealed record AdoptionPreview(string SourceRevision, FileStamp[] Files, TemplateDefinition Requirements, string Scope = "saved_declared_files_only");
public sealed record TextPatch(string Section, string ExpectedSha256, string OldText, string NewText);
public sealed record IncrementalQuery(string WorkspaceName, string Revision, string? ApplicationId = null, string? ObjectId = null,
    int Offset = 0, int PageSize = 8, int TextOffset = 0, int MaxTextChars = 4096, string? CompareRevision = null, bool Graph = false);
public sealed record VersionRecord(ChangeEvidence Evidence, string Operation, DateTimeOffset CreatedAt);
public sealed record VersionView(string Revision, Guid JobId, string Operation, DateTimeOffset CreatedAt, string BaseRevision, bool IsCurrent);
public sealed record HistoryPage(string WorkspaceName, string HeadRevision, VersionView[] Versions, int? NextOffset);
public interface IProjectHistoryStore
{
    Task<VersionRecord[]> ListAsync(string workspacePath, int offset, int count, CancellationToken token);
    Task<VersionRecord?> GetAsync(string workspacePath, string revision, CancellationToken token);
}
public sealed record TextWindow(string Sha256, int Length, int Offset, string Text, int? NextOffset);
public sealed record ContextObject(string ObjectId, string? ParentId, string Name, string Kind, string? TypeId,
    string ImplementationKind, TextWindow? Declaration, TextWindow? Implementation);
public sealed record ContextPage(string WorkspaceName, string HeadRevision, string Revision, string ApplicationId,
    ContextObject[] Objects, int TotalObjects, int? NextOffset, JsonElement Tasks, JsonElement Libraries,
    string ReferenceScope = "explicit_task_bindings_and_library_references_only");
public sealed record FileDifference(string Path, FileStamp? Before, FileStamp? After);
public sealed record ProjectDiff(string WorkspaceName, string HeadRevision, string FromRevision, string ToRevision,
    FileDifference[] Files, int TotalFileChanges, int? NextOffset, JsonElement? ObjectDifference,
    string FileComparison = "byte_hash_only_not_vendor_semantic_equivalence");
