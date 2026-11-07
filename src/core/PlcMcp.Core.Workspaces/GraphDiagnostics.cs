namespace PlcMcp.Core.Workspaces;

/// <summary>Explains a rejected graph without changing validation, rewriting input, or guessing an offending index.</summary>
public static class GraphDiagnostics
{
    public static string? ExplainWrite(string code) => code switch
    {
        "graph_parent_required" => "parentId: Select the observed Application or supported container GUID, not project.",
        "expected_revision_required" => "expectedRevision: Supply the current managed workspace m2 revision returned by the server.",
        "expected_graph_revision_required" => "objectId / expectedGraphSha256: Replacement needs an observed object GUID and the 64-character semantic hash from its graph read.",
        "artifact_unavailable" or "artifact_integrity_mismatch" => "artifactId: Read and validate the immutable artifact before writing. Do not substitute another artifact or replay a write whose outcome is unknown.",
        "graph_contains_unrepresented_data" or "single_graph_pou_required" or "graph_language_outside_profile" => "artifactId: Decode must yield exactly one supported graph without unrepresented data. Preserve the original artifact and use opaque export/import where required.",
        _ => null
    };

    public static string Explain(GraphicalProgram? p, string code)
    {
        static string At(string path, string rule) => path + ": " + rule;
        if (p is null) return At("program", "Supply a graph object with name, pouType, language, variables and nodes.");
        if (code == "invalid_graph_definition")
        {
            if (!GraphRules.Identifier(p.Name)) return At("program.name", "Use an IEC identifier of 1..128 letters, digits or underscores, starting with a letter or underscore.");
            return At("program.pouType", p.Profile == "graph-v2"
                ? "Use exactly program, functionBlock or function (case-sensitive). Program and PROGRAM are not accepted; a function also needs returnType."
                : "Use exactly program or functionBlock (case-sensitive). Use graph-v2 with function and returnType for functions.");
        }
        if (DeclarationProblem(p, code) is { } declaration) return declaration;
        if (code == "graph_profile_mismatch") return At("program.profile", "Use graph-v2 for extended declarations, associations, arrays, types and SFC branches; graph-v1 remains a separate bounded profile.");
        if (code == "invalid_graph_return_type") return At("program.returnType", "A function needs one valid return type; programs and function blocks must omit returnType.");
        if (code == "graph_language_outside_profile") return At("program.language", "Use LD, FBD or SFC; SFC functions and CFC graphs are outside this profile.");
        if (code == "invalid_graph_size")
        {
            if (p.Nodes is not { Length: > 0 and <= 256 }) return At("program.nodes", "Supply 1..256 non-null nodes.");
            if (p.Variables is not { Length: <= 512 }) return At("program.variables", "Supply at most 512 non-null variables.");
            var index = Array.FindIndex(p.Nodes, n => n is null);
            if (index >= 0) return At($"program.nodes[{index}]", "Null is not a node; supply the complete node or remove the entry intentionally.");
            index = Array.FindIndex(p.Variables, v => v is null);
            if (index >= 0) return At($"program.variables[{index}]", "Null is not a variable declaration.");
        }
        // The diagnostic pass has its own bounds and does not trust failed input.
        if (p.Nodes is not { Length: <= 256 } || p.Nodes.Any(n => n is null)) return At("program.nodes", Guidance(code));
        for (var i = 0; i < p.Nodes.Length; i++)
        {
            var n = p.Nodes[i]; var path = $"program.nodes[{i}]";
            var inputs = n.Inputs ?? []; var outputs = n.Outputs ?? []; var inouts = n.InOuts ?? [];
            if (code == "graph_node_outside_profile")
            {
                var kinds = NodeKinds(p.Language, p.Profile == "graph-v2");
                if (!kinds.Contains(n.Kind)) return At(path + ".kind", "Use exactly one of these " + p.Language + " kinds (case-sensitive): " + string.Join(", ", kinds) + ".");
                if (n.X is < 0 or > 100000) return At(path + ".x", "Use a coordinate from 0 through 100000.");
                if (n.Y is < 0 or > 100000) return At(path + ".y", "Use a coordinate from 0 through 100000.");
            }
            if (code == "invalid_graph_pin" && n.Kind != "block")
            {
                if (n.TypeName is not null) return At(path + ".typeName", "Only block nodes accept typeName. Omit it on inVariable/outVariable and other non-block nodes; declare variable types in program.variables.");
                if (n.InstanceName is not null) return At(path + ".instanceName", "Only block nodes accept instanceName. Use expression to reference a variable on inVariable/outVariable.");
            }
            foreach (var (pins, field) in new[] { (inputs, "inputs"), (outputs, "outputs"), (inouts, "inOuts") })
            {
                if (code == "invalid_graph_pin" && pins.Length > 64) return At(path + "." + field, "At most 64 pins are allowed.");
                if (pins.Length > 64) continue;
                for (var j = 0; j < pins.Length; j++)
                {
                    var pin = pins[j]; var pinPath = path + $".{field}[{j}]";
                    if (pin is null)
                    { if (code == "invalid_graph_pin") return At(pinPath, "A pin must be an object; non-block pins use name ''."); continue; }
                    if (code == "invalid_graph_pin" && n.Kind != "block" &&
                        (field == "inOuts" || pin.Negated || (field == "outputs" && GraphV2Rules.Divergence(n.Kind) ? !GraphRules.Identifier(pin.Name) : pin.Name != "")))
                        return At(pinPath, "Non-block pins use name '' and cannot be negated or inOuts; SFC divergence outputs instead need unique identifier names.");
                    if (code == "connections_belong_to_inputs" && field == "outputs" && pin.Connections is { Length: > 0 })
                        return At(pinPath + ".connections", "Move the connection definition to the receiving input pin; outputs describe ports only.");
                    if (field == "outputs") continue;
                    var links = pin.Connections;
                    if (code == "invalid_graph_connection" && (links is not { Length: > 0 and <= 64 } || links.Any(c => c is null) || links.Distinct().Count() != links.Length))
                        return At(pinPath + ".connections", "Supply 1..64 distinct non-null connections containing sourceId and optional sourcePin.");
                    if (code == "invalid_graph_fanin" && links is { Length: > 1 } && (p.Language != "LD" || n.Kind is "block" or "connector"))
                        return At(pinPath + ".connections", "This input accepts exactly one connection.");
                    if (links is not { Length: <= 64 }) continue;
                    for (var k = 0; k < links.Length; k++)
                    {
                        var c = links[k]; if (c is null) continue;
                        var source = p.Nodes.FirstOrDefault(s => s.Id == c.SourceId);
                        if (code == "dangling_graph_connection" && (source is null || source.Id == n.Id))
                            return At(pinPath + $".connections[{k}].sourceId", "Reference a different, existing node ID; do not invent identities.");
                        if (code == "unknown_graph_output_pin" && source is not null &&
                            !(source.Outputs ?? []).Concat(source.InOuts ?? []).Any(s => s is not null && s.Name == (c.SourcePin ?? "")))
                            return At(pinPath + $".connections[{k}].sourcePin", "Use a name exposed by the source node; omit sourcePin for an unnamed output.");
                    }
                }
            }
            if (code == "invalid_graph_ports" && n.Kind != "block")
            {
                var validInput = GraphV2Rules.Convergence(n.Kind) ? inputs.Length is >= 2 and <= 64 : n.Kind == "step" && n.Initial ? inputs.Length <= 1
                    : inputs.Length == (n.Kind is "leftPowerRail" or "inVariable" or "continuation" ? 0 : 1);
                var validOutput = GraphV2Rules.Divergence(n.Kind) ? outputs.Length is >= 2 and <= 64
                    : outputs.Length == (n.Kind is "outVariable" or "rightPowerRail" or "connector" or "jumpStep" or "actionBlock" ? 0 : 1);
                if (!validInput) return At(path + ".inputs", "Use one input, except input sources use none, an initial step allows zero or one, and convergences need 2..64.");
                if (!validOutput) return At(path + ".outputs", "Step/transition/contact/coil/input sources need one output; actionBlock/jumpStep/output sinks need none; divergences need 2..64 named outputs.");
            }
            if ((code == "invalid_graph_block" && n.Kind == "block" || code == "graph_execution_order_required" && n.Kind is "coil" or "outVariable")
                && n.ExecutionOrder is null or <= 0)
                return At(path + ".executionOrder", "Supply a positive integer for a block, coil or outVariable; use a unique value consistent with data flow.");
            if (code == "graph_field_not_applicable")
            {
                if (n.ExecutionOrder is not null && n.Kind is not ("block" or "coil" or "outVariable")) return At(path + ".executionOrder", "Omit executionOrder on this node kind; SFC association order belongs inside associations, not on the actionBlock.");
                if (n.Expression is not null && n.Kind is not ("contact" or "coil" or "inVariable" or "outVariable" or "transition")) return At(path + ".expression", "This node kind has no expression; omit the field.");
                if (n.Initial && n.Kind != "step") return At(path + ".initial", "Only a step can be initial.");
                if (n.Name is not null && n.Kind is not ("step" or "jumpStep" or "connector" or "continuation")) return At(path + ".name", "This node kind has no name field; omit it.");
                if (n.Kind != "actionBlock")
                {
                    if (n.Qualifier != "N") return At(path + ".qualifier", "Only actionBlock accepts an action qualifier. Omit the field on this node; do not send null.");
                    if (n.Associations is not null || n.Action is not null || n.Duration is not null) return At(path, "action, associations and duration belong only on an actionBlock; omit unused fields.");
                }
            }
            if (code == "invalid_graph_modifier")
            {
                if (n.Edge is not ("none" or "rising" or "falling") || n.Edge != "none" && n.Kind is not ("contact" or "coil")) return At(path + ".edge", "Use none/rising/falling on contact/coil; otherwise omit edge. Null is not a default value.");
                if (n.Storage is not ("none" or "set" or "reset") || n.Storage != "none" && n.Kind != "coil") return At(path + ".storage", "Use none/set/reset on coil; otherwise omit storage. Null is not a default value.");
            }
            if (n.Kind != "actionBlock") continue;
            if (code == "ambiguous_sfc_action_associations" && n.Associations is not null && (n.Action is not null || n.Qualifier != "N" || n.Duration is not null))
                return At(path + ".associations", "Choose associations or the legacy action/qualifier/duration form. When using associations omit the legacy fields entirely.");
            var items = GraphAssociationRules.Items(n);
            if (code == "invalid_sfc_action_associations" && items.Length is < 1 or > GraphAssociationRules.MaxPerBlock) return At(path + ".associations", "Use 1..64 associations per actionBlock and at most 256 per program.");
            for (var j = 0; j < items.Length; j++)
            {
                var a = items[j]; var ap = n.Associations is null ? path : path + $".associations[{j}]";
                if (code == "invalid_sfc_action_associations" && (a is null || a.Id <= 0 || items.Take(j).Any(prior => prior is not null && prior.Id == a.Id)))
                    return At(ap, "Supply a non-null association with a unique positive id within this actionBlock.");
                if (a is null) continue;
                if (code == "invalid_sfc_action" && !GraphRules.Identifier(a.Action)) return At(ap + ".action", "Supply the name of an entry in program.actions.");
                if (code == "invalid_sfc_action" && a.Qualifier is not ("N" or "S" or "R" or "P") && !GraphV2Rules.TimedQualifier(a.Qualifier))
                    return At(ap + ".qualifier", "Use N/S/R/P or timed L/D/SD/DS/SL. Omit qualifier to use N; do not send null.");
                if (code == "invalid_sfc_duration" && (GraphV2Rules.TimedQualifier(a.Qualifier) ? !GraphV2Rules.DurationValid(a.Duration) : a.Duration is not null))
                    return At(ap + ".duration", "Timed qualifiers require a positive bounded TIME literal such as T#100ms. Omit duration for N/S/R/P.");
                if (code == "invalid_sfc_topology" && !(p.Actions ?? []).Any(action => action is not null && action.Name == a.Action))
                    return At(ap + ".action", "The referenced action is absent from program.actions; add its matching name and ST implementation before generating the graph.");
            }
        }
        return At(code.StartsWith("invalid_sfc_action", StringComparison.Ordinal) ? "program.actions" : "program", Guidance(code));
    }

