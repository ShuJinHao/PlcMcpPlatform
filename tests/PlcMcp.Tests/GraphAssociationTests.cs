using System.Text.Json;
using System.Xml.Linq;
using PlcMcp.Core.Workspaces;
using PlcMcp.EngineeringService.Exchange;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Workers;
using Xunit;

namespace PlcMcp.Tests;

public sealed class GraphAssociationTests
{
    public static GraphicalProgram Sample()
    {
        var p = ExchangeTests.Sample("sfc-sequence");
        return p with { Profile = "graph-v2", Nodes = p.Nodes.Select(n => n.Id == 4 ? n with { Action = null,
            Associations = [new(40, "DriveOutput"), new(41, "ResetOutput", "D", "T#1s", 3)] } : n).ToArray() };
    }
    private static GraphicalProgram Change(Func<GraphNode, GraphNode> edit)
    { var p = Sample(); return p with { Nodes = p.Nodes.Select(n => n.Id == 4 ? edit(n) : n).ToArray() }; }
    [Fact] public void Multiple_action_associations_roundtrip_with_independent_identity_and_order_metadata()
    {
        var p = Sample(); Assert.Null(GraphRules.Validate(p)); var xml = PlcOpenGraphCodec.Encode(p); var read = PlcOpenGraphCodec.Decode(xml);
        Assert.Equal(PlcOpenGraphCodec.SemanticHash(p), read.SemanticSha256);
        Assert.Equal(JsonSerializer.Serialize(p.Nodes[3].Associations, WorkerJson.Options), JsonSerializer.Serialize(read.Program.Nodes[3].Associations, WorkerJson.Options));
        Assert.Equal("graph-v2", PlcOpenGraphCodec.Decode(xml.Replace("graph-v2", "vendor")).Profile);
        Assert.Equal("graph_profile_mismatch", GraphRules.Validate(p with { Profile = "graph-v1" }));
    }
    [Theory][InlineData("id")][InlineData("reference")][InlineData("qualifier")][InlineData("duration")][InlineData("metadata")][InlineData("listOrder")]
    public void Every_action_association_change_affects_revision_hash(string change)
    {
        var p = Change(n => { var items = n.Associations!.ToArray();
            items[0] = change switch { "id" => items[0] with { Id = 99 }, "reference" => items[0] with { Action = "ResetOutput" },
                "qualifier" => items[0] with { Qualifier = "S" }, "metadata" => items[0] with { ExecutionOrder = 1 }, _ => items[0] };
            if (change == "duration") items[1] = items[1] with { Duration = "T#2s" };
            if (change == "listOrder") items = items.Reverse().ToArray(); return n with { Associations = items }; });
        Assert.NotEqual(PlcOpenGraphCodec.SemanticHash(Sample()), PlcOpenGraphCodec.SemanticHash(p));
    }
    [Fact] public void Legacy_fields_cannot_compete_with_an_explicit_list()
    {
        foreach (var p in new[] { Change(n => n with { Action = "DriveOutput" }), Change(n => n with { Qualifier = "S" }), Change(n => n with { Duration = "T#1s" }) })
            Assert.Equal("ambiguous_sfc_action_associations", GraphRules.Validate(p));
        var sample = Sample();
        Assert.Equal("graph_field_not_applicable", GraphRules.Validate(sample with { Nodes = sample.Nodes.Select(n => n.Kind == "step" ? n with { Associations = [new(10, "DriveOutput")] } : n).ToArray() }));
    }
    [Fact] public void IDs_counts_qualifiers_and_references_are_validated()
    {
        foreach (var items in new GraphActionAssociation[][] { [], [null!], [new(0, "DriveOutput")], [new(1, "DriveOutput"), new(1, "ResetOutput")],
            Enumerable.Range(1, 65).Select(i => new GraphActionAssociation(i, "DriveOutput")).ToArray() })
            Assert.Equal("invalid_sfc_action_associations", GraphRules.Validate(Change(n => n with { Associations = items })));
        Assert.Equal("invalid_sfc_action", GraphRules.Validate(Change(n => n with { Associations = [new(1, "DriveOutput", "vendor")] })));
        Assert.Equal("invalid_sfc_duration", GraphRules.Validate(Change(n => n with { Associations = [new(1, "DriveOutput", "D")] })));
        Assert.Equal("invalid_sfc_topology", GraphRules.Validate(Change(n => n with { Associations = [new(1, "MissingAction")] })));
        Assert.Equal("invalid_sfc_action_order", GraphRules.Validate(Change(n => n with { Associations = [new(1, "DriveOutput", ExecutionOrder: 1), new(2, "ResetOutput", ExecutionOrder: 1)] })));
    }
    [Fact] public void Repeated_action_reference_is_preserved_instead_of_deduplicated()
    {
        var p = Change(n => n with { Associations = [new(10, "DriveOutput", "S"), new(11, "DriveOutput", "R")] });
        Assert.Null(GraphRules.Validate(p)); Assert.Equal(2, PlcOpenGraphCodec.Decode(PlcOpenGraphCodec.Encode(p)).Program.Nodes[3].Associations!.Length);
    }
    [Fact] public void XML_list_order_must_agree_with_vertical_association_order()
    {
        var xml = XDocument.Parse(PlcOpenGraphCodec.Encode(Sample())); var actions = xml.Descendants(ExchangeXml.Plc + "actionBlock").First().Elements(ExchangeXml.Plc + "action").ToArray();
        actions[1].Element(ExchangeXml.Plc + "relPosition")!.SetAttributeValue("y", "0");
        Assert.Equal("invalid_sfc_action_order", Assert.Throws<ProjectEditException>(() => PlcOpenGraphCodec.Decode(xml.ToString())).Code);
        actions[1].Element(ExchangeXml.Plc + "relPosition")!.SetAttributeValue("y", "100");
        Assert.Equal(PlcOpenGraphCodec.SemanticHash(Sample()), PlcOpenGraphCodec.Decode(xml.ToString()).SemanticSha256);
    }
    [Fact] public void Unknown_action_content_or_hidden_coordinate_properties_are_rejected()
    {
        foreach (var part in new[] { "action", "reference", "relPosition" })
        {
            var xml = XDocument.Parse(PlcOpenGraphCodec.Encode(Sample()));
            var action = xml.Descendants(ExchangeXml.Plc + "actionBlock").First().Element(ExchangeXml.Plc + "action")!;
            var element = part == "action" ? action : action.Element(ExchangeXml.Plc + part)!; element.SetAttributeValue("vendorFlag", "true");
            Assert.Equal("graph_contains_unrepresented_data", Assert.Throws<ProjectEditException>(() => PlcOpenGraphCodec.Decode(xml.ToString())).Code);
        }
    }
    [Fact] public void Single_legacy_association_can_be_normalized_without_changing_old_hash()
    {
        var p = ExchangeTests.Sample("sfc-sequence"); var list = p with { Profile = "graph-v2", Nodes = p.Nodes.Select(n => n.Kind == "actionBlock"
            ? n with { Action = null, Associations = [new(n.Id, n.Action!)] } : n).ToArray() };
        Assert.Equal(PlcOpenGraphCodec.SemanticHash(p), PlcOpenGraphCodec.SemanticHash(list));
        Assert.All(PlcOpenGraphCodec.Decode(PlcOpenGraphCodec.Encode(list)).Program.Nodes.Where(n => n.Kind == "actionBlock"), n => Assert.Null(n.Associations));
    }
    [Fact] public void Program_wide_action_budget_includes_every_block()
    {
        var p = Sample(); var block = p.Nodes[3] with { Associations = Enumerable.Range(1, 64).Select(i => new GraphActionAssociation(i, "DriveOutput")).ToArray() };
        var oversized = p with { Nodes = [..p.Nodes, ..Enumerable.Range(100, 4).Select(i => block with { Id = i })] };
        Assert.Equal("invalid_sfc_action_associations", GraphRules.Validate(oversized));
    }
}
