using System.Text.Json;
using PlcMcp.Core.Workspaces;
using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Batch;
using PlcMcp.Services.Contracts.Workers;

namespace PlcMcp.EngineeringService.Batch;

internal static class BatchDetails
{
    // Journals retain affected identities and evidence, not repeated full source/library inventories.
    public static JsonElement Configuration(JsonElement configuration)
    {
        var operation = configuration.GetProperty("operation").GetString();
        var data = configuration.GetProperty("data");
        object? detail;
        if (operation is ConfigurationOperations.MemberCreate or ConfigurationOperations.MemberUpdate)
        {
            object? Source(JsonElement value)
            {
                if (value.ValueKind != JsonValueKind.Object) return null;
                string? Hash(string field) => value.TryGetProperty(field, out var source) && source.ValueKind == JsonValueKind.String ? TextPatchRules.Hash(source.GetString()!) : null;
                return new { objectId = value.GetProperty("objectId").GetString(), declarationSha256 = Hash("declaration"), implementationSha256 = Hash("implementation") };
            }
            detail = new { name = data.GetProperty("name"), kind = data.GetProperty("kind"), typeId = data.GetProperty("typeId"),
                source = Source(data), getter = data.TryGetProperty("getter", out var get) ? Source(get) : null,
                setter = data.TryGetProperty("setter", out var set) ? Source(set) : null };
        }
        else if (BatchOperations.LibraryWrite(operation!))
            detail = data.TryGetProperty("reference", out var reference) ? new { reference } : (object)new { removedReference = data.GetProperty("removedReference") };
        else if (operation is ConfigurationOperations.TaskCreate or ConfigurationOperations.TaskUpdate or ConfigurationOperations.TaskCallsSet)
            detail = new { name = data.GetProperty("name"), settings = data.GetProperty("settings"), callCount = data.GetProperty("calls").GetArrayLength() };
        else return configuration;
        return JsonSerializer.SerializeToElement(new { operation, objectId = configuration.GetProperty("objectId"), data = detail }, WorkerJson.Options);
    }
}
