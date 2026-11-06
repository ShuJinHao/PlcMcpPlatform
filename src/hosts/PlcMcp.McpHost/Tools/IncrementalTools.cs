using System.ComponentModel;
using PlcMcp.Services.Contracts.Configuration;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PlcMcp.EngineeringService.Editing;
using PlcMcp.EngineeringService.Incremental;
using PlcMcp.Execution;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Incremental;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.McpHost.Tools;

[McpServerToolType]
public sealed class IncrementalTools(IncrementalService incremental, ManagedEditingJobs edits, IEngineeringExecutor executor, ExecutionSignal signal)
{
    [McpServerTool(Name = "plc_project_adopt_preview", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(EditingQueryReply<AdoptionPreview>))]
    [Description("Hash an existing saved project and explicitly listed companion files from WorkspaceRoot/imports/sourceName. Returns an a1 source revision for adoption. Unsaved external IDE state and undeclared dependencies are outside this inventory. No IDE is opened and no file is changed.")]
    public Task<CallToolResult> AdoptPreview(AdoptionSource source, CancellationToken cancellationToken)
        => Query(() => incremental.PreviewAdoptionAsync(source, cancellationToken));

    [McpServerTool(Name = "plc_project_adopt", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Enroll a saved project as a new managed workspace. Requires the exact source revision from adopt_preview. Copy only declared files, check source before and after copying, validate dependencies, save/reopen the private candidate and publish an initial m2 revision. Original files remain untouched; this does not compile or certify PLC behavior.")]
    public Task<CallToolResult> Adopt(string workspaceName, AdoptionSource source, string expectedSourceRevision, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 600)
        => Submit(edits.SubmitAsync(IncrementalOperations.Adopt, new(workspaceName, Adoption: source), idempotencyKey, expectedSourceRevision, timeoutSeconds, cancellationToken));

    [McpServerTool(Name = "plc_project_context", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Read revision-bound Application engineering data (legacy query name; no AI session management) from a private copy: object IDs, source windows and full normalized SHA-256, task bindings and library references. Optional objectId is returned first; remaining Application objects, including GVL/DUT, follow across pages. Offset/pageSize select objects; textOffset/maxTextChars select UTF-16 source windows. Follow nextOffset and truncation flags. Explicit references are not a complete call graph. Read completion through plc_job_get.")]
    public Task<CallToolResult> Context(string workspaceName, string expectedHeadRevision, string revision, string applicationId, string idempotencyKey,
        CancellationToken cancellationToken, string? objectId = null, int offset = 0, int pageSize = 8, int textOffset = 0, int maxTextChars = 4096, int timeoutSeconds = 600)
        => Submit(incremental.ReadAsync(IncrementalOperations.Context, expectedHeadRevision,
            new(workspaceName, revision, applicationId, objectId, offset, pageSize, textOffset, maxTextChars), idempotencyKey, timeoutSeconds, cancellationToken));

    [McpServerTool(Name = "plc_project_objects_read", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Read actual engineering data at an immutable revision, not AI conversation context. collection: objects (subtree/one object), members (direct children of parentId), tasks (explicit task bindings), libraries (selected Application and shared references). Returns source windows with full hashes and actual member/accessor identities. For Action classification use objectId plus sourceQuery with observed type GUID. Follow nextCursor with the same query/head/revision; cursor cannot be mixed with a different scope. Read the result with plc_job_get.")]
    public Task<CallToolResult> Objects(string workspaceName, string expectedHeadRevision, string revision, string applicationId, string idempotencyKey,
        CancellationToken cancellationToken, string collection = "objects", string? parentId = null, string? objectId = null,
        ConfigurationQuery? sourceQuery = null, string? cursor = null, int pageSize = 8, int textOffset = 0, int maxTextChars = 4096, int timeoutSeconds = 600)
        => Submit(incremental.ReadAsync(IncrementalOperations.Query, expectedHeadRevision,
            new(workspaceName, revision, applicationId, objectId, PageSize: pageSize, TextOffset: textOffset, MaxTextChars: maxTextChars,
                Collection: collection, ParentId: parentId, SourceQuery: sourceQuery, Cursor: cursor), idempotencyKey, timeoutSeconds, cancellationToken));

    [McpServerTool(Name = "plc_project_history", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(EditingQueryReply<HistoryPage>))]
    [Description("List published immutable revisions in chronological order, bound to an exact current head so concurrent publication cannot silently change pagination. Includes pre-P6 history. Failed candidates are job evidence, not historical published versions.")]
    public Task<CallToolResult> History(string workspaceName, string expectedHeadRevision, CancellationToken cancellationToken, int offset = 0, int pageSize = 32)
        => Query(() => incremental.HistoryAsync(workspaceName, expectedHeadRevision, offset, pageSize, cancellationToken));

    [McpServerTool(Name = "plc_project_diff", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Queue a comparison of two published revisions. File changes are exact hashes only. Optional objectId plus applicationId reads source before/after with hashes and bounded windows; graph=true compares supported graph-v1/v2 types, return values, variables, nodes, in/out pins, label connections, SFC branches, actions and ordering. Use toObjectId when XML replacement changed the vendor object GUID. Graphs outside the graph profiles return an explicit error. Offset/pageSize page file and graph changes; read completion through plc_job_get.")]
    public Task<CallToolResult> Diff(string workspaceName, string expectedHeadRevision, string fromRevision, string toRevision, string idempotencyKey,
        CancellationToken cancellationToken, string? applicationId = null, string? objectId = null, bool graph = false, int offset = 0, int pageSize = 8,
        int textOffset = 0, int maxTextChars = 4096, int timeoutSeconds = 600, string? toObjectId = null)
        => Submit(incremental.ReadAsync(IncrementalOperations.Diff, expectedHeadRevision,
            new(workspaceName, fromRevision, applicationId, objectId, offset, pageSize, textOffset, maxTextChars, toRevision, graph, toObjectId), idempotencyKey, timeoutSeconds, cancellationToken));

    [McpServerTool(Name = "plc_project_restore", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Restore historical project content by publishing a NEW revision, preserving every existing version. Requires the exact current head and an intact historical manifest. Validate historical dependencies, save/reopen a candidate, check tasks and compile every Application before publication. Failed or unknown execution never advances the head; unknown outcomes stay quarantined.")]
    public Task<CallToolResult> Restore(string workspaceName, string expectedHeadRevision, string sourceRevision, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 600)
        => Submit(edits.SubmitAsync(IncrementalOperations.Restore, new(workspaceName, RestoreRevision: sourceRevision), idempotencyKey, expectedHeadRevision, timeoutSeconds, cancellationToken));

    private async Task<CallToolResult> Query<T>(Func<Task<T>> action)
    {
        Result<T> result;
        try { result = Result<T>.Success(await action()); }
        catch (ProjectEditException e) { result = Result<T>.Failure(e.Code, "Managed engineering query could not be completed."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { result = Result<T>.Failure("project_unavailable", "Project data is unavailable or invalid."); }
        return Serialize(new EditingQueryReply<T>("1.6", executor.ExecutionMode, executor.ExecutionMode == "fixture" ? "simulated" : "unverified", false, result.Value, result.Error), result.Error is not null);
    }
    private async Task<CallToolResult> Submit(Task<Result<JobView>> task)
    {
        var result = await task; if (result.IsSuccess) signal.Pulse();
        return Serialize(new JobToolReply("1.6", executor.ExecutionMode, executor.ExecutionMode == "fixture" ? "simulated" : "unverified", false, result.Value, result.Error), result.Error is not null);
    }
    private static CallToolResult Serialize<T>(T value, bool error)
    {
        var json = JsonSerializer.SerializeToElement(value, WorkerJson.Options);
        return new() { IsError = error, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
