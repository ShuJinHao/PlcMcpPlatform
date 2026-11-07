namespace PlcMcp.Core.Workspaces;

public sealed record GraphActionAssociation(int Id, string Action, string Qualifier = "N", string? Duration = null,
    int? ExecutionOrder = null);

public static class GraphAssociationRules
{
    public const int MaxPerBlock = 64;
    public const int MaxPerProgram = 256;
    public static GraphActionAssociation[] Items(GraphNode node) => node.Associations
        ?? [new(node.Id, node.Action!, node.Qualifier, node.Duration)];
    public static string? Validate(GraphNode node)
    {
        if (node.Kind != "actionBlock") return node.Associations is not null || node.Action is not null || node.Qualifier != "N" || node.Duration is not null
            ? "graph_field_not_applicable" : null;
        if (node.Associations is not null && (node.Action is not null || node.Qualifier != "N" || node.Duration is not null))
            return "ambiguous_sfc_action_associations";
        var items = Items(node);
        if (items.Length is < 1 or > MaxPerBlock || items.Any(a => a is null || a.Id <= 0)
            || items.Select(a => a.Id).Distinct().Count() != items.Length) return "invalid_sfc_action_associations";
        var orders = items.Where(a => a.ExecutionOrder is not null).Select(a => a.ExecutionOrder).ToArray();
        if (orders.Any(o => o <= 0) || orders.Distinct().Count() != orders.Length) return "invalid_sfc_action_order";
        foreach (var action in items)
        {
            if (!GraphRules.Identifier(action.Action) || action.Qualifier is not ("N" or "S" or "R" or "P") && !GraphV2Rules.TimedQualifier(action.Qualifier))
                return "invalid_sfc_action";
            if (GraphV2Rules.TimedQualifier(action.Qualifier) ? !GraphV2Rules.DurationValid(action.Duration) : action.Duration is not null)
                return "invalid_sfc_duration";
        }
        return null;
    }
}
