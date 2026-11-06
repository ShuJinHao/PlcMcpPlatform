using System.Text.Json;
using System.Xml.Linq;
using PlcMcp.Core.Workspaces;
using PlcMcp.EngineeringService.Exchange;
using PlcMcp.Inovance;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Workers;
using Xunit;

namespace PlcMcp.Tests;

public sealed class GraphDeclarationTests
{
    public static GraphicalProgram Sample() => new("DeclaredProgram", "program", "FBD",
        [new("Request", "BOOL", Address: "%IX0.0", Documentation: "启动 & 停止\n输入 <A> 🟢"),
         new("Limit", "INT", InitialValue: "12"), new("Count", "DINT", InitialValue: "0"), new("Result", "BOOL"),
         new("Scratch", "INT", "temp", "0"), new("RemoteEnable", "BOOL", "external"), new("Flags", "BOOL", Dimensions: [new(0, 3)]), new("Enable", "BOOL", "input")],
        [new(1, "inVariable", Expression: "Request", Outputs: [new("")]),
         new(2, "outVariable", Expression: "Result", Inputs: [new("", [new(1)])], ExecutionOrder: 1)], Profile: "graph-v2",
        VariableGroups: [new("local", ["Request"], Name: "AddressedInputs"), new("local", ["Limit"], Constant: true, Name: "Limits"),
            new("local", ["Count"], Retention: "retain", Documentation: "运行统计\n由实际控制器决定保持行为"),
            new("local", ["Result"]), new("temp", ["Scratch"]), new("external", ["RemoteEnable"]), new("local", ["Flags"], Retention: "nonRetain"), new("input", ["Enable"])]);
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, WorkerJson.Options);
    [Fact] public void Declaration_groups_addresses_and_unicode_documentation_roundtrip()
    {
        var p = Sample(); Assert.Null(GraphRules.Validate(p)); Assert.Null(new InovanceGraphPolicy().Validate(p));
        var xml = PlcOpenGraphCodec.Encode(p); var read = PlcOpenGraphCodec.Decode(xml);
        Assert.Equal(Json(p.Variables), Json(read.Program.Variables)); Assert.Equal(Json(p.VariableGroups), Json(read.Program.VariableGroups));
        Assert.Equal(PlcOpenGraphCodec.SemanticHash(p), read.SemanticSha256); Assert.Contains("&amp;", xml);
        Assert.Contains("nonretain=\"true\"", xml); Assert.Equal("graph-v2", PlcOpenGraphCodec.Decode(xml.Replace("graph-v2", "vendor")).Profile);
        Assert.Equal("graph_profile_mismatch", GraphRules.Validate(p with { Profile = "graph-v1" }));
    }
    [Theory][InlineData("address")][InlineData("documentation")][InlineData("constant")][InlineData("retention")][InlineData("groupOrder")][InlineData("groupBoundary")]
    public void All_declaration_information_participates_in_revision_hash(string change)
    {
        var p = Sample(); var vars = p.Variables.ToArray(); var groups = p.VariableGroups!.ToArray();
        if (change == "address") vars[0] = vars[0] with { Address = "%IX0.1" };
        if (change == "documentation") vars[0] = vars[0] with { Documentation = "变更后的说明" };
        if (change == "constant") groups[1] = groups[1] with { Constant = false };
        if (change == "retention") groups[2] = groups[2] with { Retention = "nonRetain" };
        if (change == "groupOrder") groups = groups.Reverse().ToArray();
        if (change == "groupBoundary") groups[3] = groups[3] with { Name = "ExplicitGroup" };
        Assert.NotEqual(PlcOpenGraphCodec.SemanticHash(p), PlcOpenGraphCodec.SemanticHash(p with { Variables = vars, VariableGroups = groups }));
    }
    [Fact] public void Every_group_reference_must_match_one_variable_once_and_its_scope()
    {
        var p = Sample(); var g = p.VariableGroups!;
        var bad = new GraphVariableGroup[][] { [], g[..^1], [..g, g[0]], [g[0] with { Variables = ["request"] }, ..g[1..]],
            [g[0] with { Scope = "output" }, ..g[1..]], [g[0] with { Variables = ["Unknown"] }, ..g[1..]],
            [g[0] with { Variables = [] }, ..g[1..]], [null!, ..g[1..]], [g[0] with { Variables = [null!] }, ..g[1..]],
            Enumerable.Repeat(g[0], GraphDeclarationRules.MaxGroups + 1).ToArray() };
        foreach (var groups in bad) Assert.Equal("invalid_graph_variable_groups", GraphRules.Validate(p with { VariableGroups = groups }));
        Assert.Equal("invalid_graph_variable_groups", GraphRules.Validate(p with { VariableGroups = [g[0] with { Retention = "persistent" }, ..g[1..]] }));
        Assert.Equal("invalid_graph_variable_groups", GraphRules.Validate(p with { VariableGroups = [g[0] with { Constant = true, Retention = "retain" }, ..g[1..]] }));
    }
    [Fact] public void Constants_require_an_initializer_but_external_constants_can_be_represented()
    {
        var p = Sample(); Assert.Equal("graph_constant_requires_initializer", GraphRules.Validate(p with { Variables = p.Variables.Select(v => v.Name == "Limit" ? v with { InitialValue = null } : v).ToArray() }));
        var groups = p.VariableGroups!.Select(g => g.Scope == "external" ? g with { Constant = true } : g).ToArray();
        Assert.Null(GraphRules.Validate(p with { VariableGroups = groups }));
        Assert.Equal("inovance_external_declaration_outside_profile", new InovanceGraphPolicy().Validate(p with { VariableGroups = groups }));
    }
    [Theory][InlineData("%IX0.0")][InlineData("%QW0")][InlineData("%MD48")][InlineData("%I7.5")][InlineData("%M4294967295")]
    public void Explicit_bounded_addresses_are_representable(string address) => Assert.True(GraphDeclarationRules.AddressValid(address));
    [Theory][InlineData("")][InlineData("AT %IW0")][InlineData("%I*")][InlineData("%IW0\n")][InlineData("%M4294967296")][InlineData("%I0.1.2.3.4")]
    public void Unsupported_address_forms_fail_before_XML_generation(string address)
    {
        var p = Sample(); Assert.Equal("invalid_graph_address", GraphRules.Validate(p with { Variables = p.Variables.Select(v => v.Name == "Request" ? v with { Address = address } : v).ToArray() }));
    }
    [Fact] public void Documentation_is_bounded_plain_XML_text_with_defined_line_endings()
    {
        var p = Sample();
        foreach (var invalid in new[] { new string('a', 4097), "bad\0text", "bad\u0001", "bad\uFFFF", "bad\uD800", "bad\uDC00" })
            Assert.Equal("invalid_graph_documentation", GraphRules.Validate(p with { Variables = p.Variables.Select(v => v.Name == "Request" ? v with { Documentation = invalid } : v).ToArray() }));
        var normalized = p with { Variables = p.Variables.Select(v => v.Name == "Request" ? v with { Documentation = "一\r\n二\r三" } : v).ToArray() };
        Assert.Equal("一\n二\n三", PlcOpenGraphCodec.Decode(PlcOpenGraphCodec.Encode(normalized)).Program.Variables[0].Documentation);
    }
    [Fact] public void Empty_and_formatted_documentation_are_not_confused()
    {
        var p = Sample() with { Variables = Sample().Variables.Select(v => v.Name == "Request" ? v with { Documentation = "" } : v).ToArray() };
        var xml = XDocument.Parse(PlcOpenGraphCodec.Encode(p)); Assert.Equal("", PlcOpenGraphCodec.Decode(xml.ToString()).Program.Variables[0].Documentation);
        xml.Descendants(ExchangeXml.Xhtml + "xhtml").First().Add(new XElement(ExchangeXml.Xhtml + "b", "formatting"));
        Assert.Equal("graph_contains_unrepresented_data", Assert.Throws<ProjectEditException>(() => PlcOpenGraphCodec.Decode(xml.ToString())).Code);
    }
    [Fact] public void Unknown_attributes_and_conflicting_XML_group_flags_are_not_ignored()
    {
        foreach (var tag in new[] { "inputVars", "variable", "type", "BOOL", "simpleValue", "documentation" })
        {
            var xml = XDocument.Parse(PlcOpenGraphCodec.Encode(Sample())); xml.Descendants(ExchangeXml.Plc + tag).First().SetAttributeValue("width", "7");
            Assert.Equal("graph_contains_unrepresented_data", Assert.Throws<ProjectEditException>(() => PlcOpenGraphCodec.Decode(xml.ToString())).Code);
        }
        var doc = XDocument.Parse(PlcOpenGraphCodec.Encode(Sample())); var group = doc.Descendants(ExchangeXml.Plc + "localVars").First();
        group.SetAttributeValue("retain", true); group.SetAttributeValue("nonretain", true);
        Assert.Equal("invalid_graph_variable_groups", Assert.Throws<ProjectEditException>(() => PlcOpenGraphCodec.Decode(doc.ToString())).Code);
    }
    [Fact] public void Explicit_false_XML_flags_do_not_change_legacy_hashes()
    {
        var p = ExchangeTests.Sample("ld-start-stop"); var xml = XDocument.Parse(PlcOpenGraphCodec.Encode(p));
        var group = xml.Descendants(ExchangeXml.Plc + "localVars").Single();
        group.SetAttributeValue("constant", "false"); group.SetAttributeValue("retain", "0"); group.SetAttributeValue("nonretain", "false");
        var read = PlcOpenGraphCodec.Decode(xml.ToString()); Assert.Null(read.Program.VariableGroups); Assert.Equal("graph-v1", read.Profile);
        Assert.Equal(PlcOpenGraphCodec.SemanticHash(p), read.SemanticSha256);
    }
    [Fact] public void Inovance_declaration_rules_are_separate_from_portable_graph_validation()
    {
        var p = Sample(); var policy = new InovanceGraphPolicy();
        var retainedAddress = p with { Variables = p.Variables.Select(v => v.Name == "Count" ? v with { Address = "%MD0" } : v).ToArray() };
        Assert.Null(GraphRules.Validate(retainedAddress)); Assert.Equal("inovance_retain_address_conflict", policy.Validate(retainedAddress));
        var externalInit = p with { Variables = p.Variables.Select(v => v.Name == "RemoteEnable" ? v with { InitialValue = "FALSE" } : v).ToArray() };
        Assert.Null(GraphRules.Validate(externalInit)); Assert.Equal("inovance_external_declaration_invalid", policy.Validate(externalInit));
        var function = p with { PouType = "function", ReturnType = new("BOOL"), Variables = [new("Scratch", "INT", "temp"), new("Result", "BOOL"), new("Request", "BOOL", "input")], VariableGroups = null };
        Assert.Null(GraphRules.Validate(function)); Assert.Equal("inovance_temporary_declaration_invalid", policy.Validate(function));
    }
    [Fact] public void XML_numeric_true_flags_keep_the_same_declaration_semantics()
    {
        var p = Sample(); var xml = PlcOpenGraphCodec.Encode(p).Replace("constant=\"true\"", "constant=\"1\"")
            .Replace("retain=\"true\"", "retain=\"1\"").Replace("nonretain=\"true\"", "nonretain=\"1\"");
        Assert.Equal(PlcOpenGraphCodec.SemanticHash(p), PlcOpenGraphCodec.Decode(xml).SemanticSha256);
    }
    [Theory][InlineData("input")][InlineData("output")][InlineData("inOut")][InlineData("temp")]
    public void Addressed_interface_and_temporary_variables_are_portable_but_not_in_the_target_subset(string scope)
    {
        var p = Sample();
        p = p with { Variables = p.Variables.Select(v => v.Name == "Request" ? v with { Scope = scope } : v).ToArray(),
            VariableGroups = p.VariableGroups!.Select(g => g.Variables.Contains("Request") ? g with { Scope = scope } : g).ToArray() };
        Assert.Null(GraphRules.Validate(p)); Assert.Equal("inovance_address_scope_invalid", new InovanceGraphPolicy().Validate(p));
    }
}
