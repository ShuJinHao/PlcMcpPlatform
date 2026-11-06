using System.Text.Json;
using System.Text.Json.Nodes;
using PlcMcp.Core.Workspaces;
using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Exchange;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Incremental;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.EngineeringService.Incremental;

public sealed class IncrementalReadExecutor(IEngineeringExecutor worker, IManagedProjectFiles files, IProjectChangeStore changes,
    IProjectHistoryStore history) : IEngineeringExecutor
{
    public string ExecutionMode => worker.ExecutionMode;
    public OperationError? Unavailability => worker.Unavailability;
    public OperationError? CheckAvailability(string operation) => worker.CheckAvailability(operation);
    public ValidationEvidence? Validation => worker.Validation;
    public async Task<WorkerResponse> ExecuteAsync(WorkerRequest request, CancellationToken token)
    {
        if (request.Operation is not (IncrementalOperations.Context or IncrementalOperations.Diff or IncrementalOperations.Query)) return await worker.ExecuteAsync(request, token);
        var session = new WorkerSession(worker, request, request.ExpectedRevision);
        try
        {
            if (CheckAvailability(request.Operation) is { } denied) throw new ProjectEditException(denied.Code);
            var input = request.Payload.Deserialize<EngineeringInput>(WorkerJson.Options)!;
            var query = input.Incremental ?? throw new ProjectEditException("invalid_incremental_query");
            if (input.ProjectPath != files.WorkspacePath(query.WorkspaceName)) throw new ProjectEditException("path_not_allowed");
            var head = await changes.GetHeadAsync(input.ProjectPath, token) ?? throw new ProjectEditException("workspace_not_found");
            if (head.Snapshot.Revision != request.ExpectedRevision) throw new ProjectEditException("workspace_revision_conflict");
            var sources = new List<ProjectSnapshot>();
            async Task<ProjectSnapshot> Version(string revision)
            {
                var record = await history.GetAsync(input.ProjectPath, revision, token) ?? throw new ProjectEditException("version_not_found");
                var snapshot = await files.ReadVersionAsync(record, head.Snapshot.Template, token);
                if (snapshot.Template.ExecutionMode != ExecutionMode) throw new ProjectEditException("execution_mode_mismatch");
                sources.Add(snapshot); return snapshot;
            }
            async Task<EngineeringInput> Copy(ProjectSnapshot source)
            {
                var candidate = (await files.PrepareAsync(input.ProjectPath, Guid.NewGuid(), source, token)).Candidate;
                session.Revision = await files.ProjectFileRevisionAsync(candidate, token);
                return new(Path.Combine(candidate.Directory, candidate.ProjectFile), IdeProfile: source.Template.IdeProfile, MaxObjects: 2000);
            }
            async Task<JsonElement[]> Scope(EngineeringInput read)
            {
                var tree = await session.Invoke(EngineeringOperations.Structure, read, token);
                if (tree.GetProperty("truncated").GetBoolean()) throw new ProjectEditException("context_scope_incomplete");
                var rows = tree.GetProperty("objects").EnumerateArray().ToArray();
                if (!rows.Any(x => x.GetProperty("id").GetString() == query.ApplicationId && x.GetProperty("kind").GetString() == "application")) throw new ProjectEditException("application_required");
                return rows;
            }
            async Task<JsonElement?> Object(ProjectSnapshot snapshot, string objectId)
            {
                var read = await Copy(snapshot);
                var rows = await Scope(read); var scope = Descendants(rows, query.ApplicationId!);
                if (!rows.Any(x => x.GetProperty("id").GetString() == objectId)) return null;
                if (!scope.Contains(objectId)) throw new ProjectEditException("object_application_scope_mismatch");
                return await session.Invoke(query.Graph ? ExchangeOperations.ReadGraph : IncrementalOperations.Source, read with { ObjectId = objectId }, token);
            }
            var before = await Version(query.Revision);
            object result;
            if (request.Operation == IncrementalOperations.Query)
            {
                var read = await Copy(before); var rows = await Scope(read);
                result = await EngineeringQueryReader.Read(session, read, rows, query, request.ExpectedRevision, token);
            }
            else if (request.Operation == IncrementalOperations.Context)
            {
                var read = await Copy(before); var rows = await Scope(read); var scope = Descendants(rows, query.ApplicationId!);
                if (query.ObjectId is not null && !scope.Contains(query.ObjectId)) throw new ProjectEditException("object_application_scope_mismatch");
                var selected = rows.Where(x => scope.Contains(x.GetProperty("id").GetString()!))
                    .OrderBy(x => x.GetProperty("id").GetString() == query.ObjectId ? 0 : 1)
                    .ThenBy(x => x.GetProperty("id").GetString(), StringComparer.Ordinal).ToArray();
                var objects = new List<ContextObject>();
                foreach (var row in selected.Skip(query.Offset).Take(query.PageSize))
                {
                    var id = row.GetProperty("id").GetString()!;
                    var source = await session.Invoke(IncrementalOperations.Source, read with { ObjectId = id }, token);
                    objects.Add(SourceObject(row, source, query));
                }
                var tasks = await session.Invoke(ConfigurationOperations.TaskList, read with { ObjectId = query.ApplicationId, MaxObjects = 128 }, token);
                var managers = rows.Where(x => x.GetProperty("kind").GetString() == "library_manager").ToArray();
                if (managers.Length > 16) throw new ProjectEditException("context_library_scope_too_large");
                var libraries = new List<object>();
                foreach (var manager in managers)
                {
                    var id = manager.GetProperty("id").GetString()!;
                    var references = await session.Invoke(ConfigurationOperations.LibraryList, read with { ObjectId = id, MaxObjects = 128 }, token);
                    libraries.Add(new { managerId = id, scope = scope.Contains(id) ? "selected_application" : "project_or_other_application", data = references.GetProperty("configuration").GetProperty("data") });
                }
                result = new ContextPage(query.WorkspaceName, request.ExpectedRevision, query.Revision, query.ApplicationId!, objects.ToArray(), selected.Length,
                    query.Offset + objects.Count < selected.Length ? query.Offset + objects.Count : null,
                    tasks.GetProperty("configuration").GetProperty("data"), JsonSerializer.SerializeToElement(libraries, WorkerJson.Options));
            }
            else
            {
                var after = await Version(query.CompareRevision!);
                var oldFiles = before.Files.ToDictionary(x => x.Path, StringComparer.Ordinal); var newFiles = after.Files.ToDictionary(x => x.Path, StringComparer.Ordinal);
                var deltas = oldFiles.Keys.Union(newFiles.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal)
                    .Select(path => new FileDifference(path, oldFiles.GetValueOrDefault(path), newFiles.GetValueOrDefault(path))).Where(x => x.Before != x.After).ToArray();
                JsonElement? objectDiff = null;
                if (query.ObjectId is not null)
                {
                    var oldObject = await Object(before, query.ObjectId); var newObject = await Object(after, query.CompareObjectId ?? query.ObjectId);
                    objectDiff = query.Graph ? GraphDiff(oldObject, newObject, query) : JsonSerializer.SerializeToElement(new {
                        objectId = query.ObjectId, compareObjectId = query.CompareObjectId ?? query.ObjectId, comparison = "normalized_source_text", before = TextObject(oldObject, query), after = TextObject(newObject, query),
                        declarationChanged = SourceHash(oldObject, "declaration") != SourceHash(newObject, "declaration"),
                        implementationChanged = SourceHash(oldObject, "implementation") != SourceHash(newObject, "implementation") }, WorkerJson.Options);
                }
                result = new ProjectDiff(query.WorkspaceName, request.ExpectedRevision, before.Revision, after.Revision, deltas.Skip(query.Offset).Take(query.PageSize).ToArray(),
                    deltas.Length, query.Offset + query.PageSize < deltas.Length ? query.Offset + query.PageSize : null, objectDiff);
            }
            foreach (var source in sources.Append(head.Snapshot)) await files.VerifyAsync(source, token);
            if ((await changes.GetHeadAsync(input.ProjectPath, token))?.Snapshot.Revision != request.ExpectedRevision) throw new ProjectEditException("workspace_revision_conflict");
            token.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= request.DeadlineUtc) throw new ProjectEditException("deadline_expired");
            var payload = JsonSerializer.SerializeToElement(result, WorkerJson.Options);
            if (System.Text.Encoding.UTF8.GetByteCount(payload.GetRawText()) > 512 * 1024) throw new ProjectEditException("context_result_too_large_reduce_page");
            return new(1, request.RequestId, request.JobId, "succeeded", null, ExecutionMode, payload);
        }
        catch (Exception error) { return session.Failure(error); }
    }
    internal static HashSet<string> Descendants(JsonElement[] rows, string application)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { application };
        for (var i = 0; i < rows.Length; i++)
        {
            var count = ids.Count;
            foreach (var row in rows)
                if (row.TryGetProperty("parentId", out var parent) && parent.ValueKind == JsonValueKind.String && ids.Contains(parent.GetString()!)) ids.Add(row.GetProperty("id").GetString()!);
            if (ids.Count == count) break;
        }
        return ids;
    }
    internal static ContextObject SourceObject(JsonElement row, JsonElement source, IncrementalQuery query)
    {
        string? Optional(string field) => source.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return new(row.GetProperty("id").GetString()!, row.GetProperty("parentId").GetString(), source.GetProperty("name").GetString()!,
            source.GetProperty("kind").GetString()!, Optional("typeId"), source.GetProperty("implementationKind").GetString()!,
            Window(source, "declaration", query), Window(source, "implementation", query), Optional("ownerId"), Optional("accessor"), Optional("typeEvidence"));
    }
    private static string? SourceHash(JsonElement? value, string field) => value?.GetProperty(field).GetString() is { } text ? TextPatchRules.Hash(text) : null;
    private static object? TextObject(JsonElement? value, IncrementalQuery query) => value is { } item ? new { kind = item.GetProperty("kind"),
        implementationKind = item.GetProperty("implementationKind"), declaration = Window(item, "declaration", query), implementation = Window(item, "implementation", query) } : null;
    private static TextWindow? Window(JsonElement value, string field, IncrementalQuery query)
    {
        if (value.GetProperty(field).GetString() is not { } raw) return null;
        var text = TextPatchRules.Normalize(raw); var offset = Math.Min(query.TextOffset, text.Length); var length = Math.Min(query.MaxTextChars, text.Length - offset);
        // Offset/length use .NET UTF-16 units. Do not split a surrogate pair in a JSON string.
        if (offset > 0 && offset < text.Length && char.IsLowSurrogate(text[offset])) throw new ProjectEditException("invalid_text_offset");
        if (offset + length < text.Length && length > 0 && char.IsHighSurrogate(text[offset + length - 1])) length--;
        if (length == 0 && offset < text.Length) throw new ProjectEditException("context_text_window_too_small");
        return new(TextPatchRules.Hash(text), text.Length, offset, text.Substring(offset, length), offset + length < text.Length ? offset + length : null);
    }
    private static JsonElement GraphDiff(JsonElement? before, JsonElement? after, IncrementalQuery query)
    {
        JsonNode? Graph(JsonElement? value) => value is { } v ? JsonNode.Parse(v.GetProperty("exchange").GetProperty("graph").GetRawText()) : null;
        var a = Graph(before); var b = Graph(after); var deltas = new List<object>();
        foreach (var field in new[] { "name", "pouType", "language", "variables", "nodes", "actions", "returnType", "profile" })
        {
            var oldValue = a?["program"]?[field]; var newValue = b?["program"]?[field];
            if (field is "variables" or "nodes" or "actions")
            {
                string Key(JsonNode? n) => n![field == "nodes" ? "id" : "name"]!.ToJsonString();
                var oldItems = (oldValue as JsonArray ?? []).Select((x, i) => (Key: Key(x), Value: x, Index: i)).ToDictionary(x => x.Key);
                var newItems = (newValue as JsonArray ?? []).Select((x, i) => (Key: Key(x), Value: x, Index: i)).ToDictionary(x => x.Key);
                foreach (var key in oldItems.Keys.Union(newItems.Keys).Order(StringComparer.Ordinal))
                {
                    var ov = oldItems.GetValueOrDefault(key); var nv = newItems.GetValueOrDefault(key);
                    if (!JsonNode.DeepEquals(ov.Value, nv.Value) || ov.Index != nv.Index)
                        deltas.Add(new { component = field, key, before = ov.Value, after = nv.Value, beforeIndex = ov.Value is null ? (int?)null : ov.Index, afterIndex = nv.Value is null ? (int?)null : nv.Index });
                }
            }
            else if (!JsonNode.DeepEquals(oldValue, newValue)) deltas.Add(new { component = field, before = oldValue, after = newValue });
        }
        return JsonSerializer.SerializeToElement(new { objectId = query.ObjectId, compareObjectId = query.CompareObjectId ?? query.ObjectId, comparison = "graph-v1_v2_including_types_pins_connections_branches_and_order",
            beforeProfile = a?["profile"], afterProfile = b?["profile"],
            beforeSemanticSha256 = a?["semanticSha256"], afterSemanticSha256 = b?["semanticSha256"], changes = deltas.Skip(query.Offset).Take(query.PageSize),
            totalChanges = deltas.Count, nextOffset = query.Offset + query.PageSize < deltas.Count ? (int?)(query.Offset + query.PageSize) : null }, WorkerJson.Options);
    }
}
