using System.Text.Json;
using PlcMcp.Services.Contracts.Execution;

namespace PlcMcp.Services.Contracts.Workers;

public static class WorkerResultValidation
{
    public static bool IsValid(WorkerRequest request, WorkerResponse response)
    {
        if (response.Outcome != "succeeded" && response.ErrorCode != "compile_failed") return true;
        if (response.Payload is not { ValueKind: JsonValueKind.Object } payload) return false;
        try
        {
            if (!payload.TryGetProperty("revision", out var revision) || revision.GetString() != request.ExpectedRevision) return false;
            return request.Operation switch
            {
                EngineeringOperations.Open => payload.GetProperty("opened").GetBoolean(),
                EngineeringOperations.Structure => payload.GetProperty("objects").ValueKind == JsonValueKind.Array
                    && payload.GetProperty("objects").GetArrayLength() <= request.Payload.GetProperty("maxObjects").GetInt32()
                    && payload.GetProperty("truncated").ValueKind is JsonValueKind.True or JsonValueKind.False,
                EngineeringOperations.ReadProgram => payload.GetProperty("objectId").GetString() == request.Payload.GetProperty("objectId").GetString()
                    && payload.GetProperty("declaration").ValueKind == JsonValueKind.String && payload.GetProperty("implementation").ValueKind == JsonValueKind.String,
                EngineeringOperations.Compile => payload.GetProperty("applicationId").GetString() == request.Payload.GetProperty("applicationId").GetString()
                    && payload.GetProperty("diagnostics").ValueKind == JsonValueKind.Array
                    && payload.GetProperty("succeeded").GetBoolean() == (response.Outcome == "succeeded")
                    && ValidDiagnostics(payload.GetProperty("diagnostics"), response.Outcome == "succeeded"),
                _ => false
            };
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException) { return false; }
    }
    private static bool ValidDiagnostics(JsonElement diagnostics, bool succeeded)
    {
        if (diagnostics.GetArrayLength() > 500) return false;
        var hasError = false;
        foreach (var item in diagnostics.EnumerateArray())
        {
            var severity = item.GetProperty("severity").GetString();
            if (severity is not ("error" or "warning" or "information")
                || item.GetProperty("code").ValueKind != JsonValueKind.String || item.GetProperty("message").ValueKind != JsonValueKind.String) return false;
            hasError |= severity == "error";
        }
        return succeeded != hasError;
    }
}
