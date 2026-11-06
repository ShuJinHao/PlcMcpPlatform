using PlcMcp.Services.Contracts.Incremental;
using PlcMcp.Core.Workspaces;
using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Exchange;
using PlcMcp.Services.Contracts.Batch;
using PlcMcp.Services.Contracts.Delivery;
using System.Text.Json;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Editing;

namespace PlcMcp.Services.Contracts.Workers;

public static class WorkerResultValidation
{
    public static bool IsValid(WorkerRequest request, WorkerResponse response)
    {
        if (response.Outcome != "succeeded" && (response.ErrorCode != "compile_failed" || request.Operation != EngineeringOperations.Compile)) return true;
        if (response.Payload is not { ValueKind: JsonValueKind.Object } payload) return false;
        try
        {
            var input = request.Payload.Deserialize<EngineeringInput>(WorkerJson.Options)!;
            if (request.Operation is IncrementalOperations.Context or IncrementalOperations.Diff)
            {
                var q = input.Incremental!;
                if (payload.GetProperty("headRevision").GetString() != request.ExpectedRevision || payload.GetProperty("workspaceName").GetString() != q.WorkspaceName) return false;
                return request.Operation == IncrementalOperations.Context
                    ? payload.GetProperty("revision").GetString() == q.Revision && payload.GetProperty("applicationId").GetString() == q.ApplicationId
                        && payload.GetProperty("objects").GetArrayLength() <= q.PageSize
                    : payload.GetProperty("fromRevision").GetString() == q.Revision && payload.GetProperty("toRevision").GetString() == q.CompareRevision
                        && payload.GetProperty("files").GetArrayLength() <= q.PageSize;
            }
            if (request.Operation == DeliveryOperations.Create)
            {
                var artifact = payload.GetProperty("artifact"); var compilation = payload.GetProperty("compile"); var sha = artifact.GetProperty("projectSha256").GetString();
                return payload.GetProperty("packaged").GetBoolean() && payload.GetProperty("revision").GetString() == request.ExpectedRevision
                    && artifact.GetProperty("revision").GetString() == request.ExpectedRevision && artifact.GetProperty("workspaceName").GetString() == input.Delivery!.WorkspaceName
                    && artifact.GetProperty("executionMode").GetString() == request.ExecutionMode && !artifact.GetProperty("engineeringReady").GetBoolean()
                    && artifact.GetProperty("artifactId").GetString() is { Length: 64 } id && id.All(Uri.IsHexDigit) && artifact.GetProperty("sha256").GetString() == id
                    && compilation.GetProperty("applicationId").GetString() == input.Delivery.ApplicationId && compilation.GetProperty("revision").GetString() == sha
                    && compilation.GetProperty("succeeded").GetBoolean() && ValidDiagnostics(compilation.GetProperty("diagnostics"), true)
                    && payload.GetProperty("tasks").GetProperty("revision").GetString() == sha;
            }
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
            if (ExchangeOperations.IsRead(request.Operation))
                return payload.GetProperty("revision").GetString() == request.ExpectedRevision && ExchangeResultValidation.IsValid(request.Operation, input, payload);
            if (ConfigurationOperations.IsRead(request.Operation))
                return payload.GetProperty("revision").GetString() == request.ExpectedRevision && ConfigurationResultValidation.IsValid(request.Operation, input, payload, false);
            if (!payload.TryGetProperty("revision", out var revision) || revision.GetString() != request.ExpectedRevision) return false;
            return request.Operation switch
            {
                IncrementalOperations.Source => payload.GetProperty("objectId").GetString() == input.ObjectId
                    && payload.GetProperty("name").ValueKind == JsonValueKind.String && payload.GetProperty("kind").ValueKind == JsonValueKind.String
                    && payload.GetProperty("declaration").ValueKind is JsonValueKind.String or JsonValueKind.Null
                    && payload.GetProperty("implementation").ValueKind is JsonValueKind.String or JsonValueKind.Null
                    && payload.GetProperty("implementationKind").GetString() is "textual" or "graphical_or_unavailable" or "none",
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
        if (payload.TryGetProperty("dependencySnapshot", out var snapshot) && !ConfigurationResultValidation.ValidDependencies(snapshot)) return false;
        if (request.Operation == IncrementalOperations.Patch)
        {
            var section = edit.Patch!.Section;
            return payload.GetProperty("objectId").GetString() == edit.ObjectId
                && payload.GetProperty("patch").GetProperty("beforeSha256").GetString() == edit.Patch.ExpectedSha256
                && payload.GetProperty("patch").GetProperty("afterSha256").GetString() == TextPatchRules.Hash(payload.GetProperty(section).GetString()!);
        }
        if (request.Operation == IncrementalOperations.Restore)
        {
            var restore = payload.GetProperty("restore");
            var checks = restore.GetProperty("checks");
            return restore.GetProperty("sourceRevision").GetString() == edit.RestoreRevision && checks.GetArrayLength() is > 0 and <= 16
                && checks.EnumerateArray().All(x => x.GetProperty("compile").GetProperty("succeeded").GetBoolean()
                    && x.GetProperty("compile").GetProperty("revision").GetString() == revision
                    && x.GetProperty("compile").GetProperty("applicationId").GetString() == x.GetProperty("applicationId").GetString()
                    && ValidDiagnostics(x.GetProperty("compile").GetProperty("diagnostics"), true)
                    && x.GetProperty("tasks").GetProperty("revision").GetString() == revision && ConfigurationResultValidation.CompleteTaskBindings(x.GetProperty("tasks")));
        }
        if (request.Operation == BatchOperations.Apply)
        {
            var batch = payload.GetProperty("batch");
            return batch.GetProperty("state").GetString() == "verified" && batch.GetProperty("planSha256").GetString() == edit.PlanSha256
                && batch.GetProperty("steps").GetArrayLength() == edit.Batch!.Steps.Length
                && batch.GetProperty("steps").EnumerateArray().All(s => s.GetProperty("status").GetString() == "succeeded")
                && batch.GetProperty("verifiedProjectSha256").GetString() == revision
                && batch.GetProperty("compile").GetProperty("revision").GetString() == revision
                && batch.GetProperty("compile").GetProperty("applicationId").GetString() == edit.Batch.ApplicationId
                && batch.GetProperty("compile").GetProperty("succeeded").GetBoolean()
                && ValidDiagnostics(batch.GetProperty("compile").GetProperty("diagnostics"), true)
                && batch.GetProperty("tasks").GetProperty("revision").GetString() == revision;
        }
        if (ExchangeOperations.IsWrite(request.Operation)) return ExchangeResultValidation.IsValid(request.Operation, request.Payload.Deserialize<EngineeringInput>(WorkerJson.Options)!, payload);
        if (ConfigurationOperations.IsWrite(request.Operation)) return ConfigurationResultValidation.IsValid(request.Operation, request.Payload.Deserialize<EngineeringInput>(WorkerJson.Options)!, payload, true);
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
