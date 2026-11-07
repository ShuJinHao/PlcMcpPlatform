using System.Xml.Linq;
using PlcMcp.Core.Workspaces;
using PlcMcp.Services.Contracts.Editing;

namespace PlcMcp.EngineeringService.Exchange;

internal static class PlcOpenDeclarationCodec
{
    private static readonly XNamespace P = ExchangeXml.Plc, H = ExchangeXml.Xhtml;
    private static XElement? Documentation(string? value, string? xhtml) => xhtml is not null
        ? new(P + "documentation", GraphDocumentation.Parse(xhtml)) : value is null ? null : new(P + "documentation",
            new XElement(H + "xhtml", value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')));
    internal static void Attributes(XElement element, params string[] allowed)
    {
        if (element.Attributes().Any(a => !a.IsNamespaceDeclaration && !allowed.Any(n => a.Name == n)))
            throw new ProjectEditException("graph_contains_unrepresented_data");
    }
    private static (string? Plain, string? Xhtml) ReadDocumentation(XElement parent)
    {
        var doc = parent.Elements(P + "documentation").SingleOrDefault();
        if (doc is null) return (null, null);
        Attributes(doc);
        var text = doc.Elements().Single();
        // Preserve the original compact plain-text form; all other XHTML stays structured data.
        if (text.Name == H + "xhtml" && !text.HasElements && text.Attributes().All(a => a.IsNamespaceDeclaration)
            && text.Nodes().Count() <= 1 && text.Nodes().All(n => n is XText) && GraphDeclarationRules.DocumentationValid(text.Value)) return (text.Value, null);
        var xhtml = text.ToString(SaveOptions.DisableFormatting);
        if (!GraphDocumentation.Valid(null, xhtml)) throw new ProjectEditException("invalid_graph_documentation");
        return (null, xhtml);
    }
    internal static XElement EncodeMember(GraphStructMember member) => new(P + "variable", new XAttribute("name", member.Name),
        member.Address is null ? null : new XAttribute("address", member.Address),
        new XElement(P + "type", PlcOpenGraphCodec.EncodeType(member.Type)),
        member.Initializer is not null ? new XElement(P + "initialValue", PlcOpenValueCodec.Encode(member.Initializer))
            : member.InitialValue is null ? null : new XElement(P + "initialValue", new XElement(P + "simpleValue", new XAttribute("value", member.InitialValue))),
        Documentation(member.Documentation, member.DocumentationXhtml));
    internal static GraphStructMember DecodeMember(XElement variable, int depth = 1)
    {
        Attributes(variable, "name", "address");
        if (variable.Elements().Any(e => e.Name != P + "type" && e.Name != P + "initialValue" && e.Name != P + "documentation"))
            throw new ProjectEditException("graph_contains_unrepresented_data");
        var type = PlcOpenGraphCodec.DecodeType(variable.Elements(P + "type").Single(), depth);
        var initialContainer = variable.Elements(P + "initialValue").SingleOrDefault();
        if (initialContainer is not null) Attributes(initialContainer);
        var initial = initialContainer?.Elements().Single();
        GraphInitializer? value = null;
        if (initial is not null)
        {
            var budget = GraphInitializerRules.MaxNodes;
            value = PlcOpenValueCodec.Decode(initial, ref budget);
        }
        var documentation = ReadDocumentation(variable);
        return new((string?)variable.Attribute("name") ?? throw new ProjectEditException("invalid_graph_variables"), type,
            value?.Kind == "simple" ? value.Value : null, value?.Kind == "simple" ? null : value,
            (string?)variable.Attribute("address"), documentation.Plain, documentation.Xhtml);
    }
    public static IEnumerable<XElement> Encode(GraphicalProgram program)
    {
        var variables = program.Variables.ToDictionary(v => v.Name, StringComparer.Ordinal);
        return GraphDeclarationRules.Groups(program).Select(g => new XElement(P + (g.Scope + "Vars"),
            g.Name is null ? null : new XAttribute("name", g.Name), g.Constant ? new XAttribute("constant", true) : null,
            g.Retention == "default" ? null : new XAttribute(g.Retention == "retain" ? "retain" : "nonretain", true),
            g.Persistence == "default" ? null : new XAttribute(g.Persistence == "persistent" ? "persistent" : "nonpersistent", true),
            g.Variables.Select(name => {
                var v = variables[name];
                return EncodeMember(new(v.Name, GraphCompositeTypes.TypeOf(v), v.InitialValue, v.Initializer,
                    v.Address, v.Documentation, v.DocumentationXhtml));
            }), Documentation(g.Documentation, g.DocumentationXhtml)));
    }
    public static (GraphVariable[] Variables, GraphVariableGroup[]? Groups) Decode(XElement iface)
    {
        var variables = new List<GraphVariable>(); var groups = new List<GraphVariableGroup>();
        foreach (var group in iface.Elements().Where(e => e.Name != P + "returnType"))
        {
            var scope = GraphDeclarationRules.Scopes.SingleOrDefault(s => group.Name == P + (s + "Vars"))
                ?? throw new ProjectEditException("graph_contains_unrepresented_data");
            Attributes(group, "name", "constant", "retain", "nonretain", "persistent", "nonpersistent");
            if (group.Elements().Any(e => e.Name != P + "variable" && e.Name != P + "documentation"))
                throw new ProjectEditException("graph_contains_unrepresented_data");
            var names = new List<string>();
            foreach (var variable in group.Elements(P + "variable"))
            {
                var member = DecodeMember(variable); var type = member.Type; names.Add(member.Name);
                variables.Add(new(member.Name, type.Type, scope, member.InitialValue, type.StringLength, type.Dimensions,
                    member.Initializer, member.Address, member.Documentation, type.EnumValues, type.Subrange,
                    type.StructMembers, type.PointerTo, member.DocumentationXhtml));
            }
            var retain = (bool?)group.Attribute("retain") ?? false; var nonretain = (bool?)group.Attribute("nonretain") ?? false;
            var persistent = (bool?)group.Attribute("persistent") ?? false; var nonpersistent = (bool?)group.Attribute("nonpersistent") ?? false;
            if (retain && nonretain || persistent && nonpersistent) throw new ProjectEditException("invalid_graph_variable_groups");
            var documentation = ReadDocumentation(group);
            groups.Add(new(scope, names.ToArray(), (bool?)group.Attribute("constant") ?? false, retain ? "retain" : nonretain ? "nonRetain" : "default",
                (string?)group.Attribute("name"), documentation.Plain, persistent ? "persistent" : nonpersistent ? "nonPersistent" : "default", documentation.Xhtml));
        }
        var legacyScopes = GraphDeclarationRules.Scopes.Where(s => variables.Any(v => v.Scope == s));
        var legacy = groups.All(g => !g.Constant && g.Retention == "default" && g.Persistence == "default" && g.Name is null
                && g.Documentation is null && g.DocumentationXhtml is null && g.Variables.Length > 0)
            && legacyScopes.SequenceEqual(groups.Select(g => g.Scope));
        return (variables.ToArray(), legacy || groups.Count == 0 ? null : groups.ToArray());
    }
}