    private static string[] NodeKinds(string language, bool v2) => language switch
    {
        "LD" => v2 ? ["leftPowerRail", "rightPowerRail", "contact", "coil", "block", "inVariable", "outVariable", "connector", "continuation"]
            : ["leftPowerRail", "rightPowerRail", "contact", "coil", "block", "inVariable", "outVariable"],
        "FBD" => v2 ? ["block", "inVariable", "outVariable", "connector", "continuation"] : ["block", "inVariable", "outVariable"],
        "SFC" => v2 ? ["step", "transition", "jumpStep", "actionBlock", "selectionDivergence", "selectionConvergence", "simultaneousDivergence", "simultaneousConvergence"]
            : ["step", "transition", "jumpStep", "actionBlock"],
        _ => []
    };

    private static string? DeclarationProblem(GraphicalProgram p, string code)
    {
        if (p.Variables is not { Length: <= 512 } || p.Variables.Any(v => v is null)) return null;
        for (var i = 0; i < p.Variables.Length; i++)
        {
            var v = p.Variables[i]; var path = $"program.variables[{i}]";
            if (code == "invalid_graph_variables")
            {
                if (!GraphRules.Identifier(v.Name) || p.Variables.Take(i).Any(other => other.Name.Equals(v.Name, StringComparison.OrdinalIgnoreCase)))
                    return path + ".name: Supply a unique IEC identifier; variable names are compared without case.";
                if (!GraphDeclarationRules.Scopes.Contains(v.Scope)) return path + ".scope: Use local/input/output/inOut/temp/external for graph-v2.";
                if (!GraphCompositeTypes.Valid(GraphCompositeTypes.TypeOf(v))) return TypeProblem(GraphCompositeTypes.TypeOf(v), path);
                if (v.InitialValue is not null && (!GraphRules.Text(v.InitialValue, 256) || v.Dimensions is not null))
                    return path + ".initialValue: Use a bounded scalar value; arrays require a structured initializer.";
            }
            if (code == "invalid_graph_address" && v.Address is { } address && !GraphDeclarationRules.AddressValid(address))
                return path + ".address: Use an explicit bounded %I/%Q/%M address. Wildcards and AT text are outside this profile.";
            if (code == "invalid_graph_documentation" && !GraphDocumentation.Valid(v.Documentation, v.DocumentationXhtml))
                return DocumentationProblem(path, v.Documentation, v.DocumentationXhtml);
            if (code is "invalid_graph_initializer" or "graph_initializer_unknown_member" or "graph_initializer_exceeds_array" or "graph_initializer_too_large"
                && GraphCompositeTypes.Valid(GraphCompositeTypes.TypeOf(v)) && GraphInitializerRules.Validate([v]) == code)
            {
                var nested = GraphInitializerRules.Validate([v with { InitialValue = null, Initializer = null }]) == code;
                var field = nested ? v.StructMembers is not null ? ".structMembers" : v.PointerTo is not null ? ".pointerTo" : ""
                    : v.Initializer is not null ? ".initializer" : ".initialValue";
                return path + field + ": Check scalar/array/struct shape, declared member names, capacity and bounded nesting. Do not combine initialValue and initializer.";
            }
        }
        if (code == "invalid_graph_documentation" && p.VariableGroups is { Length: <= GraphDeclarationRules.MaxGroups } groups)
            for (var i = 0; i < groups.Length; i++)
                if (groups[i] is { } group && !GraphDocumentation.Valid(group.Documentation, group.DocumentationXhtml))
                    return DocumentationProblem($"program.variableGroups[{i}]", group.Documentation, group.DocumentationXhtml);
        return null;
    }

