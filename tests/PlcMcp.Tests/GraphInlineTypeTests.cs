using System.Text.Json;
using PlcMcp.Core.Workspaces;
using PlcMcp.EngineeringService.Exchange;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Workers;
using Xunit;

namespace PlcMcp.Tests;

public sealed class GraphInlineTypeTests
{
    public static GraphicalProgram Sample() => new("InlineTypes", "program", "FBD",
        [new("State", "ENUM", InitialValue: "Idle", EnumValues: [new("Idle", "0"), new("Running"), new("Fault", "100")]),
         new("WideState", "UDINT", EnumValues: [new("A", "4294967294"), new("B")]),
         new("Limit", "INT", InitialValue: "10", Subrange: new("-100", "100")),
         new("Wide", "ULINT", Subrange: new("0", "18446744073709551615")),
         new("History", "ENUM", Dimensions: [new(0, 2)], EnumValues: [new("Off"), new("On")],
             Initializer: new("array", Items: [new(new("simple", "Off"), 3)])), new("Result", "INT")],
        [new(1, "inVariable", Expression: "Limit", Outputs: [new("")]),
         new(2, "outVariable", Expression: "Result", Inputs: [new("", [new(1)])], ExecutionOrder: 1)], Profile: "graph-v2");

    [Fact] public void Enum_subrange_and_array_element_types_roundtrip_without_number_precision_loss()
    {
        var p = Sample(); Assert.Null(GraphRules.Validate(p));
        var xml = PlcOpenGraphCodec.Encode(p); var read = PlcOpenGraphCodec.Decode(xml);
        Assert.Equal(JsonSerializer.Serialize(p.Variables, WorkerJson.Options), JsonSerializer.Serialize(read.Program.Variables, WorkerJson.Options));
        Assert.Equal(PlcOpenGraphCodec.SemanticHash(p), read.SemanticSha256);
        Assert.Contains("subrangeUnsigned", xml); Assert.Contains("18446744073709551615", xml);
        Assert.Equal("graph_profile_mismatch", GraphRules.Validate(p with { Profile = "graph-v1" }));
    }
    [Fact] public void Type_changes_affect_semantic_hash_including_enum_order_and_implicit_values()
    {
        var p = Sample(); var vars = p.Variables.ToArray();
        vars[0] = vars[0] with { EnumValues = [new("Idle", "0"), new("Running", "1"), new("Fault", "100")] };
        Assert.NotEqual(PlcOpenGraphCodec.SemanticHash(p), PlcOpenGraphCodec.SemanticHash(p with { Variables = vars }));
        vars = p.Variables.ToArray(); vars[2] = vars[2] with { Subrange = new("-99", "100") };
        Assert.NotEqual(PlcOpenGraphCodec.SemanticHash(p), PlcOpenGraphCodec.SemanticHash(p with { Variables = vars }));
        var function = p with { PouType = "function", ReturnType = new("UINT", Subrange: new("0", "100")) };
        Assert.Equal(PlcOpenGraphCodec.SemanticHash(function), PlcOpenGraphCodec.Decode(PlcOpenGraphCodec.Encode(function)).SemanticSha256);
    }
    [Fact] public void Bounds_and_ambiguous_type_combinations_are_rejected()
    {
        GraphDataType[] invalid = [new("INT", Subrange: new("-32769", "0")), new("UINT", Subrange: new("-1", "2")),
            new("INT", Subrange: new("2", "1")), new("ULINT", Subrange: new("0", "18446744073709551616")),
            new("REAL", Subrange: new("0", "1")), new("INT", EnumValues: [new("x", "32767"), new("y")]),
            new("ENUM", EnumValues: [new("x"), new("X")]), new("ENUM", EnumValues: []),
            new("INT", EnumValues: [new("x")], Subrange: new("0", "100")),
            new("INT", EnumValues: [new("x", "1 + 2")]), new("STRING", StringLength: 80, EnumValues: [new("x")])];
        foreach (var type in invalid) Assert.False(GraphV2Rules.TypeValid(type));
    }
    [Fact] public void Unknown_nested_metadata_and_mismatched_signedness_never_disappear_on_decode()
    {
        var xml = PlcOpenGraphCodec.Encode(Sample());
        foreach (var altered in new[] { xml.Replace("name=\"Running\"", "name=\"Running\" vendorFlag=\"true\""),
            xml.Replace("subrangeUnsigned", "subrangeSigned"), xml.Replace("<values>", "<values><vendor/>") })
            Assert.Throws<ProjectEditException>(() => PlcOpenGraphCodec.Decode(altered));
        var nested = "<INT />";
        for (var i = 0; i < 64; i++) nested = "<enum><values><value name=\"x\" /></values><baseType>"+nested+"</baseType></enum>";
        Assert.Throws<ProjectEditException>(() => PlcOpenGraphCodec.Decode(xml.Replace("<INT />", nested)));
    }
}
