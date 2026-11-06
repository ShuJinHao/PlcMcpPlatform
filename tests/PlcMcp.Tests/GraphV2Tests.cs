using System.Text.Json;
using System.Xml.Linq;
using PlcMcp.Core.Workspaces;
using PlcMcp.EngineeringService.Exchange;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Workers;
using Xunit;

namespace PlcMcp.Tests;

public sealed class GraphV2Tests
{
    private static GraphicalProgram Sample(string name) => ExchangeTests.Sample(name);
    private static GraphicalProgram Change(GraphicalProgram p, int id, Func<GraphNode, GraphNode> edit)
        => p with { Nodes = p.Nodes.Select(n => n.Id == id ? edit(n) : n).ToArray() };
    [Theory]
    [InlineData("ld-v2-motor")]
    [InlineData("fbd-v2-data")]
    [InlineData("fbd-v2-function")]
    [InlineData("sfc-v2-branches")]
    public void Extended_graphs_roundtrip_with_complete_types_ports_and_priority(string name)
    {
        var p = Sample(name); var read = PlcOpenGraphCodec.Decode(PlcOpenGraphCodec.Encode(p));
        Assert.Equal("graph-v2", read.Profile);
        Assert.Equal(PlcOpenGraphCodec.SemanticHash(p), read.SemanticSha256);
        Assert.Equal(read.SemanticSha256, PlcOpenGraphCodec.Decode(PlcOpenGraphCodec.Encode(read.Program)).SemanticSha256);
        Assert.Equal("graph_profile_mismatch", GraphRules.Validate(p with { Profile = "graph-v1" }));
    }
    [Theory]
    [InlineData("ld-start-stop", "3086c50c8d96ed8f39e2573feb7679098de655ea9ad143ce7487dd7fc6ae428e")]
    [InlineData("fbd-delay-counter", "c433ca874c38ed32b5f6a4d9d3384df9d979313f8707b3327a57e05d058c55fa")]
    [InlineData("sfc-sequence", "702b826198dd96c0b63ef1820fb90b1c94e8c2067eec73edffd13063bf740484")]
    public void Existing_documents_keep_the_v1_profile(string name, string p7Hash)
    {
        var p = Sample(name); var read = PlcOpenGraphCodec.Decode(PlcOpenGraphCodec.Encode(p));
        Assert.Equal("graph-v1", read.Profile);
        // Golden values calculated with the merged P7 codec, not with the implementation under test.
        Assert.Equal(p7Hash, read.SemanticSha256);
        Assert.Equal(PlcOpenGraphCodec.SemanticHash(p), read.SemanticSha256);
    }
    [Fact] public void Types_and_return_values_are_part_of_the_semantic_hash()
    {
        var p = Sample("fbd-v2-function"); var hash = PlcOpenGraphCodec.SemanticHash(p);
        var changed = p with { ReturnType = new("DINT") };
        Assert.NotEqual(hash, PlcOpenGraphCodec.SemanticHash(changed));
        changed = p with { ReturnType = new("WSTRING", 80, [new(-2, 2), new(0, 4)]) };
        var read = PlcOpenGraphCodec.Decode(PlcOpenGraphCodec.Encode(changed)).Program;
        Assert.Equal("WSTRING", read.ReturnType!.Type); Assert.Equal(80, read.ReturnType.StringLength);
        Assert.Equal(new GraphDimension(-2, 2), read.ReturnType.Dimensions![0]);
        var f = Sample("fbd-v2-data");
        Assert.NotEqual(PlcOpenGraphCodec.SemanticHash(f), PlcOpenGraphCodec.SemanticHash(f with {
            Variables = f.Variables.Select(v => v.Name == "Values" ? v with { Dimensions = [new(-1, 7)] } : v).ToArray() }));
    }
    [Fact] public void Impossible_or_unrepresented_declarations_are_rejected()
    {
        var p = Sample("fbd-v2-function");
        Assert.Equal("invalid_graph_return_type", GraphRules.Validate(p with { ReturnType = null }));
        Assert.Equal("invalid_graph_return_type", GraphRules.Validate(p with { PouType = "program" }));
        Assert.Equal("graph_language_outside_profile", GraphRules.Validate(p with { Language = "SFC" }));
        foreach (var type in new[] { new GraphDataType("INT", 20), new("STRING", 0), new("INT", Dimensions: [new(3, 2)]),
            new("INT", Dimensions: [new(int.MinValue, int.MaxValue)]), new("INT", Dimensions: [new(0, 1000), new(0, 1000)]) })
            Assert.Equal("invalid_graph_return_type", GraphRules.Validate(p with { ReturnType = type }));
        Assert.Equal("invalid_graph_variables", GraphRules.Validate(p with { Variables = [new("Values", "INT", InitialValue: "1", Dimensions: [new(0, 3)])] }));
    }
    [Fact] public void Connector_resolution_checks_implicit_cycles_and_transitive_order()
    {
        var p = Sample("fbd-v2-data");
        Assert.Equal("unresolved_graph_continuation", GraphRules.Validate(Change(p, 3, n => n with { Name = "Missing" })));
        Assert.Equal("ambiguous_graph_connector", GraphRules.Validate(p with { Nodes = [..p.Nodes, p.Nodes.Single(n => n.Id == 2) with { Id = 99 }] }));
        Assert.Equal("graph_cycle_requires_state_variable", GraphRules.Validate(Change(p, 2, n => n with { Inputs = [new("", [new(10, "Value")])] })));
        var linked = Change(p, 8, n => new(n.Id, "continuation", Name: "Late", Outputs: [new("")]));
        linked = linked with { Nodes = [..linked.Nodes, new(99, "connector", Name: "Late", Inputs: [new("", [new(5, "Q")])])] };
        Assert.Null(GraphRules.Validate(linked));
        Assert.Equal("graph_execution_order_conflict", GraphRules.Validate(Change(linked, 5, n => n with { ExecutionOrder = 6 })));
    }
    [Fact] public void Inout_requires_a_variable_and_has_distinct_output_identity()
    {
        var p = Sample("fbd-v2-data");
        Assert.Equal("graph_inout_requires_variable", GraphRules.Validate(Change(p, 1, n => n with { Expression = "42" })));
        Assert.Equal("invalid_graph_inout", GraphRules.Validate(Change(p, 10, n => n with { InOuts = [n.InOuts![0] with { Negated = true }] })));
        Assert.Equal("duplicate_graph_pin", GraphRules.Validate(Change(p, 10, n => n with { Outputs = [new("Value")] })));
        Assert.Equal("unknown_graph_output_pin", GraphRules.Validate(Change(p, 12, n => n with { Inputs = [new("", [new(10, "Missing")])] })));
        Assert.Equal("invalid_graph_pin", GraphRules.Validate(Change(p, 10, n => n with { InOuts = [null!] })));
    }
    [Fact] public void Selection_priority_and_layout_cannot_silently_change_branch_choice()
    {
        var p = Sample("sfc-v2-branches");
        Assert.Equal("invalid_sfc_priority", GraphRules.Validate(Change(p, 3, n => n with { Priority = null })));
        Assert.Equal("invalid_sfc_priority", GraphRules.Validate(Change(p, 4, n => n with { Priority = 1 })));
        Assert.Equal("invalid_sfc_priority", GraphRules.Validate(Change(p, 4, n => n with { X = 0 })));
        var swapped = Change(Change(p, 3, n => n with { X = 200, Priority = 2 }), 4, n => n with { X = 0, Priority = 1 });
        swapped = Change(swapped, 2, n => n with { Outputs = n.Outputs!.Reverse().ToArray() });
        Assert.NotEqual(PlcOpenGraphCodec.SemanticHash(p), PlcOpenGraphCodec.SemanticHash(swapped));
    }
    [Fact] public void Parallel_join_requires_distinct_complete_matching_branches()
    {
        var p = Sample("sfc-v2-branches");
        Assert.Equal("invalid_sfc_topology", GraphRules.Validate(Change(p, 19, n => n with { Inputs = [new("", [new(17)]), new("", [new(17)])] })));
        Assert.Equal("invalid_sfc_branch_ports", GraphRules.Validate(Change(p, 14, n => n with { Inputs = [new("", [new(12, "Left")])] })));
        Assert.Equal("invalid_sfc_topology", GraphRules.Validate(Change(p, 19, n => n with { Kind = "selectionConvergence" })));
        Assert.Equal("unreachable_sfc_node", GraphRules.Validate(p with { Nodes = [..p.Nodes, new(98, "step", Name: "Orphan", Inputs: [new("", [new(99)])], Outputs: [new("")]), new(99, "transition", Expression: "FALSE", Inputs: [new("", [new(98)])], Outputs: [new("")])] }));
    }
    [Theory]
    [InlineData("returnType")]
    [InlineData("inOutVariables")]
    [InlineData("selectionDivergence")]
    public void Unknown_vendor_data_is_rejected_at_new_structures(string element)
    {
        var p = Sample(element == "returnType" ? "fbd-v2-function" : element == "inOutVariables" ? "fbd-v2-data" : "sfc-v2-branches");
        var xml = XDocument.Parse(PlcOpenGraphCodec.Encode(p));
        xml.Descendants(ExchangeXml.Plc + element).First().SetAttributeValue("vendorPrivate", "preserve-me");
        Assert.Equal("graph_contains_unrepresented_data", Assert.Throws<ProjectEditException>(() => PlcOpenGraphCodec.Decode(xml.ToString())).Code);
    }
    [Fact] public void Selection_can_be_nested_inside_a_parallel_branch_without_crossing_joins()
    {
        var p = Change(Sample("sfc-v2-branches"), 15, n => n with { Inputs = [new("", [new(38)])] });
        p = p with { Nodes = [..p.Nodes,
            new(30, "selectionDivergence", Inputs: [new("", [new(13)])], Outputs: [new("A"), new("B")]),
            new(31, "transition", X: 0, Expression: "Ready", Priority: 1, Inputs: [new("", [new(30, "A")])], Outputs: [new("")]),
            new(32, "transition", X: 200, Expression: "Bypass", Priority: 2, Inputs: [new("", [new(30, "B")])], Outputs: [new("")]),
            new(33, "step", Name: "ChoiceA", Inputs: [new("", [new(31)])], Outputs: [new("")]),
            new(34, "step", Name: "ChoiceB", Inputs: [new("", [new(32)])], Outputs: [new("")]),
            new(35, "transition", Expression: "TRUE", Inputs: [new("", [new(33)])], Outputs: [new("")]),
            new(36, "transition", Expression: "TRUE", Inputs: [new("", [new(34)])], Outputs: [new("")]),
            new(37, "selectionConvergence", Inputs: [new("", [new(35)]), new("", [new(36)])], Outputs: [new("")]),
            new(38, "step", Name: "ChoiceDone", Inputs: [new("", [new(37)])], Outputs: [new("")]) ] };
        Assert.Null(GraphRules.Validate(p));
        Assert.Equal(PlcOpenGraphCodec.SemanticHash(p), PlcOpenGraphCodec.Decode(PlcOpenGraphCodec.Encode(p)).SemanticSha256);
        var unjoined = new GraphicalProgram("Unjoined", "program", "SFC", [], [
            new(1, "step", Name: "Idle", Initial: true, Outputs: [new("")]),
            new(2, "selectionDivergence", Inputs: [new("", [new(1)])], Outputs: [new("A"), new("B")]),
            new(3, "transition", X: 0, Expression: "TRUE", Priority: 1, Inputs: [new("", [new(2, "A")])], Outputs: [new("")]),
            new(4, "transition", X: 200, Expression: "FALSE", Priority: 2, Inputs: [new("", [new(2, "B")])], Outputs: [new("")]),
            new(5, "jumpStep", Name: "Idle", Inputs: [new("", [new(3)])]),
            new(6, "jumpStep", Name: "Idle", Inputs: [new("", [new(4)])]) ], Profile: "graph-v2");
        Assert.Equal("sfc_branch_requires_matching_join", GraphRules.Validate(unjoined));
    }
}
