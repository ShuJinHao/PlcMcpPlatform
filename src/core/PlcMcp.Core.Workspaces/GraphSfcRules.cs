namespace PlcMcp.Core.Workspaces;

internal static class GraphSfcRules
{
    public static string? Validate(GraphicalProgram p)
    {
        var nodes = p.Nodes; var byId = nodes.ToDictionary(n => n.Id);
        var steps = nodes.Where(n => n.Kind == "step").ToArray();
        if (steps.Count(n => n.Initial) != 1 || steps.Select(n => n.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != steps.Length
            || nodes.Any(n => n.Kind == "jumpStep" && !steps.Any(s => s.Name == n.Name))
            || nodes.Where(n => n.Kind == "actionBlock").SelectMany(GraphAssociationRules.Items)
                .Any(item => !(p.Actions ?? []).Any(a => a.Name == item.Action))) return "invalid_sfc_topology";
        var next = nodes.ToDictionary(n => n.Id, _ => new List<(GraphNode Node, string Pin)>());
        foreach (var n in nodes)
        {
            var parents = (n.Inputs ?? []).SelectMany(x => x.Connections ?? []).ToArray();
            if (parents.Select(x => (x.SourceId, x.SourcePin ?? "")).Distinct().Count() != parents.Length) return "invalid_sfc_topology";
            foreach (var c in parents)
            {
                var source = byId[c.SourceId];
                var valid = n.Kind switch {
                    "transition" => source.Kind is "step" or "selectionDivergence" or "simultaneousConvergence",
                    "step" or "jumpStep" => source.Kind is "transition" or "selectionConvergence" or "simultaneousDivergence",
                    "actionBlock" or "selectionDivergence" or "simultaneousConvergence" => source.Kind == "step",
                    "simultaneousDivergence" or "selectionConvergence" => source.Kind == "transition",
                    _ => false };
                if (!valid) return "invalid_sfc_topology";
                next[source.Id].Add((n, c.SourcePin ?? ""));
            }
            if (n.Priority is not null && (n.Kind != "transition" || parents.Length != 1 || byId[parents[0].SourceId].Kind != "selectionDivergence")) return "invalid_sfc_priority";
        }
        foreach (var n in nodes)
        {
            var flow = next[n.Id].Where(x => x.Node.Kind != "actionBlock").ToArray();
            if (GraphV2Rules.Divergence(n.Kind))
            {
                var ports = n.Outputs!;
                if (flow.Length != ports.Length || ports.Any(pin => flow.Count(x => x.Pin == pin.Name) != 1)) return "invalid_sfc_branch_ports";
                if (n.Kind == "selectionDivergence")
                {
                    var branches = ports.Select(pin => flow.Single(x => x.Pin == pin.Name).Node).ToArray();
                    // Priority and left-to-right geometry must agree; moving a branch must not silently change the choice.
                    if (branches.Where((b, i) => b.Priority != i + 1 || i > 0 && b.X <= branches[i - 1].X).Any()) return "invalid_sfc_priority";
                }
            }
            else if (n.Kind == "step" ? flow.Length > 1 : n.Kind is "jumpStep" or "actionBlock" ? flow.Length != 0 : flow.Length != 1)
                return "invalid_sfc_topology";
        }
        var visited = new HashSet<int>(); var active = new HashSet<int>();
        bool Cycle(int id)
        {
            if (active.Contains(id)) return true;
            if (!visited.Add(id)) return false;
            active.Add(id); if (next[id].Any(x => Cycle(x.Node.Id))) return true; active.Remove(id); return false;
        }
        if (Cycle(steps.Single(s => s.Initial).Id)) return "sfc_cycle_requires_jump";
        if (visited.Count != nodes.Length) return "unreachable_sfc_node";
        // This profile accepts properly nested, fully joined branches. It does not guess arbitrary cross-branch synchronization.
        var owners = new Dictionary<int, int>();
        int? Join(GraphNode fork, HashSet<int> path)
        {
            if (!path.Add(fork.Id)) return null;
            var expected = fork.Kind == "selectionDivergence" ? "selectionConvergence" : "simultaneousConvergence";
            int? Walk(GraphNode node, HashSet<int> seen)
            {
                if (node.Kind == expected) return node.Id;
                if (!seen.Add(node.Id) || GraphV2Rules.Convergence(node.Kind) || node.Kind is "jumpStep" or "actionBlock") return null;
                if (GraphV2Rules.Divergence(node.Kind))
                {
                    var nested = Join(node, new(path)); if (nested is null) return null;
                    node = byId[nested.Value];
                }
                var flow = next[node.Id].Where(x => x.Node.Kind != "actionBlock").ToArray();
                return flow.Length == 1 ? Walk(flow[0].Node, seen) : null;
            }
            var joins = next[fork.Id].Select(x => Walk(x.Node, [])).ToArray();
            if (joins.Length < 2 || joins.Any(j => j is null) || joins.Distinct().Count() != 1) return null;
            var id = joins[0]!.Value;
            if (byId[id].Inputs!.Length != fork.Outputs!.Length || owners.TryGetValue(id, out var other) && other != fork.Id) return null;
            owners[id] = fork.Id; return id;
        }
        foreach (var fork in nodes.Where(n => GraphV2Rules.Divergence(n.Kind)))
            if (Join(fork, []) is null) return "sfc_branch_requires_matching_join";
        if (nodes.Any(n => GraphV2Rules.Convergence(n.Kind) && !owners.ContainsKey(n.Id))) return "sfc_branch_requires_matching_join";
        return null;
    }
}
