using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PlcMcp.EngineeringService.Editing;
using PlcMcp.Execution;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;
namespace PlcMcp.McpHost.Tools;

public sealed record EditingQueryReply<T>(string ContractVersion, string ExecutionMode, string EvidenceKind, bool EngineeringReady, T? Value, OperationError? Error);

[McpServerToolType]
public sealed class EditingTools(ManagedEditingJobs jobs, IManagedProjectFiles files, IEngineeringExecutor executor, ExecutionSignal signal)
{
    [McpServerTool(Name = "plc_templates_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(EditingQueryReply<TemplateView[]>))]
    [Description("List registered templates and declared-file revisions. IDE profile, controller and libraries are declared requirements, not verified installed dependencies.")]
    public async Task<CallToolResult> Templates(CancellationToken cancellationToken)
    {
        try { return Query(Result<TemplateView[]>.Success(await files.ListTemplatesAsync(executor.ExecutionMode, cancellationToken))); }
        catch (ProjectEditException error) { return Query(Result<TemplateView[]>.Failure(error.Code, "Template catalog could not be read.")); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return Query(Result<TemplateView[]>.Failure("template_unavailable", "Template catalog could not be read.")); }
    }
    [McpServerTool(Name = "plc_workspace_get", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(EditingQueryReply<WorkspaceInfo>))]
    [Description("Get the managed workspace's current manifest revision and immutable project path. Use this revision for editing; file-read revisions are a separate scope.")]
    public async Task<CallToolResult> Workspace(string workspaceName, CancellationToken cancellationToken) => Query(await jobs.GetWorkspaceAsync(workspaceName, cancellationToken));

    [McpServerTool(Name = "plc_project_create", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Create a managed workspace from a registered template or registered existing-project copy. Verify/save/reopen in a candidate; never overwrite the source. Fixture files are simulated.")]
    public Task<CallToolResult> CreateProject(string workspaceName, string templateId, string idempotencyKey, CancellationToken cancellationToken, string? expectedTemplateRevision = null, int timeoutSeconds = 120)
        => Submit(EditOperations.CreateProject, new(workspaceName, TemplateId: templateId), idempotencyKey, expectedTemplateRevision, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_pou_create", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Create Program/FunctionBlock/Function under a parent GUID, save and reopen a new workspace version. ST/IL accept text; LD/FBD/SFC/CFC creation leaves graphical content empty. Function needs returnType; SFC Function is invalid.")]
    public Task<CallToolResult> CreatePou(string workspaceName, string expectedRevision, string parentId, string name, string pouType, string language, string declaration, string idempotencyKey,
        CancellationToken cancellationToken, string? implementation = null, string? returnType = null, int timeoutSeconds = 120)
        => Submit(EditOperations.CreatePou, new(workspaceName, ParentId: parentId, Name: name, PouType: pouType, Language: language, Declaration: declaration, Implementation: implementation, BaseType: returnType), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_pou_update", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Replace explicit declaration and/or textual implementation by GUID in an isolated candidate. Null leaves that part unchanged. Save/readback/reopen must succeed before publishing the new revision. Graphical networks cannot be replaced with text.")]
    public Task<CallToolResult> UpdatePou(string workspaceName, string expectedRevision, string objectId, string idempotencyKey, CancellationToken cancellationToken,
        string? declaration = null, string? implementation = null, int timeoutSeconds = 120)
        => Submit(EditOperations.UpdatePou, new(workspaceName, ObjectId: objectId, Declaration: declaration, Implementation: implementation), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_gvl_create", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Create a GVL with a complete VAR_GLOBAL declaration in a managed version; read back after save and reopen.")]
    public Task<CallToolResult> CreateGvl(string workspaceName, string expectedRevision, string parentId, string name, string declaration, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 120)
        => Submit(EditOperations.CreateGvl, new(workspaceName, ParentId: parentId, Name: name, Declaration: declaration), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);
    [McpServerTool(Name = "plc_gvl_update", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Replace a GVL declaration by GUID, preserving earlier immutable versions.")]
    public Task<CallToolResult> UpdateGvl(string workspaceName, string expectedRevision, string objectId, string declaration, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 120)
        => Submit(EditOperations.UpdateGvl, new(workspaceName, ObjectId: objectId, Declaration: declaration), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);
    [McpServerTool(Name = "plc_dut_create", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Create a Structure/Enumeration/Alias/Union DUT with a complete TYPE declaration. Alias requires baseType; array aliases can use an ARRAY type expression.")]
    public Task<CallToolResult> CreateDut(string workspaceName, string expectedRevision, string parentId, string name, string dutType, string declaration, string idempotencyKey, CancellationToken cancellationToken, string? baseType = null, int timeoutSeconds = 120)
        => Submit(EditOperations.CreateDut, new(workspaceName, ParentId: parentId, Name: name, Declaration: declaration, DutType: dutType, BaseType: baseType), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);
    [McpServerTool(Name = "plc_dut_update", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Replace a DUT declaration by GUID and verify persistence after reopening the candidate.")]
    public Task<CallToolResult> UpdateDut(string workspaceName, string expectedRevision, string objectId, string declaration, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 120)
        => Submit(EditOperations.UpdateDut, new(workspaceName, ObjectId: objectId, Declaration: declaration), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_project_save", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Finalize a new delivery snapshot: save and reopen a copy of the current managed version. Earlier edits already persist to working versions. Delivery does not imply compilation, task binding or PLC runtime correctness.")]
    public Task<CallToolResult> Save(string workspaceName, string expectedRevision, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 120)
        => Submit(EditOperations.SaveProject, new(workspaceName), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_job_reconcile", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Reconcile an unknown edit only from durable stopped-executor evidence and matching files. Does not rerun scripts, overwrite files or force-unlock an unproven outcome.")]
    public async Task<CallToolResult> Reconcile(Guid jobId, CancellationToken cancellationToken)
    {
        var result = await jobs.ReconcileAsync(jobId, cancellationToken);
        if (result.IsSuccess) signal.Pulse();
        return Reply(result);
    }
    private async Task<CallToolResult> Submit(string operation, EditInput input, string key, string? revision, int timeout, CancellationToken token)
    {
        var result = await jobs.SubmitAsync(operation, input, key, revision, timeout, token);
        if (result.IsSuccess) signal.Pulse();
        return Reply(result);
    }
    private CallToolResult Reply(Result<JobView> result) => Serialize(new JobToolReply("1.1", executor.ExecutionMode, EvidenceKind, false, result.Value, result.Error), result.Error is not null);
    private CallToolResult Query<T>(Result<T> result) => Serialize(new EditingQueryReply<T>("1.1", executor.ExecutionMode, EvidenceKind, false, result.Value, result.Error), result.Error is not null);
    private string EvidenceKind => executor.ExecutionMode == "fixture" ? "simulated" : "unverified";
    private static CallToolResult Serialize<T>(T reply, bool error)
    {
        var json = JsonSerializer.SerializeToElement(reply, WorkerJson.Options);
        return new() { IsError = error, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
