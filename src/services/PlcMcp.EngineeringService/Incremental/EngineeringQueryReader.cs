using System.Text.Json;
using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Incremental;
using PlcMcp.Services.Contracts.Workers;

namespace PlcMcp.EngineeringService.Incremental;

internal static class EngineeringQueryReader
{
    public static async Task<EngineeringPage> Read(WorkerSession session, EngineeringInput read, JsonElement[] rows,
        IncrementalQuery query, string head, CancellationToken token)
    {
        var appScope = IncrementalReadExecutor.Descendants(rows, query.ApplicationId!);
        var values = new List<JsonElement>();
        int total;
        if (query.Collection is "objects" or "members")
        {
            if (query.ParentId is { } parent && !appScope.Contains(parent) || query.ObjectId is { } id && !appScope.Contains(id))
                throw new ProjectEditException("object_application_scope_mismatch");
            if (query.Collection == "members")
            {
                var parentSource = await session.Invoke(IncrementalOperations.Source, read with { ObjectId = query.ParentId }, token);
                if (parentSource.GetProperty("kind").GetString() is not ("pou" or "gvl" or "Property")) throw new ProjectEditException("member_parent_not_supported");
            }
            var scope = query.ParentId is null ? appScope : IncrementalReadExecutor.Descendants(rows, query.ParentId);
            var selected = rows.Where(x => scope.Contains(x.GetProperty("id").GetString()!)
                && (query.ObjectId is null || x.GetProperty("id").GetString() == query.ObjectId)
                && (query.Collection != "members" || x.GetProperty("parentId").GetString() == query.ParentId))
                .OrderBy(x => x.GetProperty("id").GetString(), StringComparer.Ordinal).ToArray();
            if (query.ObjectId is not null && selected.Length != 1) throw new ProjectEditException("object_scope_mismatch");
            total = selected.Length;
            foreach (var row in selected.Skip(query.Offset).Take(query.PageSize))
            {
                var source = await session.Invoke(IncrementalOperations.Source, read with { ObjectId = row.GetProperty("id").GetString(), Query = query.SourceQuery }, token);
                values.Add(JsonSerializer.SerializeToElement(IncrementalReadExecutor.SourceObject(row, source, query), WorkerJson.Options));
            }
        }
        else
        {
            if (query.Collection == "tasks")
            {
                var response = await session.Invoke(ConfigurationOperations.TaskList, read with { ObjectId = query.ApplicationId, MaxObjects = 2000 }, token);
                var data = response.GetProperty("configuration").GetProperty("data");
                if (data.GetProperty("truncated").GetBoolean()) throw new ProjectEditException("task_inventory_incomplete");
                values.AddRange(data.GetProperty("tasks").EnumerateArray().OrderBy(x => x.GetProperty("objectId").GetString(), StringComparer.Ordinal));
            }
            else
            {
                var otherApps = rows.Where(x => x.GetProperty("kind").GetString() == "application" && x.GetProperty("id").GetString() != query.ApplicationId)
                    .SelectMany(x => IncrementalReadExecutor.Descendants(rows, x.GetProperty("id").GetString()!)).ToHashSet(StringComparer.Ordinal);
                var managers = rows.Where(x => x.GetProperty("kind").GetString() == "library_manager" && !otherApps.Contains(x.GetProperty("id").GetString()!))
                    .OrderBy(x => x.GetProperty("id").GetString(), StringComparer.Ordinal).ToArray();
                if (managers.Length > 16) throw new ProjectEditException("context_library_scope_too_large");
                foreach (var manager in managers)
                {
                    var id = manager.GetProperty("id").GetString()!;
                    var response = await session.Invoke(ConfigurationOperations.LibraryList, read with { ObjectId = id, MaxObjects = 2000 }, token);
                    var data = response.GetProperty("configuration").GetProperty("data");
                    if (data.GetProperty("truncated").GetBoolean()) throw new ProjectEditException("library_inventory_incomplete");
                    values.AddRange(data.GetProperty("references").EnumerateArray().OrderBy(x => x.GetProperty("name").GetString(), StringComparer.Ordinal)
                        .Select(reference => JsonSerializer.SerializeToElement(new { managerId = id, scope = appScope.Contains(id) ? "selected_application" : "shared_project_or_device", reference }, WorkerJson.Options)));
                }
            }
            total = values.Count; values = values.Skip(query.Offset).Take(query.PageSize).ToList();
        }
        if (query.Offset > total) throw new ProjectEditException("engineering_page_out_of_range");
        return new(query.WorkspaceName, head, query.Revision, query.ApplicationId!, query.Collection,
            JsonSerializer.SerializeToElement(values, WorkerJson.Options), total,
            query.Offset + values.Count < total ? EngineeringCursor.Encode(head, query, query.Offset + values.Count) : null);
    }
}
