using System.Xml.Linq;
using PlcMcp.Core.Workspaces;
using PlcMcp.EngineeringService.Exchange;
using PlcMcp.Inovance;
using PlcMcp.Services.Contracts.Editing;
using Xunit;

namespace PlcMcp.Tests;

public sealed class GraphCompositeTypeTests
{
    private const string Rich = "<xhtml xmlns=\"http://www.w3.org/1999/xhtml\"><span width=\"12\">左</span> <b xml:lang=\"zh\">右</b></xhtml>";
    public static GraphicalProgram Sample() => new("CompositeTypes", "program", "FBD",
        [new("State", "STRUCT", StructMembers: [
            new("Count", new("INT"), InitialValue: "1", DocumentationXhtml: Rich),
            new("Values", new("UINT", Dimensions: [new(0, 1)]), Initializer: new("array", Items: [new(new("simple", "2"), 2)])),
            new("Next", new("POINTER", PointerTo: new("STRUCT", StructMembers: [new("Ready", new("BOOL"))])), InitialValue: "0")],
            Initializer: new("struct", Members: [new("Count", new("simple", "5"))])),
         new("Pointers", "POINTER", Dimensions: [new(0, 1)], PointerTo: new("INT"),
            Initializer: new("array", Items: [new(new("simple", "0"), 2)])),
         new("Counter", "INT", InitialValue: "0"), new("Result", "INT", DocumentationXhtml: Rich)],
        [new(1, "inVariable", Expression: "State.Count", Outputs: [new("")]),
         new(2, "outVariable", Expression: "Result", Inputs: [new("", [new(1)])], ExecutionOrder: 1)], Profile: "graph-v2",
        VariableGroups: [new("local", ["State", "Pointers", "Result"], DocumentationXhtml: Rich),
            new("local", ["Counter"], Retention: "retain", Persistence: "persistent")]);

    [Fact] public void Original_P14_fixture_roundtrips_in_the_existing_exchange_path()
    {
        var p = ExchangeTests.Sample("fbd-v2-standard-declarations");
        Assert.Null(GraphRules.Validate(p)); Assert.Null(new InovanceGraphPolicy().Validate(p));
        Assert.Equal(PlcOpenGraphCodec.SemanticHash(p), PlcOpenGraphCodec.Decode(PlcOpenGraphCodec.Encode(p)).SemanticSha256);
    }

    [Fact] public void Nested_struct_pointer_array_and_persistence_roundtrip_with_member_defaults()
    {
        var p = Sample(); Assert.Null(GraphRules.Validate(p)); Assert.Null(new InovanceGraphPolicy().Validate(p));
        var xml = PlcOpenGraphCodec.Encode(p); var read = PlcOpenGraphCodec.Decode(xml);
        Assert.Equal(PlcOpenGraphCodec.SemanticHash(p), read.SemanticSha256);
        Assert.Equal(new[] { "Count", "Values", "Next" }, read.Program.Variables[0].StructMembers!.Select(m => m.Name));
        Assert.Equal("BOOL", read.Program.Variables[0].StructMembers![2].Type.PointerTo!.StructMembers![0].Type.Type);
        Assert.Equal("persistent", read.Program.VariableGroups![1].Persistence);
        Assert.Contains("<pointer>", xml); Assert.Contains("persistent=\"true\"", xml);
        Assert.Contains("width=\"12\"", read.Program.Variables.Single(v => v.Name == "Result").DocumentationXhtml);
        Assert.Equal("graph_profile_mismatch", GraphRules.Validate(p with { Profile = "graph-v1" }));
        var function = p with { PouType = "function", ReturnType = new("POINTER", PointerTo: new("STRUCT", StructMembers: [new("Code", new("INT"))])) };
        Assert.Equal(PlcOpenGraphCodec.SemanticHash(function), PlcOpenGraphCodec.Decode(PlcOpenGraphCodec.Encode(function)).SemanticSha256);
    }

    [Fact] public void All_new_declaration_information_affects_hash_and_XHTML_whitespace_is_semantic()
    {
        var p = Sample(); var hash = PlcOpenGraphCodec.SemanticHash(p);
        foreach (var replacement in new[] { Rich.Replace("width=\"12\"", "width=\"13\""), Rich.Replace("</span> <b", "</span><b"), Rich.Replace("<b ", "<strong ").Replace("</b>", "</strong>") })
        {
            var vars = p.Variables.ToArray(); vars[3] = vars[3] with { DocumentationXhtml = replacement };
            Assert.NotEqual(hash, PlcOpenGraphCodec.SemanticHash(p with { Variables = vars }));
        }
        var changed = p.Variables.ToArray(); changed[0] = changed[0] with { StructMembers = changed[0].StructMembers!.Reverse().ToArray() };
        Assert.NotEqual(hash, PlcOpenGraphCodec.SemanticHash(p with { Variables = changed }));
        var groups = p.VariableGroups!.ToArray(); groups[1] = groups[1] with { Persistence = "nonPersistent" };
        Assert.NotEqual(hash, PlcOpenGraphCodec.SemanticHash(p with { VariableGroups = groups }));
        changed = p.Variables.ToArray(); changed[1] = changed[1] with { PointerTo = new("DINT") };
        Assert.NotEqual(hash, PlcOpenGraphCodec.SemanticHash(p with { Variables = changed }));
    }

