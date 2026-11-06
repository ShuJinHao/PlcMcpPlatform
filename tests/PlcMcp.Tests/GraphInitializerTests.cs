using System.Text.Json;
using System.Xml.Linq;
using PlcMcp.Core.Workspaces;
using PlcMcp.EngineeringService.Exchange;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Workers;
using Xunit;

namespace PlcMcp.Tests;

public sealed class GraphInitializerTests
{
    private static GraphInitializer Scalar(string value) => new("simple", value);
    private static GraphInitializer Array(params GraphArrayValue[] items) => new("array", Items: items);
    private static GraphInitializer Struct(params GraphStructValue[] members) => new("struct", Members: members);
    private static GraphicalProgram Program(params GraphVariable[] variables) => new("InitialValues", "program", "FBD",
        [new("Result", "INT"), ..variables], [new(1, "inVariable", Expression: "1", Outputs: [new("")]),
            new(2, "outVariable", Expression: "Result", Inputs: [new("", [new(1)])], ExecutionOrder: 1)], Profile: "graph-v2");
    public static GraphicalProgram Sample() => Program(
        new("Thresholds", "INT", Dimensions: [new(-1, 1), new(2, 3)], Initializer: Array(new GraphArrayValue(Scalar("10"), 2), new(Scalar("20"), 3))),
        new("Settings", "MachineSettings", Initializer: Struct(new GraphStructValue("Label", Scalar("'设备 & <A>'")),
            new("Limits", Array(new GraphArrayValue(Scalar("25"), 3))), new("Drive", Struct(new GraphStructValue("Enabled", Scalar("TRUE")))))),
        new("Drives", "DriveSettings", Dimensions: [new(0, 1)], Initializer: Array(new GraphArrayValue(Struct(new GraphStructValue("Enabled", Scalar("TRUE")), new("Speed", Scalar("100"))), 2))));

