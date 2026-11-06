using System.Text.Json;
using System.Text.Json.Nodes;
using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Workers;
using C = PlcMcp.Services.Contracts.Configuration.ConfigurationOperations;

namespace PlcMcp.TestHost;

/// <summary>Persisted synthetic engineering state for HTTP/coordinator tests. No vendor compatibility evidence.</summary>
public sealed class FixtureConfiguration
{
    public const string ControllerId = "33333333-3333-3333-3333-333333333333";
    public const string ManagerId = "44444444-4444-4444-4444-444444444444";
    public const string TypeId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private readonly JsonObject nodes;
    private readonly FixtureProject fixture;
    public FixtureConfiguration(FixtureProject project, TemplateDefinition? template = null)
    {
        fixture = project;
        nodes = project.Configuration?.DeepClone().AsObject() ?? new JsonObject();
        if (nodes.Count == 0)
        {
            Add(ControllerId, "Controller", "device", "project")["identification"] = J(template?.Controller ?? new DeviceRequirement(4096, "fixture-controller", "1.0"));
            Node(ControllerId)["module"] = ""; Node(ControllerId)["parameters"] = Parameters();
            Add(project.ApplicationId, "Application", "application", ControllerId);
            Add(ManagerId, "LibraryManager", "library_manager", project.ApplicationId)["references"] = new JsonArray(
                (template?.RequiredLibraries ?? []).Select(name => (JsonNode?)Reference(name)).ToArray());
        }
        foreach (var pou in project.Objects ?? [new(project.ProgramId, "PLC_PRG", "pou", project.Declaration, project.Implementation, "ST")])
        {
            if (nodes[pou.Id] is not JsonObject n) n = Add(pou.Id, pou.Name, pou.Kind, project.ApplicationId);
            n["declaration"] = pou.Declaration; n["implementation"] = pou.Implementation;
        }
    }
    public FixtureProject Save()
    {
        foreach (var node in nodes.Select(x => x.Value!.AsObject()).Where(n => S(n, "kind") == "Property").ToArray())
            foreach (var (name, field) in new[] { ("Get", "getter"), ("Set", "setter") })
                node[field] = Children(S(node, "objectId")).Select(Node).SingleOrDefault(n => S(n, "name") == name)?.DeepClone();
        return fixture with { Configuration = nodes.DeepClone().AsObject(), Objects =
            (fixture.Objects ?? [new(fixture.ProgramId, "PLC_PRG", "pou", fixture.Declaration, fixture.Implementation, "ST")])
                .Where(x => nodes.ContainsKey(x.Id)).Select(x => x with { Name = S(Node(x.Id), "name") }).ToArray() };
    }
    private JsonObject MemberView(string id)
    {
        var result = Node(id).DeepClone().AsObject();
        if (S(result, "kind") == "Property")
            foreach (var (name, field) in new[] { ("Get", "getter"), ("Set", "setter") })
                result[field] = Children(id).Select(Node).SingleOrDefault(n => S(n, "name") == name)?.DeepClone();
        return result;
    }
    public JsonObject Node(string id) => nodes[id]?.AsObject() ?? throw new ProjectEditException("object_not_found");
    private static JsonNode? J<T>(T value) => JsonSerializer.SerializeToNode(value, WorkerJson.Options);
    private static string S(JsonObject node, string key) => node[key]!.GetValue<string>();
    private JsonObject Add(string id, string name, string kind, string? parent)
    {
        var n = new JsonObject { ["objectId"] = id, ["name"] = name, ["kind"] = kind, ["parentId"] = parent, ["typeId"] = TypeId, ["index"] = parent is null ? -1 : Children(parent).Count() };
        nodes[id] = n; return n;
    }
    private string Create(string name, string kind, string parent)
    {
        if (parent != "project") _ = Node(parent);
        Unique(parent, name); var id = Guid.NewGuid().ToString(); Add(id, name, kind, parent); return id;
    }
    private void Unique(string parent, string name, string? except = null)
    {
        if (nodes.Any(k => k.Key != except && k.Value!["parentId"]?.GetValue<string>() == parent && string.Equals(k.Value["name"]!.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase)))
            throw new ProjectEditException("object_name_conflict");
    }
    private IEnumerable<string> Children(string parent) => nodes.Where(k => k.Value!["parentId"]?.GetValue<string>() == parent).OrderBy(k => k.Value!["index"]?.GetValue<int>() ?? 0).Select(k => k.Key);
    private string[] Descendants(string key) => new[] { key }.Concat(Children(key).SelectMany(Descendants)).ToArray();
    private void Kind(string id, params string[] kinds)
    { if (!kinds.Contains(S(Node(id), "kind"))) throw new ProjectEditException("object_kind_mismatch"); }
    private static JsonObject Reference(string name, string? resolution = null) => new() {
        ["managerId"] = ManagerId, ["name"] = name, ["namespace"] = "Fixture", ["isPlaceholder"] = name.StartsWith('#'), ["systemLibrary"] = false,
        ["defaultResolution"] = resolution, ["effectiveResolution"] = resolution, ["redirection"] = name.StartsWith('#') ? "" : null };
    private static JsonArray Parameters() => new(new JsonObject { ["address"] = J(new ParameterAddress(1, Elements: [])), ["value"] = "1", ["type"] = "INT", ["access"] = 3,
        ["readable"] = true, ["writable"] = true, ["variable"] = "", ["createsVariable"] = false, ["mapsExistingVariable"] = false });
    private static readonly string[] InstalledNames = ["Base, 1.0 (Fixture)", "Extra, 1.0 (Fixture)"];
    private static void Installed(string name)
    { if (!InstalledNames.Contains(name)) throw new ProjectEditException("library_not_installed"); }
    public DependencySnapshot Dependencies() => new(
        nodes.Where(x => S(x.Value!.AsObject(), "kind") == "device").OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new DeviceReference(x.Key,
            x.Value!["identification"]!.Deserialize<DeviceRequirement>(WorkerJson.Options)!, x.Value["module"]!.GetValue<string>())).ToArray(),
        nodes.Where(x => S(x.Value!.AsObject(), "kind") == "library_manager").OrderBy(x => x.Key, StringComparer.Ordinal).SelectMany(x => x.Value!["references"]!.AsArray()
            .OrderBy(r => r!["name"]!.GetValue<string>(), StringComparer.Ordinal).Select(r => r!.Deserialize<LibraryReference>(WorkerJson.Options)!)).ToArray());
    private JsonObject TaskView(string id)
    {
        var result = Node(id).DeepClone().AsObject();
        foreach (var item in result["calls"]!.AsArray())
        {
            var call = item!.AsObject(); var name = call["pouName"]!.GetValue<string>();
            var application = id;
            while (S(Node(application), "kind") != "application") application = S(Node(application), "parentId");
            var matches = Descendants(application).Where(key => Node(key)["name"]?.GetValue<string>() == name
                && Node(key)["declaration"]?.GetValue<string>().StartsWith("PROGRAM ", StringComparison.OrdinalIgnoreCase) == true).ToArray();
            call["resolved"] = matches.Length == 1; call["pouId"] = matches.Length == 1 ? matches[0] : null;
        }
        return result;
    }
    public JsonObject Read(string op, string? id, ConfigurationQuery? query, int limit)
    {
        JsonObject List(string field, IEnumerable<JsonNode?> values)
        { var all = values.ToArray(); return new() { [field] = new JsonArray(all.Take(limit).Select(x => x?.DeepClone()).ToArray()), ["truncated"] = all.Length > limit }; }
        switch (op)
        {
            case C.LibraryInstalled: return List("libraries", InstalledNames.Select(n => new JsonObject { ["name"] = n, ["version"] = "1.0", ["company"] = "Fixture" }));
            case C.DeviceTree: return List("devices", nodes.Where(x => S(x.Value!.AsObject(), "kind") == "device").Select(x => x.Value));
            case C.TaskList:
                Kind(id!, "application"); return List("tasks", Descendants(id!).Where(x => S(Node(x), "kind") == "task").Select(TaskView));
            case C.TaskRead: Kind(id!, "task"); return TaskView(id!);
            case C.MemberRead:
                Kind(id!, query!.MemberKind!);
                if (query.ExpectedTypeId is not null && query.ExpectedTypeId != TypeId || query.MemberKind == "Action" && query.ExpectedTypeId is null) throw new ProjectEditException("member_type_evidence_required");
                return MemberView(id!);
            case C.ObjectInspect:
                var result = id == "project" ? new JsonObject { ["name"] = "Project", ["kind"] = "project", ["objectId"] = "project" } : Node(id!).DeepClone().AsObject();
                var children = Children(id!).ToArray(); result["children"] = new JsonArray(children.Take(limit).Select(x => (JsonNode?)Node(x).DeepClone()).ToArray()); result["truncated"] = children.Length > limit; return result;
            case C.LibraryList: Kind(id!, "library_manager"); return List("references", Node(id!)["references"]!.AsArray());
            case C.ParameterRead: Kind(id!, "device"); return List("parameters", Node(id!)["parameters"]!.AsArray());
            case C.IoRead: Kind(id!, "device"); return List("mappings", Node(id!)["parameters"]!.AsArray());
            default: throw new ProjectEditException("invalid_operation");
        }
    }
    public (string Id, JsonObject Data) Apply(string op, EditInput edit)
    {
        var change = edit.Configuration; var id = edit.ObjectId!; var parent = edit.ParentId!;
        if (C.IsCreate(op))
        {
            var kind = op switch { C.TaskConfigurationCreate => "task_configuration", C.TaskCreate => "task", C.MemberCreate => change!.Member!.Kind, C.DeviceAdd => "device", _ => "folder" };
            if (op == C.TaskConfigurationCreate)
            {
                Kind(parent, "application");
                if (Children(parent).Any(x => S(Node(x), "kind") == kind)) throw new ProjectEditException("task_configuration_exists");
            }
            if (op == C.TaskCreate) Kind(parent, "task_configuration");
            if (op == C.DeviceAdd) Kind(parent, "device");
            if (op == C.MemberCreate) Kind(parent, "pou", "gvl");
            id = Create(edit.Name ?? "Task Configuration", kind, parent);
            var n = Node(id);
            if (op == C.TaskCreate) n["calls"] = new JsonArray();
            if (op == C.MemberCreate)
            {
                var m = change!.Member!;
                n["declaration"] = m.Kind == "Action" ? null : m.Kind.ToUpperInvariant() + " " + edit.Name + (m.ReturnType is null ? "" : " : " + m.ReturnType);
                n["implementation"] = m.Kind == "Property" || m.Language is not (null or "ST" or "IL") ? null : "";
                if (m.Kind == "Property") foreach (var (name, field) in new[] { ("Get", "getter"), ("Set", "setter") })
                {
                    var accessorId = Create(name, "accessor", id); var accessor = Node(accessorId);
                    accessor["ownerId"] = id; accessor["accessor"] = name;
                    accessor["declaration"] = "VAR\nEND_VAR"; accessor["implementation"] = m.Language is null or "ST" or "IL" ? "" : null;
                    n[field] = accessor.DeepClone();
                }
            }
            if (op == C.DeviceAdd)
            {
                n["identification"] = J(change!.Device!.Identification); n["module"] = change.Device.Module ?? ""; n["parameters"] = Parameters();
            }
        }
        var node = Node(id);
        switch (op)
        {
            case C.TaskCreate:
            case C.TaskUpdate:
                Kind(id, "task"); node["settings"] = J(change!.Task); break;
            case C.TaskCallsSet:
                Kind(id, "task");
                foreach (var call in change!.Calls!)
                {
                    var n = Node(call.PouId);
                    if (S(n, "name") != call.PouName || !S(n, "declaration").StartsWith("PROGRAM ", StringComparison.OrdinalIgnoreCase)) throw new ProjectEditException("task_program_resolution_conflict");
                }
                node["calls"] = J(change.Calls); break;
            case C.MemberCreate:
            case C.MemberUpdate:
                Kind(id, change!.Member!.Kind);
                if (change.Member.ExpectedTypeId is not null && change.Member.ExpectedTypeId != TypeId || op == C.MemberUpdate && change.Member.Kind == "Action" && change.Member.ExpectedTypeId is null) throw new ProjectEditException("member_type_evidence_required");
                if (edit.Declaration is not null) node["declaration"] = Normalize(edit.Declaration);
                if (edit.Implementation is not null)
                {
                    if (node["implementation"] is null) throw new ProjectEditException("textual_source_unavailable");
                    node["implementation"] = Normalize(edit.Implementation);
                }
                foreach (var (field, source) in new[] { ("getter", change.Member.Getter), ("setter", change.Member.Setter) })
                {
                    if (source is null) continue;
                    var accessorId = Children(id).SingleOrDefault(x => S(Node(x), "name") == (field == "getter" ? "Get" : "Set"))
                        ?? throw new ProjectEditException("property_accessor_unavailable");
                    var accessor = Node(accessorId);
                    if (source.Declaration is not null) accessor["declaration"] = Normalize(source.Declaration);
                    if (source.Implementation is not null)
                    {
                        if (accessor["implementation"] is null) throw new ProjectEditException("textual_source_unavailable");
                        accessor["implementation"] = Normalize(source.Implementation);
                    }
                }
                break;
            case C.ObjectRename:
            case C.ObjectMove:
            case C.ObjectDelete:
            case C.TaskDelete:
            case C.DeviceRemove:
                if (S(node, "name") != change!.Object!.ExpectedName) throw new ProjectEditException("object_name_conflict");
                if (id is ControllerId or ManagerId || S(node, "kind") is "application" or "task_configuration") throw new ProjectEditException("protected_object");
                if (op == C.TaskDelete) Kind(id, "task");
                if (op == C.DeviceRemove) Kind(id, "device");
                var removed = Descendants(id);
                if (nodes.Any(n => n.Value!["calls"] is JsonArray calls && calls.Any(c => removed.Contains(c!["pouId"]!.GetValue<string>())))) throw new ProjectEditException("task_reference_conflict");
                if (op == C.ObjectRename) { Unique(S(node, "parentId"), edit.Name!, id); node["name"] = edit.Name; }
                else if (op == C.ObjectMove)
                {
                    if (removed.Contains(parent)) throw new ProjectEditException("object_move_cycle");
                    _ = Node(parent); Unique(parent, S(node, "name"), id);
                    var targetChildren = Children(parent).Where(x => x != id).ToList(); var index = change.Object.Index;
                    if (index > targetChildren.Count) throw new ProjectEditException("invalid_move_index");
                    var oldParent = S(node, "parentId"); node["parentId"] = parent;
                    targetChildren.Insert(index == -1 ? targetChildren.Count : index, id);
                    foreach (var pair in targetChildren.Select((key, position) => (key, position))) Node(pair.key)["index"] = pair.position;
                    if (oldParent != parent) foreach (var pair in Children(oldParent).Select((key, position) => (key, position)).ToArray()) Node(pair.key)["index"] = pair.position;
                }
                else
                {
                    if (op != C.TaskDelete && removed.Length > 1 && !change.Object.Recursive) throw new ProjectEditException("recursive_confirmation_required");
                    foreach (var key in removed) nodes.Remove(key);
                    return (id, new() { ["removedIds"] = J(removed), ["referenceCheckScope"] = "task_calls_only" });
                }
                break;
            case C.LibraryAdd:
            case C.LibraryRemove:
            case C.PlaceholderSet:
                Kind(id, "library_manager"); var lib = change!.Library!; var refs = node["references"]!.AsArray();
                var name = op == C.PlaceholderSet ? "#" + lib.Name : lib.Name;
                var reference = refs.OfType<JsonObject>().SingleOrDefault(r => S(r, "name") == name);
                if (op == C.LibraryAdd)
                { Installed(name); if (reference is not null) throw new ProjectEditException("library_reference_exists"); refs.Add(Reference(name)); }
                else if (op == C.LibraryRemove)
                { if (reference is null) throw new ProjectEditException("library_reference_not_found"); refs.Remove(reference); }
                else
                {
                    if (lib.DefaultResolution is not null) Installed(lib.DefaultResolution);
                    if (!string.IsNullOrEmpty(lib.Redirection)) Installed(lib.Redirection);
                    if (reference is null)
                    {
                        if (lib.DefaultResolution is null) throw new ProjectEditException("placeholder_default_required");
                        reference = Reference(name, lib.DefaultResolution); refs.Add(reference);
                    }
                    if (lib.DefaultResolution is not null) reference["defaultResolution"] = lib.DefaultResolution;
                    if (lib.Redirection is not null) reference["redirection"] = lib.Redirection;
                    reference["effectiveResolution"] = reference["redirection"]?.GetValue<string>() is { Length: > 0 } redirect ? redirect : reference["defaultResolution"]?.GetValue<string>();
                }
                break;
            case C.ParameterWrite:
                var p = Parameter(id, change!.Parameter!.Address);
                if (!short.TryParse(change.Parameter.Value, out _)) throw new ProjectEditException("parameter_out_of_range");
                p["value"] = change.Parameter.Value; break;
            case C.IoMap:
                var m = change!.Mapping!; var mapping = Parameter(id, m.Address);
                mapping["variable"] = m.Variable; mapping["createsVariable"] = m.CreateVariable; mapping["mapsExistingVariable"] = m.Variable.Length > 0 && !m.CreateVariable; break;
        }
        return (id, Proof(op, id, edit));
    }
    private JsonObject Parameter(string id, ParameterAddress address)
    {
        Kind(id, "device");
        if (address.ParameterId != 1 || address.ConnectorId is not null || address.Elements is { Length: > 0 }) throw new ProjectEditException("parameter_not_found");
        return Node(id)["parameters"]![0]!.AsObject();
    }
    public JsonObject Proof(string op, string id, EditInput edit, JsonObject? before = null)
    {
        if (C.IsDelete(op))
        {
            if (before is null || before["removedIds"]!.AsArray().Any(k => nodes.ContainsKey(k!.GetValue<string>()))) throw new ProjectEditException("object_removal_unconfirmed");
            return before.DeepClone().AsObject();
        }
        if (op == C.ParameterWrite) return Parameter(id, edit.Configuration!.Parameter!.Address).DeepClone().AsObject();
        if (op == C.IoMap) return Parameter(id, edit.Configuration!.Mapping!.Address).DeepClone().AsObject();
        if (op is C.LibraryAdd or C.LibraryRemove or C.PlaceholderSet)
        {
            var refs = Node(id)["references"]!.AsArray(); var name = edit.Configuration!.Library!.Name;
            if (op == C.LibraryRemove)
            {
                if (refs.Any(r => r!["name"]!.GetValue<string>() == name)) throw new ProjectEditException("library_removal_unconfirmed");
                return new() { ["removedReference"] = name, ["references"] = refs.DeepClone() };
            }
            if (op == C.PlaceholderSet) name = "#" + name;
            return new() { ["reference"] = refs.Single(r => r!["name"]!.GetValue<string>() == name)!.DeepClone(), ["references"] = refs.DeepClone() };
        }
        return op is C.MemberCreate or C.MemberUpdate ? MemberView(id) : Node(id).DeepClone().AsObject();
    }
    public JsonNode[] Structure() => nodes.Select(x => (JsonNode)new JsonObject { ["id"] = x.Key, ["name"] = x.Value!["name"]!.DeepClone(), ["kind"] = x.Value["kind"]!.DeepClone(), ["parentId"] = x.Value["parentId"]?.DeepClone() }).ToArray();
    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}
