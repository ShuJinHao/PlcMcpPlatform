using System.Text.RegularExpressions;

namespace PlcMcp.Core.Workspaces;

/// <summary>Bounded TC6 graph-v2: explicit types, label edges, in/out pins and structured SFC forks/joins.</summary>
public static class GraphV2Rules
{
    public static bool Divergence(string kind) => kind is "selectionDivergence" or "simultaneousDivergence";
    public static bool Convergence(string kind) => kind is "selectionConvergence" or "simultaneousConvergence";
    public static bool RequiresV2(GraphicalProgram p) => p.ReturnType is not null || p.PouType == "function" || p.VariableGroups is not null
        || p.Variables?.Any(v => v is not null && (v.StringLength is not null || v.Dimensions is not null || v.Initializer is not null
            || v.Address is not null || v.Documentation is not null || v.Scope is "temp" or "external")) == true
        || p.Nodes?.Any(n => n is not null && (n.Associations is not null || n.InOuts is not null || n.Priority is not null || n.Duration is not null || TimedQualifier(n.Qualifier)
            || Divergence(n.Kind) || Convergence(n.Kind) || n.Kind is "connector" or "continuation")) == true;
    public static bool TimedQualifier(string qualifier) => qualifier is "L" or "D" or "SD" or "DS" or "SL";
    // Bounded TIME literals only; expressions, variable durations and LTIME require separate evidence.
    public static bool DurationValid(string? value)
    {
        if (value is null || value.Length > 32) return false;
        var match = Regex.Match(value, @"\A(?:T|TIME)#([0-9]{1,10})(ms|s|m|h|d)\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!match.Success || !ulong.TryParse(match.Groups[1].Value, out var count)) return false;
        ulong unit = match.Groups[2].Value.ToLowerInvariant() switch { "ms" => 1, "s" => 1000, "m" => 60000, "h" => 3600000, _ => 86400000 };
        return count > 0 && count <= uint.MaxValue / unit;
    }
    public static bool TypeValid(GraphDataType t)
    {
        if (!GraphRules.Identifier(t.Type) || t.StringLength is not null && (t.Type is not ("STRING" or "WSTRING") || t.StringLength is < 1 or > 65535)) return false;
        if (t.Dimensions is null) return true;
        if (t.Dimensions.Length is < 1 or > 3) return false;
        long total = 1;
        foreach (var d in t.Dimensions)
        {
            if (d is null || d.Lower > d.Upper) return false;
            var size = (long)d.Upper - d.Lower + 1;
            if (size > 65536 || total > 65536 / size) return false;
            total *= size;
        }
        return true;
    }
    public static bool VariableReference(string? value) => value is { Length: > 0 and <= 1024 }
        && Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*(\[-?[0-9]+(?:,\s*-?[0-9]+)*\])?(\.[A-Za-z_][A-Za-z0-9_]*(\[-?[0-9]+(?:,\s*-?[0-9]+)*\])?)*$", RegexOptions.CultureInvariant);
    public static IEnumerable<GraphPin> InputPins(GraphNode n) => (n.Inputs ?? []).Concat(n.InOuts ?? []);
    public static IEnumerable<GraphPin> OutputPins(GraphNode n) => (n.Outputs ?? []).Concat(n.InOuts ?? []);
    public static string? Validate(GraphicalProgram p)
    {
        if (!GraphRules.Identifier(p.Name) || p.PouType is not ("program" or "functionBlock" or "function")) return "invalid_graph_definition";
        if (p.Language is not ("LD" or "FBD" or "SFC") || p.Language == "SFC" && p.PouType == "function") return "graph_language_outside_profile";
        if (p.PouType == "function" ? p.ReturnType is null || !TypeValid(p.ReturnType) : p.ReturnType is not null) return "invalid_graph_return_type";
        if (p.Nodes is not { Length: > 0 and <= 256 } || p.Variables is not { Length: <= 512 }
            || p.Nodes.Any(n => n is null) || p.Variables.Any(v => v is null)) return "invalid_graph_size";
        if (p.Variables.Any(v => !GraphRules.Identifier(v.Name) || !TypeValid(new(v.Type, v.StringLength, v.Dimensions))
            || !GraphDeclarationRules.Scopes.Contains(v.Scope)
            || v.InitialValue is not null && (!GraphRules.Text(v.InitialValue, 256) || v.Dimensions is not null))
            || p.Variables.Select(v => v.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != p.Variables.Length) return "invalid_graph_variables";
        if (GraphDeclarationRules.Validate(p) is { } declarationError) return declarationError;
        if (GraphInitializerRules.Validate(p.Variables) is { } initializerError) return initializerError;
        if (p.Nodes.Any(n => n.Id <= 0) || p.Nodes.Select(n => n.Id).Distinct().Count() != p.Nodes.Length) return "duplicate_or_invalid_local_id";
        if (p.Nodes.Any(n => (n.Inputs ?? []).Concat(n.Outputs ?? []).Concat(n.InOuts ?? []).Any(pin => pin is null))) return "invalid_graph_pin";
        var byId = p.Nodes.ToDictionary(n => n.Id);
        var kinds = p.Language switch {
            "LD" => new[] { "leftPowerRail", "rightPowerRail", "contact", "coil", "block", "inVariable", "outVariable", "connector", "continuation" },
            "FBD" => new[] { "block", "inVariable", "outVariable", "connector", "continuation" },
            _ => new[] { "step", "transition", "jumpStep", "actionBlock", "selectionDivergence", "selectionConvergence", "simultaneousDivergence", "simultaneousConvergence" } };
        foreach (var n in p.Nodes)
        {
            if (!kinds.Contains(n.Kind) || n.X is < 0 or > 100000 || n.Y is < 0 or > 100000) return "graph_node_outside_profile";
            var inputs = n.Inputs ?? []; var outputs = n.Outputs ?? []; var inouts = n.InOuts ?? [];
            if (inputs.Length > 64 || outputs.Length > 64 || inouts.Length > 64 || inputs.Concat(outputs).Concat(inouts).Any(x => x is null)) return "invalid_graph_pin";
            if (!Convergence(n.Kind) && inputs.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != inputs.Length
                || outputs.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputs.Length) return "duplicate_graph_pin";
            if (n.Kind == "block")
            {
                var pins = inputs.Concat(outputs).Concat(inouts).ToArray();
                if (!GraphRules.Identifier(n.TypeName) || n.InstanceName is not null && (!GraphRules.Identifier(n.InstanceName)
                    || !p.Variables.Any(v => v.Name == n.InstanceName && v.Type == n.TypeName && v.Dimensions is null))
                    || n.ExecutionOrder is null or <= 0 || outputs.Length + inouts.Length == 0 || pins.Any(x => !GraphRules.Identifier(x.Name))) return "invalid_graph_block";
                if (pins.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != pins.Length) return "duplicate_graph_pin";
                if (inouts.Any(x => x.Negated)) return "invalid_graph_inout";
            }
            else
            {
                if (inouts.Length != 0 || n.TypeName is not null || n.InstanceName is not null || inputs.Any(x => x.Name != "" || x.Negated)
                    || outputs.Any(x => x.Negated || (Divergence(n.Kind) ? !GraphRules.Identifier(x.Name) : x.Name != ""))) return "invalid_graph_pin";
                var inputCount = Convergence(n.Kind) ? inputs.Length is >= 2 and <= 64 : n.Kind == "step" && n.Initial ? inputs.Length <= 1
                    : inputs.Length == (n.Kind is "leftPowerRail" or "inVariable" or "continuation" ? 0 : 1);
                var outputCount = Divergence(n.Kind) ? outputs.Length is >= 2 and <= 64
                    : outputs.Length == (n.Kind is "outVariable" or "rightPowerRail" or "connector" or "jumpStep" or "actionBlock" ? 0 : 1);
                if (!inputCount || !outputCount) return "invalid_graph_ports";
            }
            if (n.Kind is "contact" or "coil" or "inVariable" or "outVariable" or "transition")
            { if (!GraphRules.Text(n.Expression, 1024)) return "graph_expression_required"; }
            else if (n.Expression is not null) return "graph_field_not_applicable";
            if (n.Kind is "contact" or "coil" or "outVariable")
            {
                if (!VariableReference(n.Expression)) return "invalid_graph_variable_reference";
                if (n.Kind is "contact" or "coil" && GraphRules.Identifier(n.Expression)
                    && !p.Variables.Any(v => v.Name.Equals(n.Expression, StringComparison.OrdinalIgnoreCase) && v.Type == "BOOL" && v.Dimensions is null)
                    && !(p.PouType == "function" && p.Name.Equals(n.Expression, StringComparison.OrdinalIgnoreCase) && p.ReturnType!.Type == "BOOL" && p.ReturnType.Dimensions is null)) return "graph_bool_variable_required";
            }
            if (n.Kind is "coil" or "outVariable" && n.ExecutionOrder is null or <= 0) return "graph_execution_order_required";
            if (n.ExecutionOrder is not null && n.Kind is not ("block" or "coil" or "outVariable")) return "graph_field_not_applicable";
            if (n.Edge is not ("none" or "rising" or "falling") || n.Edge != "none" && n.Kind is not ("contact" or "coil")
                || n.Storage is not ("none" or "set" or "reset") || n.Storage != "none" && n.Kind != "coil"
                || n.Negated && n.Kind is not ("contact" or "coil" or "inVariable" or "outVariable")) return "invalid_graph_modifier";
            if (n.Initial && n.Kind != "step") return "graph_field_not_applicable";
            if (n.Kind is "step" or "jumpStep" or "connector" or "continuation")
            { if (!GraphRules.Identifier(n.Name)) return "invalid_graph_node_name"; }
            else if (n.Name is not null) return "graph_field_not_applicable";
            if (GraphAssociationRules.Validate(n) is { } associationError) return associationError;
            if (n.Priority is not null && (n.Kind != "transition" || n.Priority <= 0)) return "invalid_sfc_priority";
            if (outputs.Any(x => x.Connections is { Length: > 0 })) return "connections_belong_to_inputs";
            foreach (var pin in InputPins(n))
            {
                if (pin.Connections is not { Length: > 0 and <= 64 } || pin.Connections.Any(c => c is null)
                    || pin.Connections.Distinct().Count() != pin.Connections.Length) return "invalid_graph_connection";
                if ((p.Language != "LD" || n.Kind is "block" or "connector") && pin.Connections.Length != 1) return "invalid_graph_fanin";
                foreach (var c in pin.Connections)
                {
                    if (!byId.TryGetValue(c.SourceId, out var source) || source.Id == n.Id) return "dangling_graph_connection";
                    if (!OutputPins(source).Any(x => x.Name == (c.SourcePin ?? ""))) return "unknown_graph_output_pin";
                }
            }
        }
        if (p.Nodes.Where(n => n.Kind == "actionBlock").Sum(n => GraphAssociationRules.Items(n).Length) > GraphAssociationRules.MaxPerProgram)
            return "invalid_sfc_action_associations";
        var ordered = p.Nodes.Where(n => n.ExecutionOrder is not null).Select(n => n.ExecutionOrder).ToArray();
        if (ordered.Any(x => x <= 0) || ordered.Distinct().Count() != ordered.Length) return "graph_execution_order_conflict";
        var actions = p.Actions ?? [];
        if (actions.Length > 256 || actions.Any(a => a is null || !GraphRules.Identifier(a.Name) || !GraphRules.Text(a.Implementation, 8192))
            || actions.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != actions.Length) return "invalid_sfc_action";
        if (p.Language == "SFC") return GraphSfcRules.Validate(p);
        if (actions.Length > 0) return "graph_field_not_applicable";
        if (p.Language == "LD" && (!p.Nodes.Any(n => n.Kind == "coil") || !p.Nodes.Any(n => n.Kind == "leftPowerRail") || !p.Nodes.Any(n => n.Kind == "rightPowerRail"))) return "incomplete_ld_network";
        var labels = p.Nodes.Where(n => n.Kind == "connector").GroupBy(n => n.Name!, StringComparer.OrdinalIgnoreCase).ToArray();
        if (labels.Any(g => g.Count() != 1)) return "ambiguous_graph_connector";
        var connectors = labels.ToDictionary(g => g.Key, g => g.Single(), StringComparer.OrdinalIgnoreCase);
        if (p.Nodes.Any(n => n.Kind == "continuation" && !connectors.ContainsKey(n.Name!))) return "unresolved_graph_continuation";
        // Implicit label edges participate in BOTH cycle and transitive execution-order checks.
        int[] Parents(GraphNode n) => n.Kind == "continuation" ? [connectors[n.Name!].Id]
            : InputPins(n).SelectMany(x => x.Connections ?? []).Select(c => c.SourceId).Distinct().ToArray();
        var visited = new HashSet<int>(); var active = new HashSet<int>();
        bool Cycle(int id)
        {
            if (active.Contains(id)) return true;
            if (!visited.Add(id)) return false;
            active.Add(id); if (Parents(byId[id]).Any(Cycle)) return true; active.Remove(id); return false;
        }
        if (p.Nodes.Any(n => Cycle(n.Id))) return "graph_cycle_requires_state_variable";
        foreach (var n in p.Nodes.Where(n => n.ExecutionOrder is not null))
        {
            var seen = new HashSet<int>(); var pending = new Stack<int>(Parents(n));
            while (pending.TryPop(out var id))
            {
                if (!seen.Add(id)) continue;
                if (byId[id].ExecutionOrder >= n.ExecutionOrder) return "graph_execution_order_conflict";
                foreach (var parent in Parents(byId[id])) pending.Push(parent);
            }
        }
        foreach (var pin in p.Nodes.SelectMany(n => n.InOuts ?? []))
        {
            var c = pin.Connections![0]; var source = byId[c.SourceId]; var seen = new HashSet<int>();
            while (source.Kind == "continuation" && seen.Add(source.Id))
            { var link = connectors[source.Name!].Inputs![0].Connections![0]; source = byId[link.SourceId]; }
            if (source.Kind != "inVariable" || !VariableReference(source.Expression) || source.Negated) return "graph_inout_requires_variable";
        }
        return null;
    }
}
