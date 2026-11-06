using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PlcMcp.EngineeringService.Execution;
using PlcMcp.Execution;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.McpHost.Tools;

public sealed record JobToolReply(string ContractVersion, string ExecutionMode, string EvidenceKind,
    bool EngineeringReady, JobView? Job, OperationError? Error);

[McpServerToolType]
public sealed class EngineeringTools(EngineeringJobs jobs, ExecutionSignal signal, IEngineeringExecutor executor)
{
    [McpServerTool(Name = "plc_project_open", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Submit a project-open check. Each operation owns and closes its session; this does not keep a GUI project open. Fixture mode is simulated.")]
    public Task<CallToolResult> Open(string projectPath, string idempotencyKey, CancellationToken cancellationToken,
        string? expectedRevision = null, int timeoutSeconds = 60) => Submit(new(EngineeringOperations.Open, new(projectPath), idempotencyKey, expectedRevision, timeoutSeconds), cancellationToken);

    [McpServerTool(Name = "plc_project_get_structure", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Queue a bounded project-tree read; poll its result. No project content is changed; a task record is created.")]
    public Task<CallToolResult> Structure(string projectPath, string idempotencyKey, CancellationToken cancellationToken,
        string? expectedRevision = null, int maxObjects = 256, int timeoutSeconds = 60) => Submit(new(EngineeringOperations.Structure, new(projectPath, MaxObjects: maxObjects), idempotencyKey, expectedRevision, timeoutSeconds), cancellationToken);

    [McpServerTool(Name = "plc_pou_read", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Queue reading a textual object's declaration and implementation by GUID. Graphical networks are not represented as source strings.")]
    public Task<CallToolResult> Read(string projectPath, string objectId, string idempotencyKey, CancellationToken cancellationToken,
        string? expectedRevision = null, int timeoutSeconds = 60) => Submit(new(EngineeringOperations.ReadProgram, new(projectPath, ObjectId: objectId), idempotencyKey, expectedRevision, timeoutSeconds), cancellationToken);

    [McpServerTool(Name = "plc_project_compile", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Queue compilation of an explicit Application GUID. Acceptance is not compilation success. Fixture diagnostics are simulated.")]
    public Task<CallToolResult> Compile(string projectPath, string applicationId, string idempotencyKey, CancellationToken cancellationToken,
        string? expectedRevision = null, int timeoutSeconds = 60) => Submit(new(EngineeringOperations.Compile, new(projectPath, ApplicationId: applicationId), idempotencyKey, expectedRevision, timeoutSeconds), cancellationToken);

    [McpServerTool(Name = "plc_job_get", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Read a durable job state. A successful status query does not mean the engineering operation succeeded.")]
    public async Task<CallToolResult> Get(Guid jobId, CancellationToken cancellationToken) => Reply(await jobs.GetAsync(jobId, cancellationToken));

    [McpServerTool(Name = "plc_job_cancel", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Request cancellation. CancelRequested does not confirm executor termination; terminal jobs retain their existing outcome.")]
    public async Task<CallToolResult> Cancel(Guid jobId, CancellationToken cancellationToken) => Reply(await jobs.CancelAsync(jobId, cancellationToken));

    [McpServerTool(Name = "plc_job_get_result", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Read a terminal result. Pending, failed, cancelled and unknown outcomes return isError=true, preserving available compile diagnostics.")]
    public async Task<CallToolResult> Result(Guid jobId, CancellationToken cancellationToken)
    {
        var result = await jobs.GetAsync(jobId, cancellationToken);
        if (!result.IsSuccess || result.Value!.Status == "Succeeded") return Reply(result);
        var pending = result.Value.Status is "Queued" or "Running" or "CancelRequested";
        return Reply(result, new(pending ? "job_not_finished" : result.Value.ErrorCode ?? "job_failed", pending ? "The job has not finished." : "The operation did not succeed; inspect the recorded result."));
    }
    private async Task<CallToolResult> Submit(Submission submission, CancellationToken token)
    {
        var result = await jobs.SubmitAsync(submission, token);
        if (result.IsSuccess) signal.Pulse();
        return Reply(result);
    }
    private CallToolResult Reply(Result<JobView> result, OperationError? overrideError = null)
    {
        var error = overrideError ?? result.Error;
        var reply = new JobToolReply("1.0", executor.ExecutionMode, executor.ExecutionMode == "fixture" ? "simulated" : "unverified", false, result.Value, error);
        var content = JsonSerializer.SerializeToElement(reply, WorkerJson.Options);
        return new() { IsError = error is not null, StructuredContent = content, Content = [new TextContentBlock { Text = content.GetRawText() }] };
    }
}
