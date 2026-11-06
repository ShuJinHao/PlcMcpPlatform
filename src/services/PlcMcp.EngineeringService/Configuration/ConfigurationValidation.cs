using System.Text.RegularExpressions;
using PlcMcp.Core.Workspaces;
using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Editing;
using C = PlcMcp.Services.Contracts.Configuration.ConfigurationOperations;

namespace PlcMcp.EngineeringService.Configuration;

public static class ConfigurationValidation
{
    public static bool Identifier(string? value) => value is { Length: > 0 and <= 128 } && Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    public static bool GuidId(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty;
    private static bool Text(string? value, int max = 256) => value is { Length: > 0 } && value.Length <= max && !value.Contains('\0');
    private static bool Source(string? value) => value is null || value.Length <= 65536 && !value.Contains('\0');
    public static string? Read(string op, string? id, ConfigurationQuery? query)
    {
        if (op is not (C.LibraryInstalled or C.DeviceTree) && !(op == C.ObjectInspect && id == "project") && !GuidId(id)) return "invalid_object_identity";
        if (op == C.MemberRead && query?.MemberKind is not ("Method" or "Property" or "Action")) return "invalid_member_kind";
        if (op == C.MemberRead && query?.MemberKind == "Action" && !GuidId(query.ExpectedTypeId)) return "member_type_evidence_required";
        return null;
    }
    public static string? Write(string op, EditInput input)
    {
        if (C.IsCreate(op) && !(op == C.FolderCreate && input.ParentId == "project") && !GuidId(input.ParentId) || !C.IsCreate(op) && !GuidId(input.ObjectId)) return "invalid_object_identity";
        if (op is C.TaskCreate or C.MemberCreate or C.FolderCreate or C.DeviceAdd or C.ObjectRename && !Identifier(input.Name)) return "invalid_object_name";
        var config = input.Configuration ?? new();
        if (!Source(input.Declaration) || !Source(input.Implementation)) return "invalid_source";
        var populated = new object?[] { config.Task, config.Calls, config.Member, config.Object, config.Library, config.Device, config.Parameter, config.Mapping }.Count(x => x is not null);
        if (op == C.TaskConfigurationCreate) return populated == 0 ? null : "invalid_configuration";
        if (populated != 1) return "invalid_configuration";
        if (op is C.TaskCreate or C.TaskUpdate)
        {
            var t = config.Task;
            if (t is null || !PlcTaskRules.ValidKind(t.Kind) || !Text(t.Priority, 16) || !PlcTaskRules.ValidPriority(t.Priority) || t.CoreBinding < 0) return "invalid_task_settings";
            if (PlcTaskRules.UsesInterval(t.Kind))
            {
                if (!PlcTaskRules.PositiveNumber(t.Interval) || !PlcTaskRules.ValidUnit(t.IntervalUnit)) return "invalid_task_interval";
            }
            else if (t.Interval is not null || t.IntervalUnit is not null) return "task_field_not_applicable";
            if (t.Kind is "Event" or "Status" ? !Text(t.Event) : t.Event is not null) return "invalid_task_event";
            if (t.Kind == "ExternalEvent" ? !Text(t.ExternalEvent) : t.ExternalEvent is not null) return "invalid_task_event";
            if (t.Kind == "ParentSynchron" ? !Text(t.ParentSynchronTask) : t.ParentSynchronTask is not null) return "invalid_task_parent";
            if (t.Watchdog is { Enabled: true } w && (!PlcTaskRules.PositiveNumber(w.Time) || !PlcTaskRules.ValidUnit(w.TimeUnit) || !Text(w.Sensitivity, 16) || !uint.TryParse(w.Sensitivity, out var sensitivity) || sensitivity == 0)) return "invalid_watchdog";
            return null;
        }
        if (op == C.TaskCallsSet)
            return config.Calls is { Length: <= 128 } calls && calls.All(x => x is not null && GuidId(x.PouId) && Text(x.PouName) && x.Comment is not null && x.Comment.Length <= 1024 && !x.Comment.Contains('\0'))
                && calls.Select(x => Guid.Parse(x.PouId)).Distinct().Count() == calls.Length ? null : "invalid_task_calls";
        if (op is C.MemberCreate or C.MemberUpdate)
        {
            var m = config.Member;
            if (m is null || m.Kind is not ("Method" or "Property" or "Action") || m.Language is not (null or "ST" or "IL" or "LD" or "FBD" or "SFC" or "CFC")
                || m.ReturnType is not null && !Text(m.ReturnType)) return "invalid_member_settings";
            if (op == C.MemberUpdate && (m.Language is not null || m.ReturnType is not null)) return "member_signature_changes_require_source";
            if (op == C.MemberUpdate && m.Kind == "Action" && !GuidId(m.ExpectedTypeId)) return "member_type_evidence_required";
            if (op == C.MemberCreate && m.Kind == "Property" && !Text(m.ReturnType)) return "return_type_required";
            if (m.Kind == "Action" && (input.Declaration is not null || m.ReturnType is not null)) return "action_has_no_declaration";
            if (m.Kind == "Property" && input.Implementation is not null || m.Kind != "Property" && (m.Getter is not null || m.Setter is not null)) return "invalid_member_source";
            foreach (var a in new[] { m.Getter, m.Setter }) if (a is not null && (!Source(a.Declaration) || !Source(a.Implementation))) return "invalid_source";
            if (op == C.MemberCreate && m.Language is not (null or "ST" or "IL") && (input.Implementation is not null || m.Getter?.Implementation is not null || m.Setter?.Implementation is not null)) return "graphical_content_requires_specialized_editor";
            if (op == C.MemberUpdate && input.Declaration is null && input.Implementation is null && m.Getter is null && m.Setter is null) return "source_required";
            return null;
        }
        if (op is C.FolderCreate or C.ObjectRename or C.ObjectMove or C.ObjectDelete or C.TaskDelete or C.DeviceRemove)
        {
            if (config.Object is not { } o || o.Index < -1 || o.Index > 2000) return "invalid_object_settings";
            if (op == C.ObjectMove && input.ParentId != "project" && !GuidId(input.ParentId)) return "invalid_object_identity";
            if (op != C.FolderCreate && !Text(o.ExpectedName, 128)) return "expected_object_name_required";
            return null;
        }
        if (op is C.LibraryAdd or C.LibraryRemove or C.PlaceholderSet)
        {
            var library = config.Library;
            if (library is null || !Text(library.Name, 512) || library.DefaultResolution is not null && !Text(library.DefaultResolution, 512)
                || library.Redirection is not null && (library.Redirection.Length > 512 || library.Redirection.Contains('\0'))) return "invalid_library_settings";
            if (op == C.PlaceholderSet && (!Identifier(library.Name) || library.DefaultResolution is null && library.Redirection is null)) return "invalid_placeholder_settings";
            return null;
        }
        if (op == C.DeviceAdd)
            return config.Device is { Identification: { Type: >= 0 } d } spec && Text(d.Id) && Text(d.Version) && (spec.Module is null || Text(spec.Module)) ? null : "invalid_device_identity";
        if (op == C.ParameterWrite) return config.Parameter is { } p && Address(p.Address) && Text(p.Value, 2048) ? null : "invalid_parameter_value";
        if (op == C.IoMap) return config.Mapping is { } mapping && Address(mapping.Address) && mapping.Variable is not null && mapping.Variable.Length <= 512
            && (mapping.Variable.Length == 0 && !mapping.CreateVariable || Regex.IsMatch(mapping.Variable, "^[A-Za-z_][A-Za-z0-9_]*(?:\\[[0-9]+\\])*(?:\\.[A-Za-z_][A-Za-z0-9_]*(?:\\[[0-9]+\\])*)*$")
                && (mapping.CreateVariable ? Identifier(mapping.Variable) : mapping.Variable.Contains('.'))) ? null : "invalid_io_expression";
        return "invalid_configuration";
    }
    private static bool Address(ParameterAddress? a) => a is { ParameterId: >= 0 } && a.ConnectorId is not < 0 && (a.Elements is null || a.Elements.Length <= 32 && a.Elements.All(x => Text(x, 128)));
}
