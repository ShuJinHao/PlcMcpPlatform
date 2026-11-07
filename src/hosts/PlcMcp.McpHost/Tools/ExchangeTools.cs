using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PlcMcp.Core.Workspaces;
using PlcMcp.EngineeringService.Exchange;
using PlcMcp.EngineeringService.Execution;
using PlcMcp.Execution;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Exchange;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.McpHost.Tools;

[McpServerToolType]
public sealed class ExchangeTools(ExchangeService exchange, EngineeringJobs jobs, IEngineeringExecutor executor, ExecutionSignal signal)
{
    [McpServerTool(Name = "plc_exchange_stage", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(EditingQueryReply<ExchangeArtifact>))]
    [Description("Stage bounded UTF-8 XML as an immutable artifact. Format is plcopen or codesys-native. Reject DTD/entities. Structure checks are not IDE validation or compilation.")]
    public Task<CallToolResult> Stage(string format, string xml, CancellationToken cancellationToken)
        => Query(() => exchange.StageAsync(format, xml, cancellationToken));

    [McpServerTool(Name = "plc_exchange_read", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(EditingQueryReply<ExchangeChunk>))]
    [Description("Read an exchange artifact in bounded chunks; offsets are UTF-16 code units. Use returned nextOffset. Hash verification occurs on every read.")]
    public Task<CallToolResult> Read(string artifactId, CancellationToken cancellationToken, int offset = 0, int maxChars = 4096)
        => Query(() => exchange.ReadAsync(artifactId, offset, maxChars, cancellationToken));

    [McpServerTool(Name = "plc_exchange_validate", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(EditingQueryReply<ExchangeInspection>))]
    [Description("Inspect format, POU names, languages, extensions and structural limits. This is not full XSD validation, dependency resolution or compilation.")]
    public Task<CallToolResult> Validate(string artifactId, CancellationToken cancellationToken)
        => Query(() => exchange.InspectAsync(artifactId, cancellationToken));

    [McpServerTool(Name = "plc_graph_generate", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(EditingQueryReply<GeneratedGraph>))]
    [Description("Generate original PLCopen TC6 XML. Default graph-v1 stays compatible; graph-v2 adds Function returnType, arrays/stringLength, labels, inOuts and structured SFC. Types accept enumValues [{name,value?}] (type=ENUM for implicit INT base, or explicit integer base) or subrange {lower,upper} with decimal string bounds; these are mutually exclusive. A variable's initializer supports kind=array with items(value,repeat) or kind=struct with members(name,value); nested leaves use kind=simple/value. Do not also set initialValue; arrays may be partially initialized but must fit declared capacity. graph-v2 variableGroups reference every variable name exactly once with matching scope; group order/boundaries, constant, retention(default/retain/nonRetain), name and plain documentation are preserved. Variables accept address and plain documentation; scopes include temp/external. Use empty pin name for non-block nodes. Connections belong on inputs, not outputs. Positive executionOrder is required on block/coil/outVariable and forbidden on inVariable/rails/SFC nodes. SFC step/transition have one output; jumpStep/actionBlock have none. An actionBlock has one input from its owning step and associations [{id,action,qualifier:N}]; the referenced action must be in program.actions as {name,implementation}. Omit unused optional fields; do not send null for qualifier/edge/storage. Do not combine associations with legacy action/qualifier/duration. Association list order is preserved, not a runtime execution guarantee. Target write checks apply Inovance restrictions separately. Selection output order, priorities 1..N and left-to-right X must agree. Return artifact and semantic hash; no IDE/compiler is run.")]
    public Task<CallToolResult> Generate(GraphicalProgram program, CancellationToken cancellationToken)
        => Query(() => exchange.GenerateAsync(program, cancellationToken));

    [McpServerTool(Name = "plc_graph_decode", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(EditingQueryReply<GraphDocument>))]
    [Description("Decode exactly one graph-v1/v2 POU from an artifact. Refuse unrepresented extensions or program data; the original artifact remains available.")]
    public Task<CallToolResult> Decode(string artifactId, CancellationToken cancellationToken)
        => Query(() => exchange.DecodeAsync(artifactId, cancellationToken));

