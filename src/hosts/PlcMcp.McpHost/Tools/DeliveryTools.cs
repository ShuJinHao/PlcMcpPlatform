using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PlcMcp.EngineeringService.Batch;
using PlcMcp.EngineeringService.Delivery;
using PlcMcp.Execution;
using PlcMcp.Services.Contracts.Batch;
using PlcMcp.Services.Contracts.Delivery;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Exchange;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.McpHost.Tools;

[McpServerToolType]
public sealed class DeliveryTools(BatchService batches, DeliveryService delivery, IExchangeUploads uploads,
    IDeliveryArtifacts packages, IBatchJournal journals, IJobStore jobs, IEngineeringExecutor executor, ExecutionSignal signal)
{
    [McpServerTool(Name = "plc_project_batch_preview", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(EditingQueryReply<BatchPreview>))]
    [Description("Preview up to 32 explicit engineering steps: POU/GVL/DUT, graph-v1, text.patch, member.create/update, folder.create, object.rename/move/delete, task configuration/create/update/delete/calls, library.add/remove/placeholder.set, device.add/remove/parameter.write/io.map. Program operations stay in applicationId; devices require deviceScopeIds, external library managers require libraryManagerIds. Parent/object IDs and task-call pouId may use @stepId; source text is never interpolated. Member patches specify memberKind, Action also pins expectedTypeId, property accessors use Get/Set. Hashes cover full normalized source; oldText must match uniquely. Preview is static and does not open or validate the IDE.")]
    public Task<CallToolResult> Preview(string workspaceName, string expectedRevision, BatchPlan plan, CancellationToken cancellationToken)
        => Query(() => batches.PreviewAsync(workspaceName, expectedRevision, plan, cancellationToken));

    [McpServerTool(Name = "plc_project_batch_apply", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Execute a previewed plan in one managed candidate. Resolve prior-step identities and refresh the object tree after each save/reopen. Shared device/library writes require task checks and compilation of every Application; other plans compile the chosen Application. Publish once. Failure preserves the old head; unknown outcomes are never automatically resumed.")]
    public Task<CallToolResult> Apply(string workspaceName, string expectedRevision, BatchPlan plan, string expectedPlanSha256, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 600)
        => Submit(batches.ApplyAsync(workspaceName, expectedRevision, plan, expectedPlanSha256, idempotencyKey, timeoutSeconds, cancellationToken));

    [McpServerTool(Name = "plc_batch_get", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(EditingQueryReply<BatchProgress>))]
    [Description("Read durable batch progress together with the enclosing job. Journal state verified is internal evidence, not proof that the managed version has published. This does not resume or unlock an unknown job.")]
    public Task<CallToolResult> Progress(Guid jobId, CancellationToken cancellationToken) => Query(async () =>
    {
        var job = await jobs.GetAsync(jobId, cancellationToken) ?? throw new ProjectEditException("job_not_found");
        if (job.Operation != BatchOperations.Apply) throw new ProjectEditException("not_a_batch_job");
        return new BatchProgress(job, await journals.ReadAsync(jobId, cancellationToken));
    });

    [McpServerTool(Name = "plc_exchange_upload_begin", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(EditingQueryReply<UploadSession>))]
    [Description("Begin or resume a bounded XML upload using a stable key, UTF-8 byte length and SHA-256. Default capacity is 8 MiB, configurable up to 64 MiB. No project is modified.")]
    public Task<CallToolResult> Begin(string uploadKey, string format, int byteLength, string sha256, CancellationToken cancellationToken)
        => Query(() => uploads.BeginAsync(uploadKey, format, byteLength, sha256, cancellationToken));

    [McpServerTool(Name = "plc_exchange_upload_append", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(EditingQueryReply<UploadSession>))]
    [Description("Append at an exact byte offset, at most 32768 decoded bytes in base64. Retrying an already stored identical chunk is safe; mismatching data or offsets are errors. Offsets are bytes, not UTF-16 characters.")]
    public Task<CallToolResult> Append(string uploadId, int offset, string base64, CancellationToken cancellationToken)
        => Query(() => uploads.AppendAsync(uploadId, offset, base64, cancellationToken));

    [McpServerTool(Name = "plc_exchange_upload_complete", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(EditingQueryReply<UploadSession>))]
    [Description("Verify complete upload length, SHA-256, strict UTF-8 and bounded XML structure, then create an immutable artifact. This does not import into an IDE or prove vendor compatibility.")]
    public Task<CallToolResult> Complete(string uploadId, CancellationToken cancellationToken)
        => Query(() => uploads.CompleteAsync(uploadId, cancellationToken));

    [McpServerTool(Name = "plc_exchange_upload_abort", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(EditingQueryReply<bool>))]
    [Description("Remove upload staging data and its resume receipt. Completed immutable artifacts and project versions remain available.")]
    public Task<CallToolResult> Abort(string uploadId, CancellationToken cancellationToken) => Query(async () => { await uploads.AbortAsync(uploadId, cancellationToken); return true; });

    [McpServerTool(Name = "plc_delivery_create", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Package an exact managed revision with project files, hashes, dependencies, task bindings and successful compilation evidence from a private copy. Does not advance the workspace head. Fixture evidence remains simulated; vendor evidence remains unverified.")]
    public Task<CallToolResult> Create(string workspaceName, string expectedRevision, string applicationId, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 600)
        => Submit(delivery.CreateAsync(workspaceName, expectedRevision, applicationId, idempotencyKey, timeoutSeconds, cancellationToken));

    [McpServerTool(Name = "plc_delivery_read", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(EditingQueryReply<DeliveryChunk>))]
    [Description("Read ZIP bytes in bounded base64 chunks. Verify each chunk and the complete SHA-256 from the create result before accepting the downloaded file. No arbitrary server path is accepted.")]
    public Task<CallToolResult> Read(string artifactId, CancellationToken cancellationToken, long offset = 0, int maxBytes = 32768)
        => Query(() => packages.ReadAsync(artifactId, offset, maxBytes, cancellationToken));

    private async Task<CallToolResult> Query<T>(Func<Task<T>> action)
    {
        Result<T> result;
        try { result = Result<T>.Success(await action()); }
        catch (ProjectEditException e) { result = Result<T>.Failure(e.Code, "Engineering request could not be completed."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { result = Result<T>.Failure("engineering_data_unavailable", "Engineering data is unavailable or invalid."); }
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
