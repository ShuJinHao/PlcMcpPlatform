using System.Text.RegularExpressions;

namespace PlcMcp.Core.Workspaces;

// Groups reference the one variable inventory; declaration order and boundaries are explicit.
public sealed record GraphVariableGroup(string Scope, string[] Variables, bool Constant = false,
    string Retention = "default", string? Name = null, string? Documentation = null);

public static class GraphDeclarationRules
{
    public static readonly string[] Scopes = ["local", "input", "output", "inOut", "temp", "external"];
    public const int MaxGroups = 128;
    public const int MaxDocumentation = 4096;
    public static bool AddressValid(string value) => value.Length <= 64
        && Regex.IsMatch(value, @"\A%[IQM][XBWDL]?[0-9]{1,10}(?:\.[0-9]{1,10}){0,3}\z", RegexOptions.CultureInvariant)
        && Regex.Matches(value, "[0-9]+").All(m => uint.TryParse(m.Value, out _));
    public static bool DocumentationValid(string? value)
    {
        if (value is null) return true;
        if (value.Length > MaxDocumentation) return false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c < 32 && c is not ('\t' or '\r' or '\n') || c is '\uFFFE' or '\uFFFF') return false;
            if (char.IsHighSurrogate(c)) { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false; }
            else if (char.IsLowSurrogate(c)) return false;
        }
        return true;
    }
    public static GraphVariableGroup[] Groups(GraphicalProgram program) => program.VariableGroups
        ?? Scopes.Where(s => program.Variables.Any(v => v.Scope == s))
            .Select(s => new GraphVariableGroup(s, program.Variables.Where(v => v.Scope == s).Select(v => v.Name).ToArray())).ToArray();
    public static string? Validate(GraphicalProgram program)
    {
        foreach (var variable in program.Variables)
        {
            if (!Scopes.Contains(variable.Scope)) return "invalid_graph_variables";
            if (variable.Address is { } address && !AddressValid(address)) return "invalid_graph_address";
            if (!DocumentationValid(variable.Documentation)) return "invalid_graph_documentation";
        }
        var groups = Groups(program);
        if (program.VariableGroups is not null && groups.Length is < 1 or > MaxGroups) return "invalid_graph_variable_groups";
        var byName = program.Variables.ToDictionary(v => v.Name, StringComparer.Ordinal);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            if (group is null || !Scopes.Contains(group.Scope) || group.Variables is not { Length: > 0 and <= 512 }
                || group.Retention is not ("default" or "retain" or "nonRetain")
                || group.Constant && group.Retention != "default"
                || group.Name is not null && (!GraphRules.Text(group.Name, 128) || !DocumentationValid(group.Name)))
                return "invalid_graph_variable_groups";
            if (!DocumentationValid(group.Documentation)) return "invalid_graph_documentation";
            foreach (var name in group.Variables)
            {
                if (name is null || !byName.TryGetValue(name, out var variable) || variable.Scope != group.Scope || !referenced.Add(name))
                    return "invalid_graph_variable_groups";
                if (group.Constant && group.Scope != "external" && variable.InitialValue is null && variable.Initializer is null)
                    return "graph_constant_requires_initializer";
            }
        }
        return referenced.Count != byName.Count ? "invalid_graph_variable_groups" : null;
    }
}
