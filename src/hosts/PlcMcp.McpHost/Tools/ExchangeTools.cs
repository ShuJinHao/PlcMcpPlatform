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
    [Description("Generate original PLCopen TC6 XML from graph-v1 LD/FBD/basic-SFC nodes, variables, named block pins and connections. Return an artifact and semantic hash. Does not run an IDE or PLC compiler.")]
    public Task<CallToolResult> Generate(GraphicalProgram program, CancellationToken cancellationToken)
        => Query(() => exchange.GenerateAsync(program, cancellationToken));

    [McpServerTool(Name = "plc_graph_decode", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(EditingQueryReply<GraphDocument>))]
    [Description("Decode exactly one graph-v1 POU from an artifact. Refuse unrepresented extensions or program data; the original artifact remains available.")]
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
    [Description("Read graph-v1 LD/FBD/basic-SFC content from an actual POU via PLCopen export. Return nodes, variables and semantic hash. Unsupported extensions are explicit errors; use project export for opaque exchange.")]
    public Task<CallToolResult> ReadGraph(string projectPath, string objectId, string idempotencyKey, CancellationToken cancellationToken, string? expectedRevision = null, int timeoutSeconds = 120)
        => Submit(jobs.SubmitAsync(new(ExchangeOperations.ReadGraph, new(projectPath, ObjectId: objectId), idempotencyKey, expectedRevision, timeoutSeconds), cancellationToken));

    [McpServerTool(Name = "plc_pou_graph_write", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Create or replace a complete graph-v1 POU from an artifact in a managed candidate. Updating requires objectId and expectedGraphSha256 from graph read. Name/type/language cannot change; unknown properties cannot be discarded. Readback verifies logic before publication.")]
    public Task<CallToolResult> WriteGraph(string workspaceName, string expectedRevision, string parentId, string artifactId, string idempotencyKey, CancellationToken cancellationToken,
        string? objectId = null, string? expectedGraphSha256 = null, int timeoutSeconds = 120)
        => Submit(exchange.ImportAsync(workspaceName, expectedRevision, parentId, artifactId, objectId is null ? "reject" : "replace", false, idempotencyKey,
            timeoutSeconds, cancellationToken, graph: true, objectId: objectId, expectedGraphSha256: expectedGraphSha256));

    private string Evidence => executor.ExecutionMode == "fixture" ? "simulated" : "unverified";
    private async Task<CallToolResult> Query<T>(Func<Task<T>> action)
    {
        Result<T> result;
        try { result = Result<T>.Success(await action()); }
        catch (ProjectEditException e) { result = Result<T>.Failure(e.Code, "The exchange request could not be completed."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { result = Result<T>.Failure("artifact_unavailable", "The exchange artifact is unavailable."); }
        return Serialize(new EditingQueryReply<T>("1.3", executor.ExecutionMode, Evidence, false, result.Value, result.Error), result.Error is not null);
    }
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
