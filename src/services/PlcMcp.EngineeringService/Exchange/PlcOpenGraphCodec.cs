using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using PlcMcp.Core.Workspaces;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Exchange;

namespace PlcMcp.EngineeringService.Exchange;

/// <summary>Original TC6 2.01 graph-v1/v2 codec. Rejects unrepresented program data instead of silently discarding it.</summary>
public static class PlcOpenGraphCodec
{
    private static readonly XNamespace P = ExchangeXml.Plc, H = ExchangeXml.Xhtml;
    private static readonly string[] PrimitiveTypes = ["BOOL", "SINT", "INT", "DINT", "LINT", "USINT", "UINT", "UDINT", "ULINT", "REAL", "LREAL", "TIME", "DATE", "DT", "TOD", "BYTE", "WORD", "DWORD", "LWORD"];
    private static XElement E(string name, params object?[] content) => new(P + name, content);
    private static XAttribute? A(string name, object? value) => value is null ? null : new(name, value);
    private static XElement Position(string name, decimal x = 0, decimal y = 0) => E(name, A("x", x), A("y", y));
    private static XElement St(string source) => E("ST", new XElement(H + "xhtml", source));
    public static string Encode(GraphicalProgram program)
    {
        if (GraphRules.Validate(program) is { } error) throw new ProjectEditException(error);
        var root = E("project", E("fileHeader", A("companyName", "PlcMcpPlatform"), A("productName", "PlcMcpPlatform"),
            A("productVersion", program.Profile), A("creationDateTime", "2000-01-01T00:00:00Z")),
            E("contentHeader", A("name", program.Name), E("coordinateInfo", new[] { "fbd", "ld", "sfc" }.Select(l => E(l, E("scaling", A("x", 1), A("y", 1)))))),
            E("types", E("dataTypes"), E("pous", EncodePou(program))), E("instances", E("configurations")));
        var xml = new XDocument(root).ToString(SaveOptions.DisableFormatting);
        _ = ExchangeXml.Parse(xml); return xml;
    }
    private static XElement EncodePou(GraphicalProgram p)
    {
        var iface = E("interface", p.ReturnType is null ? null : E("returnType", EncodeType(p.ReturnType)));
        iface.Add(PlcOpenDeclarationCodec.Encode(p));
        return E("pou", A("name", p.Name), A("pouType", p.PouType), iface,
            p.Actions is { Length: > 0 } ? E("actions", p.Actions.Select(a => E("action", A("name", a.Name), E("body", St(a.Implementation))))) : null,
            E("body", E(p.Language, p.Nodes.Select(EncodeNode))));
    }
    internal static XElement EncodeType(GraphDataType type)
    {
        var scalar = PrimitiveTypes.Contains(type.Type) ? E(type.Type) : type.Type is "STRING" or "WSTRING"
            ? E(type.Type.ToLowerInvariant(), A("length", type.StringLength)) : E("derived", A("name", type.Type));
        return type.Dimensions is null ? scalar : E("array", type.Dimensions.Select(d => E("dimension", A("lower", d.Lower), A("upper", d.Upper))), E("baseType", scalar));
    }
    internal static GraphDataType DecodeType(XElement container)
    {
        PlcOpenDeclarationCodec.Attributes(container);
        var type = container.Elements().Single(); GraphDimension[]? dimensions = null;
        if (type.Name == P + "array")
        {
            PlcOpenDeclarationCodec.Attributes(type);
            if (type.Elements().Any(e => e.Name != P + "dimension" && e.Name != P + "baseType")) throw new ProjectEditException("graph_contains_unrepresented_data");
            foreach (var dimension in type.Elements(P + "dimension"))
            {
                PlcOpenDeclarationCodec.Attributes(dimension, "lower", "upper");
                if (dimension.Nodes().Any()) throw new ProjectEditException("graph_contains_unrepresented_data");
            }
            dimensions = type.Elements(P + "dimension").Select(d => new GraphDimension((int)d.Attribute("lower")!, (int)d.Attribute("upper")!)).ToArray();
            var baseType = type.Elements(P + "baseType").Single(); PlcOpenDeclarationCodec.Attributes(baseType);
            type = baseType.Elements().Single();
        }
        PlcOpenDeclarationCodec.Attributes(type, type.Name == P + "derived" ? ["name"] : type.Name == P + "string" || type.Name == P + "wstring" ? ["length"] : []);
        if (type.HasElements) throw new ProjectEditException("graph_contains_unrepresented_data");
        return new(type.Name.LocalName == "derived" ? S(type, "name")! : type.Name.LocalName is "string" or "wstring"
            ? type.Name.LocalName.ToUpperInvariant() : type.Name.LocalName, (int?)type.Attribute("length"), dimensions);
    }
    private static XElement Input(GraphPin pin) => E("connectionPointIn", Position("relPosition"),
        (pin.Connections ?? []).Select(c => E("connection", A("refLocalId", c.SourceId), A("formalParameter", c.SourcePin))));
    private static XElement Output() => E("connectionPointOut", Position("relPosition"));
    private static XElement EncodeNode(GraphNode n)
    {
        var e = E(n.Kind, A("localId", n.Id), A("executionOrderId", n.ExecutionOrder), A("priority", n.Priority), Position("position", n.X, n.Y));
        if (n.Negated) e.Add(A("negated", true));
        if (n.Edge != "none") e.Add(A("edge", n.Edge));
        if (n.Storage != "none") e.Add(A("storage", n.Storage));
        if (n.Kind == "block")
        {
            e.Add(A("typeName", n.TypeName), A("instanceName", n.InstanceName));
            e.Add(E("inputVariables", (n.Inputs ?? []).Select(p => E("variable", A("formalParameter", p.Name), p.Negated ? A("negated", true) : null, Input(p)))),
                E("inOutVariables", (n.InOuts ?? []).Select(p => E("variable", A("formalParameter", p.Name), Input(p), Output()))), E("outputVariables", (n.Outputs ?? []).Select(p => E("variable", A("formalParameter", p.Name), p.Negated ? A("negated", true) : null, Output()))));
            return e;
        }
        foreach (var pin in n.Inputs ?? []) e.Add(Input(pin));
        foreach (var pin in n.Outputs ?? [])
        {
            var port = Output(); if (n.Kind is "step" or "leftPowerRail" || GraphV2Rules.Divergence(n.Kind)) port.Add(A("formalParameter", pin.Name));
            e.Add(port);
        }
        switch (n.Kind)
        {
            case "contact": case "coil": e.Add(E("variable", n.Expression)); break;
            case "inVariable": case "outVariable": e.Add(E("expression", n.Expression)); break;
            case "step": e.Add(A("name", n.Name)); if (n.Initial) e.Add(A("initialStep", true)); break;
            case "jumpStep": e.Add(A("targetName", n.Name)); break;
            case "connector": case "continuation": e.Add(A("name", n.Name)); break;
            case "transition": e.Add(E("condition", E("inline", A("name", "condition_" + n.Id), St(n.Expression!)))); break;
            case "actionBlock": e.Add(PlcOpenAssociationCodec.Encode(n)); break;
        }
        return e;
    }
    public static GraphDocument Decode(string xml)
    {
        _ = ExchangeXml.Parse(xml, ExchangeOperations.MaxInlineXmlBytes);
        _ = ExchangeXml.Inspect("plcopen", xml);
        var root = ExchangeXml.Parse(xml).Root!;
        try
        {
            var pous = root.Element(P + "types")!.Element(P + "pous")!.Elements(P + "pou").ToArray();
            if (pous.Length != 1) throw new ProjectEditException("single_graph_pou_required");
            var pou = pous[0]; var body = pou.Element(P + "body")!.Elements().Single();
            if (body.Name.Namespace != P || body.Name.LocalName is not ("LD" or "FBD" or "SFC")) throw new ProjectEditException("graph_language_outside_profile");
            var declarations = PlcOpenDeclarationCodec.Decode(pou.Element(P + "interface")!);
            var actions = pou.Element(P + "actions")?.Elements(P + "action").Select(a => new GraphAction(S(a, "name")!, a.Element(P + "body")!.Element(P + "ST")!.Element(H + "xhtml")!.Value)).ToArray() ?? [];
            var program = new GraphicalProgram(S(pou, "name")!, S(pou, "pouType")!, body.Name.LocalName, declarations.Variables, body.Elements().Select(DecodeNode).ToArray(), actions,
                pou.Element(P + "interface")!.Element(P + "returnType") is { } returnType ? DecodeType(returnType) : null, VariableGroups: declarations.Groups);
            if (GraphV2Rules.RequiresV2(program) || S(root.Element(P + "fileHeader")!, "productVersion") == "graph-v2") program = program with { Profile = "graph-v2" };
            if (GraphRules.Validate(program) is { } error) throw new ProjectEditException(error);
            // The entire program must be represented. Unknown attributes, addData, additional bodies,
            // declarations or vendor properties cause an explicit refusal to edit; the raw artifact remains available.
            if (Canonical(pou) != Canonical(EncodePou(program))) throw new ProjectEditException("graph_contains_unrepresented_data");
            var allowedRoot = new[] { "fileHeader", "contentHeader", "types", "instances" };
            if (root.Elements().Any(e => e.Name.Namespace != P || !allowedRoot.Contains(e.Name.LocalName))
                || root.Elements().GroupBy(e => e.Name).Any(g => g.Count() > 1)
                || root.Descendants(P + "addData").Any()
                || root.Element(P + "types")!.Elements().Any(e => e.Name != P + "dataTypes" && e.Name != P + "pous")
                || root.Element(P + "types")!.Elements().GroupBy(e => e.Name).Any(g => g.Count() > 1)
                || root.Element(P + "types")!.Element(P + "pous")!.Elements().Any(e => e.Name != P + "pou")
                || root.Element(P + "types")!.Element(P + "dataTypes")?.HasElements == true
                || root.Element(P + "instances")!.Descendants().Any(e => e.Name != P + "configurations")) throw new ProjectEditException("graph_document_contains_other_objects");
            return new(program, SemanticHash(program), program.Profile);
        }
        catch (Exception error) when (error is InvalidOperationException or NullReferenceException or FormatException or OverflowException or ArgumentException)
        { throw new ProjectEditException("graph_contains_unrepresented_data"); }
    }
    private static string? S(XElement e, string name) => (string?)e.Attribute(name);
    private static bool B(XElement e, string name) => (bool?)e.Attribute(name) ?? false;
    private static GraphPin Pin(XElement e, bool input, string name = "", bool negated = false) => new(name,
        input ? e.Elements(P + "connection").Select(c => new GraphConnection((int)c.Attribute("refLocalId")!, S(c, "formalParameter"))).ToArray() : null, negated);
    private static GraphNode DecodeNode(XElement e)
    {
        var kind = e.Name.LocalName; var pos = e.Element(P + "position");
        var associations = kind == "actionBlock" ? PlcOpenAssociationCodec.Decode(e) : null;
        var legacyAction = associations is { Length: 1 } && associations[0].Id == (int)e.Attribute("localId")! && associations[0].ExecutionOrder is null;
        var inputs = kind == "block" ? e.Element(P + "inputVariables")!.Elements(P + "variable").Select(v => Pin(v.Element(P + "connectionPointIn")!, true, S(v, "formalParameter")!, B(v, "negated"))).ToArray()
            : e.Elements(P + "connectionPointIn").Select(p => Pin(p, true)).ToArray();
        var outputs = kind == "block" ? e.Element(P + "outputVariables")!.Elements(P + "variable").Select(v => Pin(v.Element(P + "connectionPointOut")!, false, S(v, "formalParameter")!, B(v, "negated"))).ToArray()
            : e.Elements(P + "connectionPointOut").Select(p => Pin(p, false, S(p, "formalParameter") ?? "")).ToArray();
        return new((int)e.Attribute("localId")!, kind, (decimal?)pos?.Attribute("x") ?? 0, (decimal?)pos?.Attribute("y") ?? 0,
            (string?)e.Element(P + "variable") ?? (string?)e.Element(P + "expression") ?? (string?)e.Element(P + "condition")?.Element(P + "inline")?.Element(P + "ST")?.Element(H + "xhtml"),
            S(e, "typeName"), S(e, "instanceName"), inputs, outputs, B(e, "negated"), S(e, "edge") ?? "none", S(e, "storage") ?? "none",
            (int?)e.Attribute("executionOrderId"), S(e, "name") ?? S(e, "targetName"), B(e, "initialStep"),
            legacyAction ? associations![0].Action : null, legacyAction ? associations![0].Qualifier : "N",
            e.Element(P + "inOutVariables")?.Elements(P + "variable").Any() == true
                ? e.Element(P + "inOutVariables")!.Elements(P + "variable").Select(v => Pin(v.Element(P + "connectionPointIn")!, true, S(v, "formalParameter")!, B(v, "negated"))).ToArray() : null,
            (int?)e.Attribute("priority"), legacyAction ? associations![0].Duration : null, legacyAction ? null : associations);
    }
    public static string SemanticHash(GraphicalProgram p)
    {
        if (GraphRules.Validate(p) is { } error) throw new ProjectEditException(error);
        // Local IDs and pin order are retained. Renumbering/reordering is conservatively rejected,
        // not guessed to be equivalent. Geometry and XML prefix/attribute order are non-semantic.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(EncodePou(p))))).ToLowerInvariant();
    }
    private static string Canonical(XElement source)
    {
        var sb = new StringBuilder();
        void Token(string value) => sb.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        void Visit(XElement e)
        {
            if (e.Name == P + "position" || e.Name == P + "relPosition") return;
            Token(e.Name.ToString());
            foreach (var a in e.Attributes().Where(a => !a.IsNamespaceDeclaration && a.Name != "width" && a.Name != "height").OrderBy(a => a.Name.ToString(), StringComparer.Ordinal))
            {
                if (a.Name.LocalName is "negated" or "initialStep" && a.Value is "false" or "0"
                    || a.Name.LocalName is "edge" or "storage" && a.Value == "none" || a.Name == "qualifier" && a.Value == "N"
                    || e.Name == P + "value" && e.Parent?.Name == P + "arrayValue" && a.Name == "repetitionValue" && a.Value == "1") continue;
                if (GraphDeclarationRules.Scopes.Any(s => e.Name == P + (s + "Vars")) && a.Name.LocalName is "constant" or "retain" or "nonretain"
                    && a.Name.Namespace == XNamespace.None)
                {
                    if (a.Value is "false" or "0") continue;
                    if (a.Value == "1") { Token(a.Name.ToString()); Token("true"); continue; }
                }
                Token(a.Name.ToString()); Token(a.Value);
            }
            Token("attributes-end");
            foreach (var node in e.Nodes())
            {
                if (node is XElement child) Visit(child);
                else if (node is XText t && (!e.HasElements || !string.IsNullOrWhiteSpace(t.Value))) Token(t.Value.Replace("\r\n", "\n", StringComparison.Ordinal));
                else if (node is XComment or XProcessingInstruction) Token(node.ToString());
            }
            Token("element-end");
        }
        Visit(source); return sb.ToString();
    }
}
