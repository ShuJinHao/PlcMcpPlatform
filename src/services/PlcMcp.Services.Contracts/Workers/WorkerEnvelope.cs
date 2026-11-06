using System.Text.Json;

namespace PlcMcp.Services.Contracts.Workers;

/// <summary>版本化线协议草案；旧 .NET Framework Worker 按 JSON Schema 实现，不引用本 net8.0 DLL。</summary>
public sealed record WorkerRequest(int ProtocolVersion, Guid RequestId, Guid JobId,
    string Operation, Guid WorkspaceId, string ExpectedRevision, DateTimeOffset DeadlineUtc, JsonElement Payload);
public sealed record WorkerResponse(int ProtocolVersion, Guid RequestId, Guid JobId,
    string Outcome, string? ErrorCode, JsonElement? Payload);
