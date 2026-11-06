using System.Text.Json;
using PlcMcp.Services.Contracts.Batch;
using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Editing;

namespace PlcMcp.EngineeringService.Batch;

/// <summary>Explicit engineering scope over the current candidate tree, never over cached pre-edit identities.</summary>
public sealed class BatchScope
{
    private readonly BatchPlan plan;
    private readonly Dictionary<string, JsonElement> rows;
    public BatchScope(BatchPlan plan, JsonElement[] rows)
    {
        this.plan = plan;
        if (rows.Select(x => x.GetProperty("id").GetString()).Distinct(StringComparer.Ordinal).Count() != rows.Length)
            throw new ProjectEditException("batch_scope_incomplete");
        this.rows = rows.ToDictionary(x => x.GetProperty("id").GetString()!, StringComparer.Ordinal);
        if (Kind(plan.ApplicationId) != "application") throw new ProjectEditException("application_required");
    }
    private string? Kind(string id) => rows.TryGetValue(id, out var row) ? row.GetProperty("kind").GetString() : null;
    public void ValidateDeclaredScopes()
    {
        if ((plan.DeviceScopeIds ?? []).Any(id => Kind(id) != "device")
            || (plan.LibraryManagerIds ?? []).Any(id => Kind(id) != "library_manager")) throw new ProjectEditException("batch_declared_scope_mismatch");
    }
    private HashSet<string> Descendants(string root)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        if (!rows.ContainsKey(root)) return found;
        found.Add(root);
        for (var n = 0; n < rows.Count; n++)
        {
            var count = found.Count;
            foreach (var (id, row) in rows)
                if (row.TryGetProperty("parentId", out var p) && p.ValueKind == JsonValueKind.String && found.Contains(p.GetString()!)) found.Add(id);
            if (found.Count == count) break;
        }
        return found;
    }
    public void ValidateStep(string operation, EditInput edit)
    {
        if (BatchOperations.DeviceWrite(operation))
        {
            var target = operation == ConfigurationOperations.DeviceAdd ? edit.ParentId : edit.ObjectId;
            if (target is null || Kind(target) != "device" || !(plan.DeviceScopeIds ?? []).Any(root => Descendants(root).Contains(target)))
                throw new ProjectEditException("batch_device_scope_mismatch");
            return;
        }
        var allowed = Descendants(plan.ApplicationId);
        if (BatchOperations.LibraryWrite(operation))
        {
            if (edit.ObjectId is not { } id || Kind(id) != "library_manager"
                || !allowed.Contains(id) && !(plan.LibraryManagerIds ?? []).Contains(id, StringComparer.Ordinal))
                throw new ProjectEditException("batch_library_scope_mismatch");
            return;
        }
        if (edit.ParentId is { } parent && !allowed.Contains(parent) || edit.ObjectId is { } targetId && (!allowed.Contains(targetId) || Kind(targetId) == "device")
            || edit.Configuration?.Calls?.Any(c => !allowed.Contains(c.PouId)) == true)
            throw new ProjectEditException("batch_application_scope_mismatch");
    }
    public string[] Applications(bool all)
    {
        var ids = all ? rows.Where(x => x.Value.GetProperty("kind").GetString() == "application").Select(x => x.Key).Order(StringComparer.Ordinal).ToArray() : [plan.ApplicationId];
        if (ids.Length is < 1 or > 16) throw new ProjectEditException("batch_application_scope_invalid");
        return ids;
    }
}
