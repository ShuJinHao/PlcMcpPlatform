using System.Text.RegularExpressions;

namespace PlcMcp.Core.Workspaces;

// Vendor-neutral, deliberately bounded graph editing profile. Arbitrary vendor XML is a separate exchange operation.
public sealed record GraphicalProgram(string Name, string PouType, string Language, GraphVariable[] Variables,
    GraphNode[] Nodes, GraphAction[]? Actions = null);
public sealed record GraphVariable(string Name, string Type, string Scope = "local", string? InitialValue = null);
public sealed record GraphAction(string Name, string Implementation);
public sealed record GraphConnection(int SourceId, string? SourcePin = null);
public sealed record GraphPin(string Name, GraphConnection[]? Connections = null, bool Negated = false);
public sealed record GraphNode(int Id, string Kind, decimal X = 0, decimal Y = 0, string? Expression = null,
    string? TypeName = null, string? InstanceName = null, GraphPin[]? Inputs = null, GraphPin[]? Outputs = null,
    bool Negated = false, string Edge = "none", string Storage = "none", int? ExecutionOrder = null,
    string? Name = null, bool Initial = false, string? Action = null, string Qualifier = "N");

public static class GraphRules
{
    public static bool Identifier(string? value) => value is { Length: > 0 and <= 128 }
        && Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);
    public static bool Text(string? value, int max = 4096) => value is { Length: > 0 } && value.Length <= max && !value.Contains('\0');
    public static string? Validate(GraphicalProgram? program)
    {
        if (program is null || !Identifier(program.Name) || program.PouType is not ("program" or "functionBlock")) return "invalid_graph_definition";
        if (program.Language is not ("LD" or "FBD" or "SFC")) return "graph_language_outside_profile";
        if (program.Nodes is not { Length: > 0 and <= 256 } || program.Variables is not { Length: <= 512 }
            || program.Nodes.Any(n => n is null) || program.Variables.Any(v => v is null)) return "invalid_graph_size";
        var variables = program.Variables;
        if (variables.Any(v => !Identifier(v.Name) || !Identifier(v.Type) || v.Scope is not ("local" or "input" or "output" or "inOut")
            || v.InitialValue is not null && !Text(v.InitialValue, 256))
            || variables.Select(v => v.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != variables.Length) return "invalid_graph_variables";
        var nodes = program.Nodes;
        if (nodes.Any(n => n.Id <= 0) || nodes.Select(n => n.Id).Distinct().Count() != nodes.Length) return "duplicate_or_invalid_local_id";
        var byId = nodes.ToDictionary(n => n.Id);
        var kinds = program.Language switch {
            "LD" => new[] { "leftPowerRail", "rightPowerRail", "contact", "coil", "block", "inVariable", "outVariable" },
            "FBD" => new[] { "block", "inVariable", "outVariable" },
            _ => new[] { "step", "transition", "jumpStep", "actionBlock" } };
        foreach (var n in nodes)
        {
            if (!kinds.Contains(n.Kind) || n.X is < 0 or > 100000 || n.Y is < 0 or > 100000) return "graph_node_outside_profile";
            var inputs = n.Inputs ?? []; var outputs = n.Outputs ?? [];
            if (inputs.Length > 64 || outputs.Length > 64 || inputs.Concat(outputs).Any(p => p is null)) return "invalid_graph_pin";
            if (inputs.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != inputs.Length
                || outputs.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputs.Length) return "duplicate_graph_pin";
            if (n.Kind == "block")
            {
                if (!Identifier(n.TypeName) || n.InstanceName is not null && (!Identifier(n.InstanceName)
                    || !variables.Any(v => v.Name == n.InstanceName && v.Type == n.TypeName))
                    || n.ExecutionOrder is null or <= 0 || outputs.Length == 0 || inputs.Concat(outputs).Any(p => !Identifier(p.Name))) return "invalid_graph_block";
            }
            else
            {
                if (inputs.Length > 1 || outputs.Length > 1 || inputs.Concat(outputs).Any(p => p.Name != "" || p.Negated)
                    || n.TypeName is not null || n.InstanceName is not null) return "invalid_graph_pin";
                var needsInput = n.Kind is "contact" or "coil" or "outVariable" or "rightPowerRail" or "transition" or "jumpStep" or "actionBlock" || n.Kind == "step" && !n.Initial;
                var needsOutput = n.Kind is "leftPowerRail" or "contact" or "coil" or "inVariable" or "step" or "transition";
                if (needsInput && inputs.Length != 1 || needsOutput != (outputs.Length == 1)
                    || n.Kind is "leftPowerRail" or "inVariable" && inputs.Length != 0) return "invalid_graph_ports";
            }
            if (n.Kind is "contact" or "coil" or "inVariable" or "outVariable" or "transition")
            { if (!Text(n.Expression, 1024)) return "graph_expression_required"; }
            else if (n.Expression is not null) return "graph_field_not_applicable";
            if (n.Kind is "contact" or "coil")
            { if (!Regex.IsMatch(n.Expression!, "^[A-Za-z_][A-Za-z0-9_]*(\\.[A-Za-z_][A-Za-z0-9_]*)*$")) return "invalid_graph_variable_reference"; }
            if (n.Edge is not ("none" or "rising" or "falling") || n.Edge != "none" && n.Kind is not ("contact" or "coil")
                || n.Storage is not ("none" or "set" or "reset") || n.Storage != "none" && n.Kind != "coil"
                || n.Negated && n.Kind is not ("contact" or "coil" or "inVariable" or "outVariable")) return "invalid_graph_modifier";
            if (n.Kind is "step" or "jumpStep") { if (!Identifier(n.Name)) return "invalid_step_name"; }
            else if (n.Name is not null || n.Initial) return "graph_field_not_applicable";
            if (n.Kind == "actionBlock")
            { if (!Identifier(n.Action) || n.Qualifier is not ("N" or "S" or "R" or "P")) return "invalid_sfc_action"; }
            else if (n.Action is not null || n.Qualifier != "N") return "graph_field_not_applicable";
            if (outputs.Any(p => p.Connections is { Length: > 0 })) return "connections_belong_to_inputs";
            foreach (var pin in inputs)
            {
                if (pin.Connections is not { Length: > 0 and <= 64 } || pin.Connections.Any(c => c is null)
                    || pin.Connections.Distinct().Count() != pin.Connections.Length) return "invalid_graph_connection";
                if (program.Language != "LD" && pin.Connections.Length != 1 || n.Kind == "block" && pin.Connections.Length != 1) return "invalid_graph_fanin";
                foreach (var c in pin.Connections)
                {
                    if (!byId.TryGetValue(c.SourceId, out var source) || c.SourceId == n.Id) return "dangling_graph_connection";
                    if (!(source.Outputs ?? []).Any(p => p.Name == (c.SourcePin ?? ""))) return "unknown_graph_output_pin";
                    if (program.Language == "SFC" && (n.Kind is "transition" or "actionBlock" ? source.Kind != "step" : source.Kind != "transition")) return "invalid_sfc_topology";
                    if (source.ExecutionOrder is { } prior && n.ExecutionOrder is { } next && prior >= next) return "graph_execution_order_conflict";
                }
            }
        }
        var ordered = nodes.Where(n => n.ExecutionOrder is not null).Select(n => n.ExecutionOrder).ToArray();
        if (ordered.Any(x => x <= 0) || ordered.Distinct().Count() != ordered.Length) return "graph_execution_order_conflict";
        var actions = program.Actions ?? [];
        if (actions.Any(a => a is null || !Identifier(a.Name) || !Text(a.Implementation, 8192))
            || actions.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != actions.Length) return "invalid_sfc_action";
        if (program.Language == "SFC")
        {
            var steps = nodes.Where(n => n.Kind == "step").ToArray();
            if (steps.Count(n => n.Initial) != 1 || steps.Select(n => n.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != steps.Length
                || nodes.Any(n => n.Kind == "jumpStep" && !steps.Any(s => s.Name == n.Name))
                || nodes.Any(n => n.Kind == "actionBlock" && !actions.Any(a => a.Name == n.Action))) return "invalid_sfc_topology";
        }
        else
        {
            if (actions.Length > 0) return "graph_field_not_applicable";
            var visited = new HashSet<int>(); var active = new HashSet<int>();
            bool Cycle(int id)
            {
                if (active.Contains(id)) return true;
                if (!visited.Add(id)) return false;
                active.Add(id);
                if ((byId[id].Inputs ?? []).SelectMany(p => p.Connections ?? []).Any(c => Cycle(c.SourceId))) return true;
                active.Remove(id); return false;
            }
            if (nodes.Any(n => Cycle(n.Id))) return "graph_cycle_requires_state_variable";
        }
        return null;
    }
}
