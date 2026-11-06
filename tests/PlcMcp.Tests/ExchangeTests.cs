using System.Text.Json;
using System.Xml.Linq;
using PlcMcp.Core.Workspaces;
using PlcMcp.EngineeringService.Exchange;
using PlcMcp.Execution;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Exchange;
using PlcMcp.Services.Contracts.Workers;
using Xunit;

namespace PlcMcp.Tests;

public sealed class ExchangeTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PlcMcpPlatform.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }
    public static GraphicalProgram Sample(string name) => JsonSerializer.Deserialize<GraphicalProgram>(
        File.ReadAllText(Path.Combine(Root(), "tests", "fixtures", "graphs", name + ".json")), WorkerJson.Options)!;
    [Theory]
    [InlineData("ld-start-stop")]
    [InlineData("fbd-delay-counter")]
    [InlineData("sfc-sequence")]
    public void Graph_roundtrip_preserves_logic_and_ignores_only_geometry(string sample)
    {
        var program = Sample(sample); var xml = PlcOpenGraphCodec.Encode(program);
        var read = PlcOpenGraphCodec.Decode(xml);
        Assert.Equal(PlcOpenGraphCodec.SemanticHash(program), read.SemanticSha256);
        Assert.Equal(program.Nodes.Length, read.Program.Nodes.Length);
        Assert.Equal(program.Language, read.Program.Language);
        var geometry = program with { Nodes = program.Nodes.Select(n => n with { X = n.X + 20, Y = n.Y + 50 }).ToArray() };
        Assert.Equal(read.SemanticSha256, PlcOpenGraphCodec.Decode(PlcOpenGraphCodec.Encode(geometry)).SemanticSha256);
        var data = XDocument.Parse(xml); var p = ExchangeXml.Plc;
        data.Descendants(p + "connection").First().SetAttributeValue("refLocalId", 999);
        Assert.Equal("dangling_graph_connection", Assert.Throws<ProjectEditException>(() => PlcOpenGraphCodec.Decode(data.ToString())).Code);
    }
    [Fact] public void Coil_polarity_connectivity_and_execution_order_change_semantic_hash()
    {
        var p = Sample("ld-start-stop"); var original = PlcOpenGraphCodec.SemanticHash(p);
        Assert.NotEqual(original, PlcOpenGraphCodec.SemanticHash(p with { Nodes = p.Nodes.Select(n => n.Kind == "coil" ? n with { Negated = true } : n).ToArray() }));
        var f = Sample("fbd-delay-counter");
        Assert.Equal("unknown_graph_output_pin", GraphRules.Validate(f with { Nodes = f.Nodes.Select(n => n.Kind == "outVariable" ? n with { Inputs = [new("", [new(3, "missing")])] } : n).ToArray() }));
        Assert.Equal("graph_execution_order_conflict", GraphRules.Validate(f with { Nodes = f.Nodes.Select(n => n.Id == 4 ? n with { ExecutionOrder = 1 } : n).ToArray() }));
    }
    [Fact] public void Duplicate_ids_cycles_and_unrepresented_vendor_properties_are_rejected()
    {
        var p = Sample("ld-start-stop");
        Assert.Equal("duplicate_or_invalid_local_id", GraphRules.Validate(p with { Nodes = [.. p.Nodes, p.Nodes[0]] }));
        Assert.Equal("graph_cycle_requires_state_variable", GraphRules.Validate(p with { Nodes = p.Nodes.Select(n => n.Id == 2 ? n with { Inputs = [new("", [new(4)])] } : n).ToArray() }));
        var xml = XDocument.Parse(PlcOpenGraphCodec.Encode(p));
        xml.Descendants(ExchangeXml.Plc + "pou").Single().Add(new XElement(ExchangeXml.Plc + "addData", new XElement("VendorProperty", "must survive")));
        Assert.Equal("graph_contains_unrepresented_data", Assert.Throws<ProjectEditException>(() => PlcOpenGraphCodec.Decode(xml.ToString())).Code);
        Assert.True(ExchangeXml.Inspect("plcopen", xml.ToString()).HasExtensions);
    }
    [Theory]
    [InlineData("<!DOCTYPE project [<!ENTITY secret SYSTEM 'file:///etc/passwd'>]><project>&secret;</project>")]
    [InlineData("<project><broken></project>")]
    public void Xml_parser_rejects_dtd_entities_and_malformed_documents(string xml)
        => Assert.Equal("invalid_exchange_xml", Assert.Throws<ProjectEditException>(() => ExchangeXml.Parse(xml)).Code);
    [Fact] public void Native_validation_does_not_claim_to_understand_vendor_semantics()
    {
        var value = ExchangeXml.Inspect("codesys-native", "<VendorExport><unknown/></VendorExport>");
        Assert.Equal("well_formed_xml_only", value.ValidationScope);
        Assert.Equal("exchange_format_mismatch", Assert.Throws<ProjectEditException>(() => ExchangeXml.Inspect("codesys-native", PlcOpenGraphCodec.Encode(Sample("ld-start-stop")))).Code);
    }
    [Fact] public async Task Artifacts_are_content_addressed_and_detect_corruption()
    {
        var directory = Path.Combine(Path.GetTempPath(), "plc-exchange-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ExchangeArtifacts(directory); var xml = PlcOpenGraphCodec.Encode(Sample("ld-start-stop"));
            var a = await store.PutAsync("plcopen", xml, default); var b = await store.PutAsync("plcopen", xml, default);
            Assert.Equal(a, b); Assert.Equal(xml, (await store.GetAsync(a.ArtifactId, default)).Xml);
            await Assert.ThrowsAsync<ProjectEditException>(() => store.GetAsync("../invalid", default));
            var path = Path.Combine(directory, a.ArtifactId + ".json");
            var doc = JsonSerializer.Deserialize<ExchangeDocument>(await File.ReadAllTextAsync(path), WorkerJson.Options)!;
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(doc with { Xml = doc.Xml.Replace("StartStop", "LostData", StringComparison.Ordinal) }, WorkerJson.Options));
            Assert.Equal("artifact_integrity_mismatch", (await Assert.ThrowsAsync<ProjectEditException>(() => store.GetAsync(a.ArtifactId, default))).Code);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