    [McpServerTool(Name = "plc_project_export", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Export selected GUIDs, subtrees or [project] through the official IDE API. Format plcopen or codesys-native. Results include an artifact, exact selection and reporter messages; no arbitrary output paths.")]
    public Task<CallToolResult> Export(string projectPath, string[] objectIds, string format, string idempotencyKey, CancellationToken cancellationToken,
        bool recursive = true, string? expectedRevision = null, int timeoutSeconds = 120)
        => Submit(jobs.SubmitAsync(new(ExchangeOperations.Export, new(projectPath, Exchange: new(format, objectIds, recursive)), idempotencyKey, expectedRevision, timeoutSeconds), cancellationToken));

    [McpServerTool(Name = "plc_project_import", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Import a staged artifact into a managed candidate at a parent GUID or project. Conflict policy reject/replace/skip is explicit. Save/reopen/re-export proof precedes publication. Unknown/partial failures never publish; warnings require allowWarnings.")]
    public Task<CallToolResult> Import(string workspaceName, string expectedRevision, string parentId, string artifactId, string idempotencyKey, CancellationToken cancellationToken,
        string conflictPolicy = "reject", bool allowWarnings = false, int timeoutSeconds = 120)
        => Submit(exchange.ImportAsync(workspaceName, expectedRevision, parentId, artifactId, conflictPolicy, allowWarnings, idempotencyKey, timeoutSeconds, cancellationToken));

    [McpServerTool(Name = "plc_pou_graph_read", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Read graph-v1/v2 LD/FBD/SFC content from an actual POU via PLCopen export. Return nodes, variables and semantic hash. Unsupported extensions are explicit errors; use project export for opaque exchange.")]
    public Task<CallToolResult> ReadGraph(string projectPath, string objectId, string idempotencyKey, CancellationToken cancellationToken, string? expectedRevision = null, int timeoutSeconds = 120)
        => Submit(jobs.SubmitAsync(new(ExchangeOperations.ReadGraph, new(projectPath, ObjectId: objectId), idempotencyKey, expectedRevision, timeoutSeconds), cancellationToken));

    [McpServerTool(Name = "plc_pou_graph_write", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Create or replace a complete graph-v1/v2 POU from an artifact in a managed candidate. Updating requires objectId and expectedGraphSha256 from graph read. Name/type/language cannot change; unknown properties cannot be discarded. Readback verifies logic before publication.")]
    public Task<CallToolResult> WriteGraph(string workspaceName, string expectedRevision, string parentId, string artifactId, string idempotencyKey, CancellationToken cancellationToken,
        string? objectId = null, string? expectedGraphSha256 = null, int timeoutSeconds = 120)
        => Submit(exchange.ImportAsync(workspaceName, expectedRevision, parentId, artifactId, objectId is null ? "reject" : "replace", false, idempotencyKey,
            timeoutSeconds, cancellationToken, graph: true, objectId: objectId, expectedGraphSha256: expectedGraphSha256));

    private string Evidence => executor.ExecutionMode == "fixture" ? "simulated" : "unverified";
    private async Task<CallToolResult> Query<T>(Func<Task<T>> action)
    {
        Result<T> result;
        try { result = Result<T>.Success(await action()); }
        catch (ProjectEditException e) { result = Result<T>.Failure(e.Code, Explain(e.Code)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { result = Result<T>.Failure("artifact_unavailable", "The exchange artifact is unavailable."); }
        return Serialize(new EditingQueryReply<T>("1.3", executor.ExecutionMode, Evidence, false, result.Value, result.Error), result.Error is not null);
    }
    private static string Explain(string code) => code switch
    {
        "graph_execution_order_required" or "invalid_graph_block" => "Use a positive executionOrder on block, coil and outVariable nodes. Omit executionOrder on inVariable, rails and all SFC nodes. Blocks also need typeName and named pins.",
        "graph_field_not_applicable" => "A field was supplied on the wrong node kind. executionOrder is allowed only on block/coil/outVariable; name only on step/jumpStep/connector/continuation; action/associations/qualifier/duration only on actionBlock. Omit unused fields instead of null.",
        "invalid_graph_ports" => "Use unnamed pins for non-block nodes. inVariable/leftPowerRail: no input, one output; outVariable/rightPowerRail/jumpStep/actionBlock: one input, no output; contact/coil/transition: one input and one output; initial step: zero or one input, one output.",
        "invalid_sfc_action" or "invalid_sfc_action_associations" => "An actionBlock needs action plus qualifier N/S/R/P or associations [{id:1,action:'ActionName',qualifier:'N'}]. Include matching {name:'ActionName',implementation:'ST code'} in program.actions. Do not supply both forms or null qualifier.",
        "invalid_graph_connection" or "invalid_graph_pin" => "Put connections [{sourceId:nodeId,sourcePin:optionalNamedOutput}] on input pins only; source nodes must expose the referenced output. Non-block pins use name ''. Each ordinary FBD/SFC input has one connection.",
        "invalid_graph_variables" => "Check unique variable names, IEC types, scope and initial values. enumValues and subrange are mutually exclusive; enum names must be unique; decimal bounds/values must fit their integer base type. graph-v1 does not accept extended declarations.",
        "graph_contains_unrepresented_data" => "The XML contains data outside the bounded graph model. Keep the original artifact and use opaque export/import; do not remove unknown properties to force a graph edit.",
        _ => "The exchange request could not be completed."
    };
    private async Task<CallToolResult> Submit(Task<Result<JobView>> task)
    {
        var result = await task; if (result.IsSuccess) signal.Pulse();
        return Serialize(new JobToolReply("1.3", executor.ExecutionMode, Evidence, false, result.Value, result.Error), result.Error is not null);
    }
    private static CallToolResult Serialize<T>(T value, bool error)
    {
        var json = JsonSerializer.SerializeToElement(value, WorkerJson.Options);
        return new() { IsError = error, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
