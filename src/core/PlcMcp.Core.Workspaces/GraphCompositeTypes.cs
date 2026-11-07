namespace PlcMcp.Core.Workspaces;

public static class GraphCompositeTypes
{
    public const int MaxDepth = 8;
    public const int MaxNodes = 4096;
    public const int MaxMembers = 256;
    public static GraphDataType TypeOf(GraphVariable v) => new(v.Type, v.StringLength, v.Dimensions, v.EnumValues, v.Subrange, v.StructMembers, v.PointerTo);
    public static bool Valid(GraphDataType? type)
    {
        var budget = MaxNodes;
        return Valid(type, 1, ref budget);
    }
    public static bool ValidProgram(GraphicalProgram program)
    {
        var budget = MaxNodes;
        return (program.ReturnType is null || Valid(program.ReturnType, 1, ref budget))
            && program.Variables.All(v => Valid(TypeOf(v), 1, ref budget));
    }
    private static bool Valid(GraphDataType? type, int depth, ref int budget)
    {
        if (type is null || depth > MaxDepth || --budget < 0 || !GraphV2Rules.ScalarTypeValid(type)) return false;
        var forms = (type.EnumValues is null ? 0 : 1) + (type.Subrange is null ? 0 : 1)
            + (type.StructMembers is null ? 0 : 1) + (type.PointerTo is null ? 0 : 1);
        if (forms > 1 || forms > 0 && type.StringLength is not null) return false;
        if (type.StructMembers is { } members)
        {
            if (type.Type != "STRUCT" || members.Length is < 1 or > MaxMembers
                || members.Any(m => m is null || !GraphRules.Identifier(m.Name))
                || members.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != members.Length) return false;
            foreach (var member in members)
            {
                if (!Valid(member.Type, depth + 1, ref budget)
                    || member.Address is { } address && !GraphDeclarationRules.AddressValid(address)
                    || !GraphDocumentation.Valid(member.Documentation, member.DocumentationXhtml)) return false;
            }
        }
        if (type.PointerTo is { } pointer && (type.Type != "POINTER" || !Valid(pointer, depth + 1, ref budget))) return false;
        return true;
    }
}
