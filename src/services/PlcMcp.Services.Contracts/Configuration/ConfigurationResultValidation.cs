using System.Text.Json;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using C = PlcMcp.Services.Contracts.Configuration.ConfigurationOperations;

namespace PlcMcp.Services.Contracts.Configuration;

public static class ConfigurationResultValidation
{
    public static bool CompleteTaskBindings(JsonElement payload)
    {
        try
        {
            var data = payload.GetProperty("configuration").GetProperty("data");
            return !data.GetProperty("truncated").GetBoolean() && data.GetProperty("tasks").EnumerateArray().All(task =>
                task.GetProperty("calls").EnumerateArray().All(call => call.GetProperty("resolved").GetBoolean()
                    && Guid.TryParse(call.GetProperty("pouId").GetString(), out var id) && id != Guid.Empty));
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or JsonException) { return false; }
    }
    public static bool ValidDependencies(JsonElement value)
    {
        var snapshot = value.Deserialize<DependencySnapshot>(WorkerJson.Options);
        return snapshot is { Devices.Length: <= 512, Libraries.Length: <= 1024 }
            && snapshot.Devices.All(x => x is not null && Guid.TryParse(x.ObjectId, out var id) && id != Guid.Empty && x.Identification is { Type: >= 0 }
                && !string.IsNullOrWhiteSpace(x.Identification.Id) && !string.IsNullOrWhiteSpace(x.Identification.Version) && x.Module is not null)
            && snapshot.Libraries.All(x => x is not null && Guid.TryParse(x.ManagerId, out var id) && id != Guid.Empty && !string.IsNullOrWhiteSpace(x.Name) && x.Namespace is not null);
    }
    public static bool IsValid(string op, EngineeringInput input, JsonElement payload, bool write)
    {
        var c = payload.GetProperty("configuration");
        if (c.GetProperty("operation").GetString() != op || c.GetProperty("data").ValueKind != JsonValueKind.Object) return false;
        var id = c.GetProperty("objectId").GetString(); var data = c.GetProperty("data");
        if (!write)
        {
            if (op is not (C.LibraryInstalled or C.DeviceTree) && id != input.ObjectId) return false;
            return op switch
            {
                C.TaskList => Array(data, "tasks", input.MaxObjects),
                C.TaskRead => data.GetProperty("settings").ValueKind == JsonValueKind.Object && Array(data, "calls", 128),
                C.MemberRead => data.GetProperty("kind").GetString() == input.Query!.MemberKind,
                C.ObjectInspect => data.GetProperty("name").ValueKind == JsonValueKind.String && Array(data, "children", input.MaxObjects),
                C.LibraryList => Array(data, "references", input.MaxObjects),
                C.LibraryInstalled => Array(data, "libraries", input.MaxObjects),
                C.DeviceTree => Array(data, "devices", input.MaxObjects),
                C.ParameterRead => Array(data, "parameters", input.MaxObjects),
                C.IoRead => Array(data, "mappings", input.MaxObjects),
                _ => false
            };
        }
        if (!payload.TryGetProperty("dependencySnapshot", out var deps) || !ValidDependencies(deps)) return false;
        var e = input.Edit!; var change = e.Configuration;
        if (!C.IsCreate(op) && id != e.ObjectId || C.IsCreate(op) && (!Guid.TryParse(id, out var newId) || newId == Guid.Empty)) return false;
        if (C.IsDelete(op)) return data.GetProperty("removedIds").EnumerateArray().Any(x => x.GetString() == e.ObjectId);
        switch (op)
        {
            case C.TaskConfigurationCreate: return data.GetProperty("parentId").GetString() == e.ParentId && data.GetProperty("kind").GetString() == "task_configuration";
            case C.TaskCreate:
            case C.TaskUpdate:
                return (op != C.TaskCreate || data.GetProperty("name").GetString() == e.Name)
                    && Supplied(JsonSerializer.SerializeToElement(change!.Task, WorkerJson.Options), data.GetProperty("settings"));
            case C.TaskCallsSet:
                var calls = data.GetProperty("calls");
                return calls.GetArrayLength() == change!.Calls!.Length && change.Calls.Select((x, i) =>
                    calls[i].GetProperty("pouId").GetString() == Guid.Parse(x.PouId).ToString() && calls[i].GetProperty("pouName").GetString() == x.PouName && calls[i].GetProperty("comment").GetString() == x.Comment).All(x => x);
            case C.MemberCreate:
            case C.MemberUpdate:
                var m = change!.Member!;
                return data.GetProperty("kind").GetString() == m.Kind && (op != C.MemberCreate || data.GetProperty("name").GetString() == e.Name)
                    && MatchText(e.Declaration, data, "declaration") && MatchText(e.Implementation, data, "implementation")
                    && (m.Getter is null || Supplied(JsonSerializer.SerializeToElement(m.Getter, WorkerJson.Options), data.GetProperty("getter")))
                    && (m.Setter is null || Supplied(JsonSerializer.SerializeToElement(m.Setter, WorkerJson.Options), data.GetProperty("setter")));
            case C.FolderCreate: return data.GetProperty("name").GetString() == e.Name && data.GetProperty("parentId").GetString() == e.ParentId && data.GetProperty("kind").GetString() == "folder";
            case C.ObjectRename: return data.GetProperty("name").GetString() == e.Name;
            case C.ObjectMove: return data.GetProperty("parentId").GetString() == e.ParentId && (change!.Object!.Index == -1 || data.GetProperty("index").GetInt32() == change.Object.Index);
            case C.LibraryAdd: return data.GetProperty("reference").GetProperty("name").GetString() == change!.Library!.Name;
            case C.LibraryRemove: return data.GetProperty("removedReference").GetString() == change!.Library!.Name;
            case C.PlaceholderSet:
                var reference = data.GetProperty("reference"); var library = change!.Library!;
                return reference.GetProperty("name").GetString() == "#" + library.Name && reference.GetProperty("isPlaceholder").GetBoolean()
                    && (library.DefaultResolution is null || reference.GetProperty("defaultResolution").GetString() == library.DefaultResolution)
                    && (library.Redirection is null || reference.GetProperty("redirection").GetString() == library.Redirection);
            case C.DeviceAdd: return data.GetProperty("name").GetString() == e.Name && data.GetProperty("parentId").GetString() == e.ParentId
                && Supplied(JsonSerializer.SerializeToElement(change!.Device!.Identification, WorkerJson.Options), data.GetProperty("identification"))
                && (change.Device.Module is null || data.GetProperty("module").GetString() == change.Device.Module);
            case C.ParameterWrite: return data.GetProperty("value").GetString() == change!.Parameter!.Value
                && Supplied(JsonSerializer.SerializeToElement(change.Parameter.Address, WorkerJson.Options), data.GetProperty("address"));
            case C.IoMap: return data.GetProperty("variable").GetString() == change!.Mapping!.Variable
                && Supplied(JsonSerializer.SerializeToElement(change.Mapping.Address, WorkerJson.Options), data.GetProperty("address"))
                && data.GetProperty("createsVariable").GetBoolean() == change.Mapping.CreateVariable;
            default: return false;
        }
    }
    private static bool Array(JsonElement e, string key, int max) => e.GetProperty(key).ValueKind == JsonValueKind.Array && e.GetProperty(key).GetArrayLength() <= max;
    private static bool MatchText(string? expected, JsonElement actual, string key) => expected is null || Normalize(expected) == Normalize(actual.GetProperty(key).GetString()!);
    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    private static bool Supplied(JsonElement expected, JsonElement actual)
    {
        if (expected.ValueKind == JsonValueKind.Null) return true;
        if (expected.ValueKind == JsonValueKind.Object)
            return actual.ValueKind == JsonValueKind.Object && expected.EnumerateObject().All(p => p.Value.ValueKind == JsonValueKind.Null || actual.TryGetProperty(p.Name, out var a) && Supplied(p.Value, a));
        if (expected.ValueKind == JsonValueKind.Array)
            return actual.ValueKind == JsonValueKind.Array && expected.GetArrayLength() == actual.GetArrayLength()
                && expected.EnumerateArray().Select((v, i) => Supplied(v, actual[i])).All(x => x);
        if (expected.ValueKind == JsonValueKind.String) return actual.ValueKind == JsonValueKind.String && Normalize(expected.GetString()!) == Normalize(actual.GetString()!);
        return expected.GetRawText() == actual.GetRawText();
    }
}
