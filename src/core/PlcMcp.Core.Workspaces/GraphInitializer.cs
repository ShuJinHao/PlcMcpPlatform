namespace PlcMcp.Core.Workspaces;

// TC6 values retain their declared order and repetition; the platform does not evaluate ST expressions.
public sealed record GraphInitializer(string Kind, string? Value = null, GraphArrayValue[]? Items = null,
    GraphStructValue[]? Members = null);
public sealed record GraphArrayValue(GraphInitializer Value, int Repeat = 1);
public sealed record GraphStructValue(string Name, GraphInitializer Value);

public static class GraphInitializerRules
{
    public const int MaxDepth = 8;
    public const int MaxNodes = 4096;
    public const int MaxExpandedValues = 65536;
    public static bool Elementary(string type) => type is "BOOL" or "SINT" or "INT" or "DINT" or "LINT"
        or "USINT" or "UINT" or "UDINT" or "ULINT" or "REAL" or "LREAL" or "TIME" or "DATE" or "DT" or "TOD"
        or "BYTE" or "WORD" or "DWORD" or "LWORD" or "STRING" or "WSTRING";

    public static string? Validate(GraphVariable[] variables, GraphDataType? returnType = null)
    {
        var budget = MaxNodes; long total = 0;
        string? Value(GraphDataType type, string? simple, GraphInitializer? value)
        {
            if (simple is not null && (!GraphRules.Text(simple, 256) || value is not null || type.Dimensions is not null || type.StructMembers is not null))
                return "invalid_graph_initializer";
            if (value is null) return null;
            if (value.Kind is not ("array" or "struct") || type.Dimensions is null && value.Kind != "struct") return "invalid_graph_initializer";
            if (Visit(value, 1, ref budget, out var expanded) is { } error) return error;
            total += expanded;
            if (total > 4L * MaxExpandedValues) return "graph_initializer_too_large";
            return Match(type, value);
        }
        string? Members(GraphDataType type)
        {
            foreach (var member in type.StructMembers ?? [])
            {
                if (Value(member.Type, member.InitialValue, member.Initializer) is { } error) return error;
                if (Members(member.Type) is { } nested) return nested;
            }
            return type.PointerTo is { } pointer ? Members(pointer) : null;
        }
        foreach (var variable in variables)
        {
            var type = GraphCompositeTypes.TypeOf(variable);
            if (Value(type, variable.InitialValue, variable.Initializer) is { } error) return error;
            if (Members(type) is { } nested) return nested;
        }
        return returnType is null ? null : Members(returnType);
    }

    private static string? Match(GraphDataType type, GraphInitializer value)
    {
        if (type.Dimensions is { } dimensions)
        {
            if (value.Kind != "array") return "invalid_graph_initializer";
            long capacity = 1; foreach (var dimension in dimensions) capacity *= (long)dimension.Upper - dimension.Lower + 1;
            if (value.Items!.Sum(i => (long)i.Repeat) > capacity) return "graph_initializer_exceeds_array";
            foreach (var item in value.Items!)
                if (Match(type with { Dimensions = null }, item.Value) is { } error) return error;
            return null;
        }
        if (type.StructMembers is { } members)
        {
            if (value.Kind != "struct") return "invalid_graph_initializer";
            foreach (var item in value.Members!)
            {
                var member = members.SingleOrDefault(m => m.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
                if (member is null) return "graph_initializer_unknown_member";
                if (Match(member.Type, item.Value) is { } error) return error;
            }
            return null;
        }
        if (Elementary(type.Type) || type.EnumValues is not null || type.Subrange is not null || type.PointerTo is not null)
            return value.Kind == "simple" ? null : "invalid_graph_initializer";
        // Named DUT contents remain an IDE type-checking concern, as in the original graph profile.
        return null;
    }

    private static string? Visit(GraphInitializer? value, int depth, ref int budget, out long expanded)
    {
        expanded = 0;
        if (value is null) return "invalid_graph_initializer";
        if (depth > MaxDepth || --budget < 0) return "graph_initializer_too_large";
        switch (value.Kind)
        {
            case "simple":
                if (!GraphRules.Text(value.Value, 256) || value.Items is not null || value.Members is not null) return "invalid_graph_initializer";
                expanded = 1; return null;
            case "array":
                if (value.Value is not null || value.Members is not null || value.Items is not { Length: > 0 and <= 256 }) return "invalid_graph_initializer";
                long count = 0;
                foreach (var item in value.Items)
                {
                    if (item is null || item.Repeat is < 1 or > MaxExpandedValues) return "invalid_graph_initializer";
                    if (Visit(item.Value, depth + 1, ref budget, out var weight) is { } error) return error;
                    count += item.Repeat; expanded += item.Repeat * weight;
                    if (count > MaxExpandedValues || expanded > MaxExpandedValues) return "graph_initializer_too_large";
                }
                return null;
            case "struct":
                if (value.Value is not null || value.Items is not null || value.Members is not { Length: > 0 and <= 256 }
                    || value.Members.Any(m => m is null || !GraphRules.Identifier(m.Name))
                    || value.Members.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Members.Length)
                    return "invalid_graph_initializer";
                foreach (var member in value.Members)
                {
                    if (Visit(member.Value, depth + 1, ref budget, out var weight) is { } error) return error;
                    expanded += weight;
                    if (expanded > MaxExpandedValues) return "graph_initializer_too_large";
                }
                return null;
            default: return "invalid_graph_initializer";
        }
    }
}
