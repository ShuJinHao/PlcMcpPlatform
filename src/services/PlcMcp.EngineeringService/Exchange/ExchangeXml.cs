using System.Text;
using System.Xml;
using System.Xml.Linq;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Exchange;

namespace PlcMcp.EngineeringService.Exchange;

public static class ExchangeXml
{
    public static readonly XNamespace Plc = "http://www.plcopen.org/xml/tc6_0201";
    public static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    public static XDocument Parse(string xml)
    {
        if (string.IsNullOrWhiteSpace(xml) || Encoding.UTF8.GetByteCount(xml) > ExchangeOperations.MaxXmlBytes) throw new ProjectEditException("exchange_size_invalid");
        try
        {
            using var input = new StringReader(xml);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = ExchangeOperations.MaxXmlBytes, IgnoreComments = false });
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            if (document.Descendants().Count() > 4096 || document.Descendants().Any(e => e.Ancestors().Count() > 32)) throw new ProjectEditException("exchange_structure_too_large");
            return document;
        }
        catch (XmlException) { throw new ProjectEditException("invalid_exchange_xml"); }
    }
    public static ExchangeInspection Inspect(string format, string xml)
    {
        if (!ExchangeOperations.Format(format)) throw new ProjectEditException("invalid_exchange_format");
        var root = Parse(xml).Root!;
        if (format == "codesys-native")
        {
            if (root.Name == Plc + "project") throw new ProjectEditException("exchange_format_mismatch");
            return new(format, root.Name.ToString(), [], [], true, "well_formed_xml_only",
                ["Native structure and compatibility are validated by the target IDE; no generic native schema or losslessness claim."]);
        }
        if (root.Name != Plc + "project") throw new ProjectEditException("exchange_namespace_unsupported");
        if (root.Element(Plc + "types") is null || root.Element(Plc + "instances") is null) throw new ProjectEditException("invalid_plcopen_structure");
        var pous = root.Element(Plc + "types")!.Element(Plc + "pous")?.Elements(Plc + "pou").ToArray() ?? [];
        var names = pous.Select(p => (string?)p.Attribute("name") ?? "").ToArray();
        if (names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length) throw new ProjectEditException("duplicate_or_invalid_pou_name");
        foreach (var body in pous.SelectMany(p => p.Elements(Plc + "body")).SelectMany(b => b.Elements()))
        {
            var ids = body.Elements().Attributes("localId").Select(a => a.Value).ToArray();
            if (ids.Distinct().Count() != ids.Length) throw new ProjectEditException("duplicate_or_invalid_local_id");
            foreach (var c in body.Descendants(Plc + "connection"))
                if (!ids.Contains((string?)c.Attribute("refLocalId"))) throw new ProjectEditException("dangling_graph_connection");
        }
        return new(format, root.Name.ToString(), names,
            pous.SelectMany(p => p.Elements(Plc + "body")).SelectMany(b => b.Elements()).Select(b => b.Name.LocalName).Distinct().ToArray(),
            root.Descendants().Any(e => e.Name == Plc + "addData" || e.Name.Namespace != Plc && e.Name.Namespace != Xhtml),
            "bounded_structure_not_full_xsd_or_compilation",
            ["Target IDE may reject objects or extensions. Reporter warnings require explicit acknowledgement; errors never publish.",
             "PLCopen representation may lose vendor properties; native exchange is a separate format."]);
    }
}
