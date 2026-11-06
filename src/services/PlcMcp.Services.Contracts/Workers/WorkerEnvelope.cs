using System.Text.Json;

namespace PlcMcp.Services.Contracts.Workers;

/// <summary>v1 内部协议；跨运行时 Worker 按 JSON 实现，不引用本 net8.0 DLL。</summary>
public sealed record WorkerRequest(int ProtocolVersion, Guid RequestId, Guid JobId,
    string Operation, Guid WorkspaceId, string ExpectedRevision, DateTimeOffset DeadlineUtc,
    string ExecutionMode, JsonElement Payload);
public sealed record WorkerResponse(int ProtocolVersion, Guid RequestId, Guid JobId,
    string Outcome, string? ErrorCode, string ExecutionMode, JsonElement? Payload);

public static class WorkerJson
{
    public const int MaxRequestBytes = 131072;
    public const int MaxResponseBytes = 1048576;
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 32
    };
    public static WorkerResponse Unknown(WorkerRequest request, string code = "execution_outcome_unknown") =>
        new(1, request.RequestId, request.JobId, "unknown", code, request.ExecutionMode, null);
    public static WorkerResponse Failed(WorkerRequest request, string code) =>
        new(1, request.RequestId, request.JobId, "failed", code, request.ExecutionMode, null);
    public static bool Matches(WorkerRequest request, WorkerResponse response) =>
        response.ProtocolVersion == 1 && response.RequestId == request.RequestId && response.JobId == request.JobId
        && response.ExecutionMode == request.ExecutionMode
        && response.Outcome is "succeeded" or "failed" or "cancelled" or "unknown"
        && (response.Outcome != "succeeded" || response.ErrorCode is null && response.Payload is not null)
        && (response.Outcome == "succeeded" || response.ErrorCode is { Length: > 0 and <= 128 }
            && response.ErrorCode.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'));
}