    [Fact] public void Type_depth_member_count_global_budget_and_ambiguous_forms_are_bounded()
    {
        var deep = new GraphDataType("INT");
        for (var i = 0; i < GraphCompositeTypes.MaxDepth; i++) deep = new("POINTER", PointerTo: deep);
        Assert.False(GraphV2Rules.TypeValid(deep));
        Assert.False(GraphV2Rules.TypeValid(new("STRUCT", StructMembers: [new("x", new("INT")), new("X", new("BOOL"))])));
        Assert.False(GraphV2Rules.TypeValid(new("STRUCT", StructMembers: [])));
        Assert.False(GraphV2Rules.TypeValid(new("STRUCT", StructMembers: [new("x", new("INT"))], PointerTo: new("INT"))));
        Assert.False(GraphV2Rules.TypeValid(new("INT", PointerTo: new("INT"))));
        Assert.False(GraphV2Rules.TypeValid(new("STRUCT", StructMembers: Enumerable.Range(0,257).Select(i => new GraphStructMember("x"+i,new("INT"))).ToArray())));
        var vars = Enumerable.Range(0, 300).Select(i => new GraphVariable("v"+i,"STRUCT", StructMembers:
            Enumerable.Range(0,16).Select(j => new GraphStructMember("x"+j,new("INT"))).ToArray())).ToArray();
        Assert.Equal("invalid_graph_variables", GraphRules.Validate(Sample() with { Variables = vars, VariableGroups = null }));
    }

    [Fact] public void Inline_member_defaults_and_initializers_are_type_checked_without_evaluating_ST()
    {
        var p = Sample(); var vars = p.Variables.ToArray();
        vars[0] = vars[0] with { Initializer = new("struct", Members: [new("Missing", new("simple", "1"))]) };
        Assert.Equal("graph_initializer_unknown_member", GraphRules.Validate(p with { Variables = vars }));
        vars = p.Variables.ToArray(); var members = vars[0].StructMembers!.ToArray();
        members[1] = members[1] with { Initializer = new("array", Items: [new(new("simple", "1"), 3)]) };
        vars[0] = vars[0] with { StructMembers = members };
        Assert.Equal("graph_initializer_exceeds_array", GraphRules.Validate(p with { Variables = vars }));
        vars = p.Variables.ToArray(); members = vars[0].StructMembers!.ToArray();
        members[2] = members[2] with { InitialValue = null, Initializer = new("struct", Members: [new("Ready", new("simple", "TRUE"))]) };
        vars[0] = vars[0] with { StructMembers = members };
        Assert.Equal("invalid_graph_initializer", GraphRules.Validate(p with { Variables = vars }));
    }

    [Fact] public void XHTML_is_opaque_bounded_data_and_unknown_nodes_are_rejected_without_loss()
    {
        Assert.True(GraphDocumentation.Valid(null, Rich));
        Assert.True(GraphDocumentation.Valid(null, "<div xmlns=\"http://www.w3.org/1999/xhtml\" style=\"color:red\"><a href=\"https://example.invalid\">link</a></div>"));
        foreach (var xhtml in new[] { "<!DOCTYPE xhtml [<!ENTITY x 'value'>]><xhtml xmlns=\"http://www.w3.org/1999/xhtml\">&x;</xhtml>",
            Rich.Replace("<span", "<span xmlns=\"urn:vendor\""), Rich.Replace("</xhtml>", "<!-- secret --></xhtml>"),
            Rich.Replace("</xhtml>", "<?hidden data?></xhtml>"), new string('x',GraphDocumentation.MaxCharacters+1),
            "<xhtml xmlns=\"http://www.w3.org/1999/xhtml\">"+string.Concat(Enumerable.Repeat("<b>",17))+"text"+string.Concat(Enumerable.Repeat("</b>",17))+"</xhtml>" })
            Assert.False(GraphDocumentation.Valid(null, xhtml));
        Assert.False(GraphDocumentation.Valid("plain", Rich));
        var xml = PlcOpenGraphCodec.Encode(Sample());
        Assert.Throws<ProjectEditException>(() => PlcOpenGraphCodec.Decode(xml.Replace("<struct>", "<struct vendorFlag=\"1\">")));
        Assert.Throws<ProjectEditException>(() => PlcOpenGraphCodec.Decode(xml.Replace("<pointer>", "<pointer><addData/>")));
    }

