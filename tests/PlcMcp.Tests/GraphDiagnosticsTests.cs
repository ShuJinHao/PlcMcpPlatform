using System.Text.Json;
using PlcMcp.Core.Workspaces;
using PlcMcp.Services.Contracts.Workers;
using Xunit;

namespace PlcMcp.Tests;

public sealed class GraphDiagnosticsTests
{
    [Theory]
    [InlineData("pouCase", "invalid_graph_definition", "program.pouType:", "case-sensitive")]
    [InlineData("pouUpper", "invalid_graph_definition", "program.pouType:", "program, functionBlock or function")]
    [InlineData("name", "invalid_graph_definition", "program.name:", "IEC identifier")]
    [InlineData("rail", "graph_node_outside_profile", "program.nodes[0].kind:", "leftPowerRail")]
    [InlineData("fbdKind", "graph_node_outside_profile", "program.nodes[0].kind:", "inVariable")]
    [InlineData("sfcKind", "graph_node_outside_profile", "program.nodes[0].kind:", "step, transition")]
    [InlineData("x", "graph_node_outside_profile", "program.nodes[0].x:", "0 through 100000")]
    [InlineData("y", "graph_node_outside_profile", "program.nodes[0].y:", "0 through 100000")]
    [InlineData("typeName", "invalid_graph_pin", "program.nodes[0].typeName:", "Only block")]
    [InlineData("instanceName", "invalid_graph_pin", "program.nodes[0].instanceName:", "Only block")]
    public void Observed_model_mistakes_receive_exact_names_and_locations(string change, string code, string path, string correction)
    {
        var p = GraphDeclarationTests.Sample(); var nodes = p.Nodes.ToArray();
        nodes[0] = change switch
        {
            "rail" => nodes[0] with { Kind = "powerRailLeft" },
            "fbdKind" => nodes[0] with { Kind = "input" },
            "sfcKind" => nodes[0] with { Kind = "initialStep" },
            "x" => nodes[0] with { X = -1 },
            "y" => nodes[0] with { Y = 100001 },
            "typeName" => nodes[0] with { TypeName = "DINT" },
            "instanceName" => nodes[0] with { InstanceName = "Input" },
            _ => nodes[0]
        };
        p = p with { Nodes = nodes, PouType = change == "pouCase" ? "Program" : change == "pouUpper" ? "PROGRAM" : p.PouType,
            Name = change == "name" ? "bad name" : p.Name, Language = change == "rail" ? "LD" : change == "sfcKind" ? "SFC" : p.Language };
        Assert.Equal(code, GraphRules.Validate(p));
        var explanation = GraphDiagnostics.Explain(p, code);
        Assert.StartsWith(path, explanation); Assert.Contains(correction, explanation);
        Assert.Equal(code, GraphRules.Validate(p));
    }

    [Theory]
    [InlineData("forms", "invalid_graph_variables", "program.variables[0].structMembers:")]
    [InlineData("pointer", "invalid_graph_variables", "program.variables[0].pointerTo.type:")]
    [InlineData("documentation", "invalid_graph_documentation", "program.variables[0].documentation / documentationXhtml:")]
    [InlineData("memberDocumentation", "invalid_graph_variables", "program.variables[0].structMembers[0].documentation / documentationXhtml:")]
    [InlineData("initializer", "invalid_graph_initializer", "program.variables[0].initializer:")]
    [InlineData("duplicate", "invalid_graph_variables", "program.variables[1].name:")]
    public void Declaration_diagnostics_name_only_fields_that_are_actually_invalid(string change, string code, string path)
    {
        var p = GraphDeclarationTests.Sample(); var variables = p.Variables.ToArray();
        const string xhtml = "<p xmlns='http://www.w3.org/1999/xhtml'>text</p>";
        variables[0] = change switch
        {
            "forms" => variables[0] with { Type = "STRUCT", StructMembers = [new("Count", new("INT"))], PointerTo = new("INT") },
            "pointer" => variables[0] with { Type = "POINTER", PointerTo = new("not a type") },
            "documentation" => variables[0] with { DocumentationXhtml = xhtml },
            "memberDocumentation" => variables[0] with { Type = "STRUCT", StructMembers = [new("Count", new("INT"), Documentation: "text", DocumentationXhtml: xhtml)] },
            "initializer" => variables[0] with { Initializer = new("struct", Members: [new("Count", new("simple", "1"))]) },
            _ => variables[0]
        };
        if (change == "duplicate") variables[1] = variables[1] with { Name = variables[0].Name };
        p = p with { Variables = variables };
        Assert.Equal(code, GraphRules.Validate(p));
        Assert.StartsWith(path, GraphDiagnostics.Explain(p, code));
        Assert.Equal(code, GraphRules.Validate(p));
    }