    [Fact] public void Aggregates_roundtrip_with_nested_values_partial_arrays_and_repetitions()
    {
        var p = Sample(); Assert.Null(GraphRules.Validate(p));
        var xml = PlcOpenGraphCodec.Encode(p); var read = PlcOpenGraphCodec.Decode(xml);
        Assert.Equal("graph-v2", read.Profile); Assert.Equal(PlcOpenGraphCodec.SemanticHash(p), read.SemanticSha256);
        Assert.Equal(JsonSerializer.Serialize(p.Variables, WorkerJson.Options), JsonSerializer.Serialize(read.Program.Variables, WorkerJson.Options));
        Assert.Contains("repetitionValue=\"3\"", xml); Assert.Contains("&amp;", xml);
        var document = XDocument.Parse(xml); document.Root!.Element(ExchangeXml.Plc + "fileHeader")!.SetAttributeValue("productVersion", "vendor");
        Assert.Equal("graph-v2", PlcOpenGraphCodec.Decode(document.ToString()).Profile);
        Assert.Equal("graph_profile_mismatch", GraphRules.Validate(p with { Profile = "graph-v1" }));
    }
    [Theory][InlineData("value")][InlineData("repeat")][InlineData("member")][InlineData("order")]
    public void Initializer_changes_affect_semantic_hash(string change)
    {
        var p = Sample(); var values = p.Variables.ToArray(); var v = values[1];
        values[1] = v with { Initializer = change switch {
            "value" => Array(new GraphArrayValue(Scalar("11"), 2), new(Scalar("20"), 3)),
            "repeat" => Array(new GraphArrayValue(Scalar("10"), 1), new(Scalar("20"), 3)),
            "order" => Array(v.Initializer!.Items!.Reverse().ToArray()), _ => v.Initializer } };
        if (change == "member") values[2] = values[2] with { Initializer = Struct(new GraphStructValue("Changed", Scalar("1"))) };
        Assert.NotEqual(PlcOpenGraphCodec.SemanticHash(p), PlcOpenGraphCodec.SemanticHash(p with { Variables = values }));
    }
    [Fact] public void Declared_capacity_and_element_shape_are_checked_without_evaluating_ST()
    {
        var v = new GraphVariable("A", "INT", Dimensions: [new(-2, 1)], Initializer: Array(new GraphArrayValue(Scalar("42"), 4)));
        Assert.Null(GraphRules.Validate(Program(v)));
        Assert.Equal("graph_initializer_exceeds_array", GraphRules.Validate(Program(v with { Initializer = Array(new GraphArrayValue(Scalar("42"), 5)) })));
        Assert.Equal("invalid_graph_initializer", GraphRules.Validate(Program(v with { Initializer = Array(new GraphArrayValue(Struct(new GraphStructValue("X", Scalar("1"))))) })));
        Assert.Equal("invalid_graph_initializer", GraphRules.Validate(Program(v with { Dimensions = null })));
        Assert.Equal("invalid_graph_initializer", GraphRules.Validate(Program(new GraphVariable("A", "INT", Initializer: Struct(new GraphStructValue("X", Scalar("1")))))));
        Assert.Equal("invalid_graph_initializer", GraphRules.Validate(Program(new GraphVariable("A", "DriveSettings", InitialValue: "legacy", Initializer: Struct(new GraphStructValue("X", Scalar("1")))))));
        Assert.Equal("invalid_graph_initializer", GraphRules.Validate(Program(new GraphVariable("A", "DriveSettings", Initializer: Scalar("1")))));
    }
    [Theory][InlineData(0)][InlineData(-1)][InlineData(65537)]
    public void Invalid_repeat_counts_are_rejected(int repeat)
    {
        Assert.Equal("invalid_graph_initializer", GraphRules.Validate(Program(new GraphVariable("Settings", "Config", Initializer: Struct(new GraphStructValue("A", Array(new GraphArrayValue(Scalar("1"), repeat))))))));
    }
    [Fact] public void Tagged_value_shape_and_member_identity_are_not_ambiguous()
    {
        var invalid = new[] { Struct(new GraphStructValue("X", Scalar("1")), new("x", Scalar("2"))), Struct(new GraphStructValue("X.Y", Scalar("1"))),
            Struct(new GraphStructValue("A", new("simple", "1", Items: []))), Struct(new GraphStructValue("A", new("array", "ignored", Items: [new(Scalar("1"))]))),
            Struct(new GraphStructValue("A", Array())), Struct(new GraphStructValue("A", new("other"))), Struct(new GraphStructValue("A", Scalar(""))), Struct(new GraphStructValue("A", null!)),
            Struct(new GraphStructValue("A", new("simple", "1", Members: []))), Struct(new GraphStructValue("A", Array(null!))), Struct(null!) };
        foreach (var value in invalid)
            Assert.Equal("invalid_graph_initializer", GraphRules.Validate(Program(new GraphVariable("Settings", "Config", Initializer: value))));
    }
    [Fact] public void Depth_node_and_expansion_limits_apply_before_encoding()
    {
        GraphInitializer value = Scalar("1");
        for (var i = 1; i < GraphInitializerRules.MaxDepth; i++) value = Struct(new GraphStructValue("Next", value));
        Assert.Null(GraphRules.Validate(Program(new GraphVariable("Settings", "Config", Initializer: value))));
        Assert.Equal("graph_initializer_too_large", GraphRules.Validate(Program(new GraphVariable("Settings", "Config", Initializer: Struct(new GraphStructValue("Next", value))))));
        var row = Struct(new GraphStructValue("Values", Array(new GraphArrayValue(Scalar("1"), 65536))));
        var huge = Struct(new GraphStructValue("Rows", Array(new GraphArrayValue(row, 2))));
        Assert.Equal("graph_initializer_too_large", GraphRules.Validate(Program(new GraphVariable("Settings", "Config", Initializer: huge))));
        var max = new GraphVariable("Max", "INT", Dimensions: [new(0, 65535)], Initializer: Array(new GraphArrayValue(Scalar("1"), 65536)));
        Assert.Null(GraphRules.Validate(Program(max)));
        Assert.Equal("graph_initializer_too_large", GraphRules.Validate(Program(Enumerable.Range(0, 5).Select(i => max with { Name = "A" + i }).ToArray())));
        var dense = Struct(Enumerable.Range(0, 256).Select(i => new GraphStructValue("M" + i, Scalar("1"))).ToArray());
        Assert.Equal("graph_initializer_too_large", GraphRules.Validate(Program(Enumerable.Range(0, 16).Select(i => new GraphVariable("A" + i, "Config", Initializer: dense)).ToArray())));
    }
    [Theory][InlineData("arrayValue")][InlineData("structValue")][InlineData("simpleValue")][InlineData("value")]
    public void Unknown_data_inside_initializers_is_preserved_by_refusing_loss(string element)
    {
        foreach (var attribute in new[] { "vendorFlag", "width", "height" })
        {
            var xml = XDocument.Parse(PlcOpenGraphCodec.Encode(Sample()));
            xml.Descendants(ExchangeXml.Plc + element).First().SetAttributeValue(attribute, "retain");
            Assert.Equal("graph_contains_unrepresented_data", Assert.Throws<ProjectEditException>(() => PlcOpenGraphCodec.Decode(xml.ToString())).Code);
        }
    }
    [Fact] public void Explicit_default_repeat_is_equivalent_but_unknown_attributes_are_not()
    {
        var p = Program(new GraphVariable("A", "INT", Dimensions: [new(0, 3)], Initializer: Array(new GraphArrayValue(Scalar("1")))));
        var xml = XDocument.Parse(PlcOpenGraphCodec.Encode(p));
        var value = xml.Descendants(ExchangeXml.Plc + "arrayValue").Single().Elements().Single();
        value.SetAttributeValue("repetitionValue", "1");
        Assert.Equal(PlcOpenGraphCodec.SemanticHash(p), PlcOpenGraphCodec.Decode(xml.ToString()).SemanticSha256);
        value.SetAttributeValue(XName.Get("repetitionValue", "urn:vendor"), "1");
        Assert.Equal("graph_contains_unrepresented_data", Assert.Throws<ProjectEditException>(() => PlcOpenGraphCodec.Decode(xml.ToString())).Code);
    }
    [Fact] public void XML_cannot_bypass_depth_limits_or_hide_geometry_inside_a_value()
    {
        var xml = XDocument.Parse(PlcOpenGraphCodec.Encode(Sample()));
        var leaf = xml.Descendants(ExchangeXml.Plc + "simpleValue").First();
        leaf.Add(new XElement(ExchangeXml.Plc + "position", new XAttribute("x", "1"), new XAttribute("y", "1")));
        Assert.Equal("graph_contains_unrepresented_data", Assert.Throws<ProjectEditException>(() => PlcOpenGraphCodec.Decode(xml.ToString())).Code);
        xml = XDocument.Parse(PlcOpenGraphCodec.Encode(Sample()));
        leaf = xml.Descendants(ExchangeXml.Plc + "simpleValue").First();
        XElement nested = new(ExchangeXml.Plc + "simpleValue", new XAttribute("value", "1"));
        for (var i = 0; i < GraphInitializerRules.MaxDepth; i++)
            nested = new(ExchangeXml.Plc + "structValue", new XElement(ExchangeXml.Plc + "value", new XAttribute("member", "Next"), nested));
        leaf.ReplaceWith(nested);
        Assert.Equal("graph_initializer_too_large", Assert.Throws<ProjectEditException>(() => PlcOpenGraphCodec.Decode(xml.ToString())).Code);
    }
    [Fact] public void Invalid_XML_aggregate_count_and_duplicate_choices_are_rejected()
    {
        var xml = XDocument.Parse(PlcOpenGraphCodec.Encode(Sample()));
        var item = xml.Descendants(ExchangeXml.Plc + "arrayValue").First().Elements().First();
        item.SetAttributeValue("repetitionValue", "65537");
        Assert.Throws<ProjectEditException>(() => PlcOpenGraphCodec.Decode(xml.ToString()));
        item.SetAttributeValue("repetitionValue", "2"); item.Add(new XElement(ExchangeXml.Plc + "simpleValue", new XAttribute("value", "2")));
        Assert.Equal("graph_contains_unrepresented_data", Assert.Throws<ProjectEditException>(() => PlcOpenGraphCodec.Decode(xml.ToString())).Code);
    }
}
