using System.Xml.Linq;
using PlcMcp.Core.Workspaces;
using PlcMcp.Services.Contracts.Editing;

namespace PlcMcp.EngineeringService.Exchange;

internal static class PlcOpenValueCodec
{
    private static readonly XNamespace P = ExchangeXml.Plc;
    private static void Attributes(XElement element, string? allowed = null)
    {
        if (element.Attributes().Any(a => !a.IsNamespaceDeclaration && (allowed is null || a.Name != allowed)))
            throw new ProjectEditException("graph_contains_unrepresented_data");
    }
    public static XElement Encode(GraphInitializer value) => value.Kind switch
    {
        "simple" => new(P + "simpleValue", new XAttribute("value", value.Value!)),
        "array" => new(P + "arrayValue", value.Items!.Select(i => new XElement(P + "value",
            i.Repeat == 1 ? null : new XAttribute("repetitionValue", i.Repeat), Encode(i.Value)))),
        "struct" => new(P + "structValue", value.Members!.Select(m => new XElement(P + "value",
            new XAttribute("member", m.Name), Encode(m.Value)))),
        _ => throw new ProjectEditException("invalid_graph_initializer")
    };

    public static GraphInitializer Decode(XElement value, ref int budget, int depth = 1)
    {
        if (depth > GraphInitializerRules.MaxDepth || --budget < 0) throw new ProjectEditException("graph_initializer_too_large");
        if (value.Name == P + "simpleValue")
        {
            Attributes(value, "value");
            if (value.HasElements) throw new ProjectEditException("graph_contains_unrepresented_data");
            return new("simple", (string?)value.Attribute("value"));
        }
        Attributes(value);
        var elements = value.Elements().ToArray();
        if (elements.Length is < 1 or > 256 || elements.Any(e => e.Name != P + "value"))
            throw new ProjectEditException("graph_contains_unrepresented_data");
        if (value.Name == P + "arrayValue")
        {
            var items = new List<GraphArrayValue>();
            foreach (var e in elements)
            { Attributes(e, "repetitionValue"); items.Add(new(Decode(e.Elements().Single(), ref budget, depth + 1), (int?)e.Attribute("repetitionValue") ?? 1)); }
            return new("array", Items: items.ToArray());
        }
        if (value.Name == P + "structValue")
        {
            var members = new List<GraphStructValue>();
            foreach (var e in elements)
            { Attributes(e, "member"); members.Add(new((string?)e.Attribute("member") ?? throw new ProjectEditException("graph_contains_unrepresented_data"), Decode(e.Elements().Single(), ref budget, depth + 1))); }
            return new("struct", Members: members.ToArray());
        }
        throw new ProjectEditException("graph_contains_unrepresented_data");
    }
}
