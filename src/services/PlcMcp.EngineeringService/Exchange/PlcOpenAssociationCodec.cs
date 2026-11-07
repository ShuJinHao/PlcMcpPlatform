using System.Xml.Linq;
using PlcMcp.Core.Workspaces;
using PlcMcp.Services.Contracts.Editing;

namespace PlcMcp.EngineeringService.Exchange;

internal static class PlcOpenAssociationCodec
{
    private static readonly XNamespace P = ExchangeXml.Plc;
    public static IEnumerable<XElement> Encode(GraphNode node) => GraphAssociationRules.Items(node).Select((a, i) =>
        new XElement(P + "action", new XAttribute("localId", a.Id), new XAttribute("qualifier", a.Qualifier),
            a.Duration is null ? null : new XAttribute("duration", a.Duration), a.ExecutionOrder is null ? null : new XAttribute("executionOrderId", a.ExecutionOrder),
            new XElement(P + "relPosition", new XAttribute("x", 0), new XAttribute("y", i * 20)),
            new XElement(P + "reference", new XAttribute("name", a.Action))));

    public static GraphActionAssociation[] Decode(XElement block)
    {
        var elements = block.Elements(P + "action").ToArray();
        if (elements.Length is < 1 or > GraphAssociationRules.MaxPerBlock) throw new ProjectEditException("invalid_sfc_action_associations");
        var actions = new List<GraphActionAssociation>(); decimal? previousY = null;
        foreach (var element in elements)
        {
            PlcOpenDeclarationCodec.Attributes(element, "localId", "qualifier", "duration", "executionOrderId", "width", "height");
            if (element.Elements().Any(e => e.Name != P + "relPosition" && e.Name != P + "reference"))
                throw new ProjectEditException("graph_contains_unrepresented_data");
            var reference = element.Elements(P + "reference").Single(); PlcOpenDeclarationCodec.Attributes(reference, "name");
            if (reference.Nodes().Any()) throw new ProjectEditException("graph_contains_unrepresented_data");
            var position = element.Elements(P + "relPosition").Single(); PlcOpenDeclarationCodec.Attributes(position, "x", "y");
            var x = (decimal)position.Attribute("x")!; var y = (decimal)position.Attribute("y")!;
            if (position.Nodes().Any() || x is < 0 or > 100000 || y is < 0 or > 100000 || previousY is { } prior && y <= prior)
                throw new ProjectEditException("invalid_sfc_action_order");
            previousY = y;
            actions.Add(new((int)element.Attribute("localId")!, (string)reference.Attribute("name")!,
                (string?)element.Attribute("qualifier") ?? "N", (string?)element.Attribute("duration"), (int?)element.Attribute("executionOrderId")));
        }
        return actions.ToArray();
    }
}