    private static string DocumentationProblem(string path, string? plain, string? xhtml) => path +
        (plain is not null && xhtml is not null ? ".documentation / documentationXhtml: Supply either plain text or one XHTML fragment; never both."
            : xhtml is not null ? ".documentationXhtml: Supply one bounded XHTML-namespace element. DTDs, external entities and unsupported XML nodes are rejected."
            : ".documentation: Supply bounded XML-safe text.");

    private static string TypeProblem(GraphDataType? type, string path, int depth = 1)
    {
        if (type is null) return path + ": Supply a type object such as {type:'INT'}, not null.";
        if (depth > GraphCompositeTypes.MaxDepth) return path + ": Type nesting exceeds 8 levels.";
        if (!GraphRules.Identifier(type.Type)) return path + ".type: Supply an IEC type name.";
        var forms = (type.StructMembers is null ? 0 : 1) + (type.PointerTo is null ? 0 : 1) + (type.EnumValues is null ? 0 : 1) + (type.Subrange is null ? 0 : 1);
        if (forms > 1) return path + (type.StructMembers is not null ? ".structMembers" : type.PointerTo is not null ? ".pointerTo" : ".enumValues")
            + ": structMembers, pointerTo, enumValues and subrange are mutually exclusive type forms; keep exactly the intended form.";
        if (type.StringLength is not null && (forms > 0 || type.Type is not ("STRING" or "WSTRING") || type.StringLength is < 1 or > 65535))
            return path + ".stringLength: Use 1..65535 only with STRING/WSTRING; omit it for composite types.";
        if (!GraphInlineTypes.Valid(type)) return path + (type.EnumValues is not null ? ".enumValues" : ".subrange") + ": Check unique enum names and decimal values/bounds within the integer base type.";
        if (type.StructMembers is { } members)
        {
            if (type.Type != "STRUCT") return path + ".type: structMembers requires type='STRUCT'.";
            if (members.Length is < 1 or > GraphCompositeTypes.MaxMembers) return path + ".structMembers: Supply 1..256 members.";
            for (var i = 0; i < members.Length; i++)
            {
                var m = members[i]; var memberPath = path + $".structMembers[{i}]";
                if (m is null) return memberPath + ": Supply a member object containing name and type.";
                if (!GraphRules.Identifier(m.Name) || members.Take(i).Any(other => other is not null && other.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase)))
                    return memberPath + ".name: Supply a unique member identifier.";
                if (!GraphCompositeTypes.Valid(m.Type)) return TypeProblem(m.Type, memberPath + ".type", depth + 1);
                if (m.Address is { } address && !GraphDeclarationRules.AddressValid(address)) return memberPath + ".address: Supply an explicit bounded %I/%Q/%M address.";
                if (!GraphDocumentation.Valid(m.Documentation, m.DocumentationXhtml)) return DocumentationProblem(memberPath, m.Documentation, m.DocumentationXhtml);
            }
        }
        if (type.PointerTo is { } pointer)
        {
            if (type.Type != "POINTER") return path + ".type: pointerTo requires type='POINTER'.";
            if (!GraphCompositeTypes.Valid(pointer)) return TypeProblem(pointer, path + ".pointerTo", depth + 1);
        }
        if (!GraphV2Rules.ScalarTypeValid(type)) return path + ".dimensions: Use 1..3 ordered dimensions with total capacity at most 65536.";
        return path + ": The complete type exceeds the per-program depth/node budget or violates the bounded type profile.";
    }

    public static string Guidance(string code) => code switch
    {
        "graph_contains_unrepresented_data" => "The artifact contains data outside the bounded graph profile. Preserve the original and use opaque exchange; do not delete unknown data to force an edit.",
        "graph_execution_order_conflict" => "Execution orders must be positive, unique and follow every direct and indirect data-flow edge.",
        "invalid_sfc_topology" => "Check the whole SFC: one initial step, matching action names, valid step/transition flow and complete fork/join branches.",
        "invalid_graph_variables" => "Check unique names, scope, type, initializer and declaration metadata against graph-v2. Different type forms cannot be combined.",
        "invalid_sfc_action" => "Actions need unique identifier names and nonempty ST implementations; associations must reference those exact names and valid qualifiers.",
        "invalid_sfc_action_associations" => "Associations need unique positive IDs, 1..64 entries per actionBlock and at most 256 per program.",
        "invalid_sfc_action_order" => "Association executionOrder values, when present, must be positive and unique within the actionBlock.",
        "invalid_graph_pin" => "Check pin objects, names, modifiers and node-kind constraints; non-block nodes have unnamed pins except named divergence outputs.",
        "invalid_graph_block" => "A block needs typeName, named pins, an output or inOut, and positive executionOrder. Its optional instanceName must match a declared function-block variable.",
        _ => "The graph violates " + code + ". Review the graph profile and preserve unrepresented data."
    };
}
