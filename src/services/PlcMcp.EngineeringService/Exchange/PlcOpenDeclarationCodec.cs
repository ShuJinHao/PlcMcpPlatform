using System.Xml.Linq;
using PlcMcp.Core.Workspaces;
using PlcMcp.Services.Contracts.Editing;

namespace PlcMcp.EngineeringService.Exchange;

internal static class PlcOpenDeclarationCodec
{
    private static readonly XNamespace P = ExchangeXml.Plc, H = ExchangeXml.Xhtml;
    private static XElement? Documentation(string? value) => value is null ? null : new(P + "documentation",
        new XElement(H + "xhtml", value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')));
    internal static void Attributes(XElement element, params string[] allowed)
    {
        if (element.Attributes().Any(a => !a.IsNamespaceDeclaration && !allowed.Any(n => a.Name == n)))
            throw new ProjectEditException("graph_contains_unrepresented_data");
    }
    private static string? ReadDocumentation(XElement parent)
    {
        var doc = parent.Elements(P + "documentation").SingleOrDefault();
        if (doc is null) return null;
        Attributes(doc);
        var text = doc.Elements().Single(); Attributes(text);
        if (text.Name != H + "xhtml" || text.HasElements || text.Nodes().Any(n => n is not XText))
            throw new ProjectEditException("graph_contains_unrepresented_data");
        return text.Value;
    }
    public static IEnumerable<XElement> Encode(GraphicalProgram program)
    {
        var variables = program.Variables.ToDictionary(v => v.Name, StringComparer.Ordinal);
        return GraphDeclarationRules.Groups(program).Select(g => new XElement(P + (g.Scope + "Vars"),
            g.Name is null ? null : new XAttribute("name", g.Name), g.Constant ? new XAttribute("constant", true) : null,
            g.Retention == "default" ? null : new XAttribute(g.Retention == "retain" ? "retain" : "nonretain", true),
            g.Variables.Select(name => {
                var v = variables[name];
                return new XElement(P + "variable", new XAttribute("name", v.Name), v.Address is null ? null : new XAttribute("address", v.Address),
                    new XElement(P + "type", PlcOpenGraphCodec.EncodeType(new(v.Type, v.StringLength, v.Dimensions))),
                    v.Initializer is not null ? new XElement(P + "initialValue", PlcOpenValueCodec.Encode(v.Initializer))
                        : v.InitialValue is null ? null : new XElement(P + "initialValue", new XElement(P + "simpleValue", new XAttribute("value", v.InitialValue))),
                    Documentation(v.Documentation));
            }), Documentation(g.Documentation)));
    }
    public static (GraphVariable[] Variables, GraphVariableGroup[]? Groups) Decode(XElement iface)
    {
        var variables = new List<GraphVariable>(); var groups = new List<GraphVariableGroup>();
        var budget = GraphInitializerRules.MaxNodes;
        foreach (var group in iface.Elements().Where(e => e.Name != P + "returnType"))
        {
            var scope = GraphDeclarationRules.Scopes.SingleOrDefault(s => group.Name == P + (s + "Vars"))
                ?? throw new ProjectEditException("graph_contains_unrepresented_data");
            Attributes(group, "name", "constant", "retain", "nonretain");
            if (group.Elements().Any(e => e.Name != P + "variable" && e.Name != P + "documentation"))
                throw new ProjectEditException("graph_contains_unrepresented_data");
            var names = new List<string>();
            foreach (var variable in group.Elements(P + "variable"))
            {
                Attributes(variable, "name", "address");
                if (variable.Elements().Any(e => e.Name != P + "type" && e.Name != P + "initialValue" && e.Name != P + "documentation"))
                    throw new ProjectEditException("graph_contains_unrepresented_data");
                var type = PlcOpenGraphCodec.DecodeType(variable.Elements(P + "type").Single());
                var initialContainer = variable.Elements(P + "initialValue").SingleOrDefault();
                if (initialContainer is not null) Attributes(initialContainer);
                var initial = initialContainer?.Elements().Single();
                if (initial?.Name == P + "simpleValue")
                { var scalarBudget = 1; _ = PlcOpenValueCodec.Decode(initial, ref scalarBudget); }
                var name = (string?)variable.Attribute("name") ?? throw new ProjectEditException("invalid_graph_variables"); names.Add(name);
                variables.Add(new(name, type.Type, scope, initial?.Name == P + "simpleValue" ? (string?)initial.Attribute("value") : null,
                    type.StringLength, type.Dimensions, initial is null || initial.Name == P + "simpleValue" ? null : PlcOpenValueCodec.Decode(initial, ref budget),
                    (string?)variable.Attribute("address"), ReadDocumentation(variable)));
            }
            var retain = (bool?)group.Attribute("retain") ?? false; var nonretain = (bool?)group.Attribute("nonretain") ?? false;
            if (retain && nonretain) throw new ProjectEditException("invalid_graph_variable_groups");
            groups.Add(new(scope, names.ToArray(), (bool?)group.Attribute("constant") ?? false, retain ? "retain" : nonretain ? "nonRetain" : "default",
                (string?)group.Attribute("name"), ReadDocumentation(group)));
        }
        // Recover the legacy compact form only when no group boundary, order or metadata would be lost.
        var legacyScopes = GraphDeclarationRules.Scopes.Where(s => variables.Any(v => v.Scope == s));
        var legacy = groups.All(g => !g.Constant && g.Retention == "default" && g.Name is null && g.Documentation is null && g.Variables.Length > 0)
            && legacyScopes.SequenceEqual(groups.Select(g => g.Scope));
        return (variables.ToArray(), legacy || groups.Count == 0 ? null : groups.ToArray());
    }
}
