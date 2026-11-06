using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using PlcMcp.EngineeringService.Exchange;
using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Exchange;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;

namespace PlcMcp.TestHost;

/// <summary>Synthetic persisted XML payloads, not a CODESYS emulator or a PLC compiler.</summary>
public static class FixtureExchange
{
    private static readonly XNamespace P = ExchangeXml.Plc;
    private static FixtureObject[] Objects(FixtureProject p) => p.Objects ?? [new(p.ProgramId, "PLC_PRG", "pou", p.Declaration, p.Implementation, "ST")];
    private static XDocument Single(XElement pou) => new(new XElement(P + "project",
        new XElement(P + "fileHeader", new XAttribute("companyName", "Fixture"), new XAttribute("productName", "Fixture"), new XAttribute("productVersion", "1"), new XAttribute("creationDateTime", "2000-01-01T00:00:00Z")),
        new XElement(P + "contentHeader", new XAttribute("name", "Fixture"), new XElement(P + "coordinateInfo")),
        new XElement(P + "types", new XElement(P + "dataTypes"), new XElement(P + "pous", new XElement(pou))),
        new XElement(P + "instances", new XElement(P + "configurations"))));
    private static XElement Pou(FixtureObject obj)
    {
        if (obj.Kind != "pou" || obj.ExchangeXml is null) throw new ProjectEditException("fixture_exchange_object_not_modeled");
        var pou = ExchangeXml.Parse(obj.ExchangeXml).Root!.Element(P + "types")!.Element(P + "pous")!.Elements(P + "pou").Single();
        pou.SetAttributeValue("name", obj.Name); return pou;
    }
    public static JsonObject Export(FixtureProject fixture, EngineeringInput input, bool graph)
    {
        var selection = graph ? new ExchangeQuery("plcopen", [input.ObjectId!], false) : input.Exchange!;
        var ids = selection.ObjectIds; var nodes = Objects(fixture);
        if (ids.Contains("project") || ids.Contains(fixture.ApplicationId)) ids = nodes.Select(n => n.Id).ToArray();
        var selected = ids.Select(id => nodes.SingleOrDefault(n => n.Id == id) ?? throw new ProjectEditException("fixture_exchange_selection_unsupported")).ToArray();
        if (selected.Length == 0) throw new ProjectEditException("exchange_selection_empty");
        var project = Single(Pou(selected[0])); var pous = project.Root!.Element(P + "types")!.Element(P + "pous")!;
        foreach (var obj in selected.Skip(1)) pous.Add(Pou(obj));
        var xml = selection.Format == "plcopen" ? project.ToString(SaveOptions.DisableFormatting)
            : new XElement("FixtureNative", new XAttribute("simulated", true), project.Root).ToString(SaveOptions.DisableFormatting);
        return new() { ["format"] = selection.Format, ["xml"] = xml,
            ["objectIds"] = new JsonArray(selected.Select(n => (JsonNode?)JsonValue.Create(n.Id)).ToArray()), ["report"] = new JsonArray(),
            ["selectionScope"] = "fixture_pous_only", ["sourceToExportLosslessness"] = "not_proven" };
    }
    public static (FixtureProject Project, JsonObject Result) Apply(FixtureProject fixture, EngineeringInput input, bool graph)
    {
        var edit = input.Edit!; var change = edit.Exchange!;
        if (edit.ParentId != fixture.ApplicationId) throw new ProjectEditException("fixture_exchange_parent_unsupported");
        var root = ExchangeXml.Parse(change.Xml ?? throw new ProjectEditException("exchange_xml_required")).Root!;
        if (change.Format == "codesys-native")
        {
            if (root.Name != "FixtureNative" || (string?)root.Attribute("simulated") != "true") throw new ProjectEditException("fixture_cannot_parse_vendor_native_format");
            root = root.Element(P + "project") ?? throw new ProjectEditException("invalid_exchange_xml");
        }
        var incoming = root.Element(P + "types")!.Element(P + "pous")!.Elements(P + "pou").ToArray();
        if (root.Element(P + "types")!.Element(P + "dataTypes")?.HasElements == true
            || root.Element(P + "instances")?.Descendants().Any(e => e.Name != P + "configurations") == true
            || root.Elements().Any(e => e.Name != P + "fileHeader" && e.Name != P + "contentHeader" && e.Name != P + "types" && e.Name != P + "instances"))
            throw new ProjectEditException("fixture_exchange_objects_unsupported");
        if (incoming.Length == 0) throw new ProjectEditException("fixture_exchange_objects_unsupported");
        var list = Objects(fixture).ToList(); var ids = new List<string>(); var report = new JsonArray();
        var config = new FixtureConfiguration(fixture, input.Requirements);
        foreach (var pou in incoming)
        {
            var name = (string)pou.Attribute("name")!; var previous = list.SingleOrDefault(n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (graph && previous?.Id != edit.ObjectId) throw new ProjectEditException("graph_identity_conflict");
            if (previous is not null)
            {
                if (change.ConflictPolicy == "reject") throw new ProjectEditException("exchange_reported_error");
                if (change.ConflictPolicy == "skip")
                { ids.Add(previous.Id); report.Add(new JsonObject { ["severity"] = "information", ["message"] = "Skipped conflicting object: " + name }); continue; }
                config.Apply(ConfigurationOperations.ObjectDelete, new(edit.WorkspaceName, ObjectId: previous.Id,
                    Configuration: new(Object: new(ExpectedName: previous.Name, Recursive: true))));
                list.Remove(previous);
            }
            var id = Guid.NewGuid().ToString(); var body = pou.Element(P + "body")!.Elements().Single();
            var xml = Single(pou).ToString(SaveOptions.DisableFormatting);
            list.Add(new(id, name, "pou", ((string?)pou.Attribute("pouType") == "functionBlock" ? "FUNCTION_BLOCK " : "PROGRAM ") + name,
                body.Name.LocalName == "ST" ? body.Value : null, body.Name.LocalName, xml));
            ids.Add(id); report.Add(new JsonObject { ["severity"] = "information", ["message"] = previous is null ? "Imported object" : "Replaced object",
                ["objectId"] = id, ["oldObjectId"] = previous?.Id, ["name"] = name });
        }
        var saved = new FixtureConfiguration(config.Save() with { Objects = list.ToArray() }, input.Requirements).Save();
        var result = Export(saved, input with { Exchange = new(change.Format, ids.ToArray(), true), ObjectId = ids[0] }, graph);
        result["report"] = report; result["sourceArtifactId"] = change.ArtifactId; result["reexportVerified"] = true;
        result["verificationScope"] = "fixture_saved_xml_payload";
        return (saved, result);
    }
}