    [Fact] public void Long_plain_XHTML_stays_structured_when_it_exceeds_the_legacy_plain_text_limit()
    {
        var p = Sample(); var vars = p.Variables.ToArray();
        vars[3] = vars[3] with { DocumentationXhtml = "<xhtml xmlns=\"http://www.w3.org/1999/xhtml\">" + new string('a', 5000) + "</xhtml>" };
        p = p with { Variables = vars };
        Assert.Null(GraphRules.Validate(p));
        var read = PlcOpenGraphCodec.Decode(PlcOpenGraphCodec.Encode(p));
        Assert.Null(read.Program.Variables.Single(v => v.Name == "Result").Documentation);
        Assert.Contains(new string('a', 5000), read.Program.Variables.Single(v => v.Name == "Result").DocumentationXhtml);
        Assert.Equal(PlcOpenGraphCodec.SemanticHash(p), read.SemanticSha256);
    }

    [Fact] public void Adjacent_CDATA_and_text_nodes_remain_structured_without_changing_existing_hash_rules()
    {
        var p = Sample(); var vars = p.Variables.ToArray();
        vars[3] = vars[3] with { DocumentationXhtml = "<xhtml xmlns=\"http://www.w3.org/1999/xhtml\"><![CDATA[one]]> two</xhtml>" };
        p = p with { Variables = vars };
        var read = PlcOpenGraphCodec.Decode(PlcOpenGraphCodec.Encode(p));
        var xhtml = read.Program.Variables.Single(v => v.Name == "Result").DocumentationXhtml;
        Assert.Contains("<![CDATA[one]]> two", xhtml);
        Assert.Equal(PlcOpenGraphCodec.SemanticHash(p), read.SemanticSha256);
    }

    [Fact] public void Explicit_false_and_numeric_persistence_flags_keep_existing_hashes()
    {
        var p = ExchangeTests.Sample("ld-start-stop"); var xml = XDocument.Parse(PlcOpenGraphCodec.Encode(p));
        var group = xml.Descendants(ExchangeXml.Plc+"localVars").Single();
        group.SetAttributeValue("persistent", "false"); group.SetAttributeValue("nonpersistent", "0");
        var read = PlcOpenGraphCodec.Decode(xml.ToString());
        Assert.Null(read.Program.VariableGroups); Assert.Equal("graph-v1", read.Profile);
        Assert.Equal(PlcOpenGraphCodec.SemanticHash(p), read.SemanticSha256);
        var richer = Sample(); var richXml = PlcOpenGraphCodec.Encode(richer).Replace("persistent=\"true\"", "persistent=\"1\"");
        Assert.Equal(PlcOpenGraphCodec.SemanticHash(richer), PlcOpenGraphCodec.Decode(richXml).SemanticSha256);
        Assert.Throws<ProjectEditException>(() => PlcOpenGraphCodec.Decode(richXml.Replace("persistent=\"1\"", "persistent=\"1\" nonpersistent=\"true\"")));
    }

    [Fact] public void Inovance_rejects_persistence_scope_and_nested_address_conflicts_before_writes()
    {
        var p = Sample(); var policy = new InovanceGraphPolicy();
        var groups = p.VariableGroups!.ToArray(); groups[1] = groups[1] with { Retention = "default" };
        Assert.Null(policy.Validate(p with { VariableGroups = groups }));
        Assert.Equal("inovance_persistence_requires_retain",policy.Validate(p with { PouType = "functionBlock", VariableGroups = groups }));
        Assert.Equal("inovance_persistence_scope_invalid",policy.Validate(p with { PouType = "function", ReturnType = new("INT"), VariableGroups = groups }));
        var vars = p.Variables.ToArray(); var members = vars[0].StructMembers!.ToArray(); members[0] = members[0] with { Address = "%MW0" };
        vars[0] = vars[0] with { StructMembers = members };
        Assert.Null(policy.Validate(p with { Variables = vars }));
        groups = p.VariableGroups!.ToArray(); groups[0] = groups[0] with { Persistence = "persistent" };
        Assert.Equal("inovance_persistent_address_conflict", policy.Validate(p with { Variables = vars, VariableGroups = groups }));
        groups[0] = groups[0] with { Persistence = "default", Retention = "retain" };
        Assert.Equal("inovance_retain_address_conflict", policy.Validate(p with { Variables = vars, VariableGroups = groups }));
    }
}