    [Theory]
    [InlineData("order", "graph_field_not_applicable", "program.nodes[3].executionOrder:")]
    [InlineData("ports", "invalid_graph_ports", "program.nodes[3].outputs:")]
    [InlineData("qualifier", "invalid_sfc_action", "program.nodes[3].associations[0].qualifier:")]
    [InlineData("nodeQualifier", "graph_field_not_applicable", "program.nodes[0].qualifier:")]
    [InlineData("edge", "invalid_graph_modifier", "program.nodes[0].edge:")]
    [InlineData("storage", "invalid_graph_modifier", "program.nodes[0].storage:")]
    [InlineData("connection", "dangling_graph_connection", "program.nodes[3].inputs[0].connections[0].sourceId:")]
    [InlineData("sourcePin", "unknown_graph_output_pin", "program.nodes[3].inputs[0].connections[0].sourcePin:")]
    [InlineData("action", "invalid_sfc_topology", "program.nodes[3].associations[0].action:")]
    [InlineData("mixed", "ambiguous_sfc_action_associations", "program.nodes[3].associations:")]
    [InlineData("empty", "invalid_sfc_action_associations", "program.nodes[3].associations:")]
    [InlineData("nullPin", "invalid_graph_pin", "program.nodes[3].inputs[0]:")]
    public void Rejection_identifies_the_real_input_field_without_mutating_or_weakening_validation(string change, string code, string path)
    {
        var p = GraphAssociationTests.Sample();
        Assert.Null(GraphRules.Validate(p));
        var nodes = p.Nodes.ToArray(); var action = nodes[3];
        nodes[3] = change switch
        {
            "order" => action with { ExecutionOrder = 1 },
            "ports" => action with { Outputs = [new("")] },
            "qualifier" => action with { Associations = [action.Associations![0] with { Qualifier = null! }] },
            "connection" => action with { Inputs = [new("", [new(999)])] },
            "sourcePin" => action with { Inputs = [new("", [new(p.Nodes[0].Id, "ENO")])] },
            "mixed" => action with { Action = "DriveOutput" },
            "empty" => action with { Associations = [] },
            "nullPin" => action with { Inputs = [null!] },
            _ => action
        };
        nodes[0] = change switch
        {
            "nodeQualifier" => nodes[0] with { Qualifier = null! },
            "edge" => nodes[0] with { Edge = null! },
            "storage" => nodes[0] with { Storage = null! },
            _ => nodes[0]
        };
        p = p with { Nodes = nodes, Actions = change == "action" ? [] : p.Actions };
        var before = JsonSerializer.Serialize(p, WorkerJson.Options);
        Assert.Equal(code, GraphRules.Validate(p));
        Assert.StartsWith(path, GraphDiagnostics.Explain(p, code));
        Assert.Equal(before, JsonSerializer.Serialize(p, WorkerJson.Options));
        Assert.Equal(code, GraphRules.Validate(p));
    }

    [Fact]
    public void Unknown_or_artifact_errors_do_not_invent_an_offending_node_index()
    {
        var p = GraphAssociationTests.Sample();
        Assert.StartsWith("program:", GraphDiagnostics.Explain(p, "graph_contains_unrepresented_data"));
        Assert.DoesNotContain("nodes[", GraphDiagnostics.Explain(p, "vendor_graph_failure"));
        Assert.StartsWith("program:", GraphDiagnostics.Explain(null, "invalid_graph_definition"));
        Assert.StartsWith("objectId / expectedGraphSha256:", GraphDiagnostics.ExplainWrite("expected_graph_revision_required"));
        Assert.StartsWith("artifactId:", GraphDiagnostics.ExplainWrite("graph_contains_unrepresented_data"));
        Assert.Null(GraphDiagnostics.ExplainWrite("unknown_outcome"));
    }
}
