using System.Text.Json;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Editing;

namespace PlcMcp.Services.Contracts.Workers;

public static class WorkerResultValidation
{
    public static bool IsValid(WorkerRequest request, WorkerResponse response)
    {
        if (response.Outcome != "succeeded" && response.ErrorCode != "compile_failed") return true;
        if (response.Payload is not { ValueKind: JsonValueKind.Object } payload) return false;
        try
        {
            var input = request.Payload.Deserialize<EngineeringInput>(WorkerJson.Options)!;
            if (EditOperations.Contains(request.Operation) && !input.Candidate)
            {
                var revisionValue = payload.GetProperty("revision").GetString();
                return payload.GetProperty("published").GetBoolean() && payload.GetProperty("baseRevision").GetString() == request.ExpectedRevision
                    && payload.GetProperty("revisionScope").GetString() == "managed_project_manifest_v2"
                    && revisionValue is { Length: 100 } && revisionValue.StartsWith("m2:" + request.JobId.ToString("N") + ":", StringComparison.Ordinal)
                    && revisionValue[36..].All(Uri.IsHexDigit)
                    && payload.GetProperty("workspaceName").GetString() == input.Edit!.WorkspaceName
                    && payload.GetProperty("projectPath").GetString()!.StartsWith(input.ProjectPath + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && payload.GetProperty("engineeringResult").GetProperty("saved").GetBoolean()
                    && payload.GetProperty("engineeringResult").GetProperty("reopened").GetBoolean();
            }
            if (request.Operation == EditOperations.VerifyProject || EditOperations.Contains(request.Operation))
                return input.Candidate && ValidEdit(request, payload, input.Edit!);
            if (!payload.TryGetProperty("revision", out var revision) || revision.GetString() != request.ExpectedRevision) return false;
            return request.Operation switch
            {
                EngineeringOperations.Open => payload.GetProperty("opened").GetBoolean(),
                EngineeringOperations.Structure => payload.GetProperty("objects").ValueKind == JsonValueKind.Array
                    && payload.GetProperty("objects").GetArrayLength() <= request.Payload.GetProperty("maxObjects").GetInt32()
                    && payload.GetProperty("truncated").ValueKind is JsonValueKind.True or JsonValueKind.False,
                EngineeringOperations.ReadProgram => payload.GetProperty("objectId").GetString() == request.Payload.GetProperty("objectId").GetString()
                    && payload.GetProperty("declaration").ValueKind == JsonValueKind.String && payload.GetProperty("implementation").ValueKind == JsonValueKind.String,
                EngineeringOperations.ReadGvl or EngineeringOperations.ReadDut => payload.GetProperty("objectId").GetString() == input.ObjectId
                    && payload.GetProperty("kind").GetString() == request.Operation.Split('.')[0] && payload.GetProperty("declaration").ValueKind == JsonValueKind.String,
                EngineeringOperations.Compile => payload.GetProperty("applicationId").GetString() == request.Payload.GetProperty("applicationId").GetString()
                    && payload.GetProperty("diagnostics").ValueKind == JsonValueKind.Array
                    && payload.GetProperty("succeeded").GetBoolean() == (response.Outcome == "succeeded")
                    && ValidDiagnostics(payload.GetProperty("diagnostics"), response.Outcome == "succeeded"),
                _ => false
            };
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException or JsonException or NullReferenceException) { return false; }
    }
    private static bool ValidEdit(WorkerRequest request, JsonElement payload, EditInput edit)
    {
        var revision = payload.GetProperty("revision").GetString();
        if (payload.GetProperty("baseRevision").GetString() != request.ExpectedRevision || revision is not { Length: 64 }
            || !revision.All(Uri.IsHexDigit) || !payload.GetProperty("saved").GetBoolean() || !payload.GetProperty("reopened").GetBoolean()
            || !payload.GetProperty("readBackVerified").GetBoolean() || !payload.GetProperty("sessionClosedAfterOperation").GetBoolean()) return false;
        if (!payload.GetProperty("dependencies").GetProperty("controllerMatched").GetBoolean()
            || !payload.GetProperty("dependencies").GetProperty("libraryReferencesMatched").GetBoolean()
            || payload.GetProperty("dependencies").GetProperty("installedDependenciesVerified").GetBoolean()) return false;
        if (request.Operation == EditOperations.VerifyProject) return true;
        if (!Guid.TryParse(payload.GetProperty("objectId").GetString(), out var id) || id == Guid.Empty) return false;
        if (EditOperations.UpdatesObject(request.Operation) && payload.GetProperty("objectId").GetString() != edit.ObjectId) return false;
        if (EditOperations.CreatesObject(request.Operation) && payload.GetProperty("name").GetString() != edit.Name) return false;
        return (edit.Declaration is null || Normalize(edit.Declaration) == Normalize(payload.GetProperty("declaration").GetString()!))
            && (edit.Implementation is null || Normalize(edit.Implementation) == Normalize(payload.GetProperty("implementation").GetString()!));
    }
    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
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
