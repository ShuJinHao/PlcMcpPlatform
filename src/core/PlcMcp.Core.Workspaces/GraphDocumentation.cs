using System.Xml;
using System.Xml.Linq;

namespace PlcMcp.Core.Workspaces;

/// <summary>XHTML remains bounded data. No stylesheet, link, script or entity is executed or fetched.</summary>
public static class GraphDocumentation
{
    public const int MaxCharacters = 16384;
    public const int MaxDepth = 16;
    public const int MaxNodes = 1024;
    private static readonly XNamespace H = "http://www.w3.org/1999/xhtml";
    public static bool Valid(string? plain, string? xhtml)
    {
        if (!GraphDeclarationRules.DocumentationValid(plain) || plain is not null && xhtml is not null) return false;
        if (xhtml is null) return true;
        try { _ = Parse(xhtml); return true; }
        catch (Exception e) when (e is XmlException or ArgumentException) { return false; }
    }
    public static XElement Parse(string value)
    {
        if (value.Length is 0 or > MaxCharacters) throw new ArgumentException("Invalid XHTML size.");
        using var reader = XmlReader.Create(new StringReader(value), new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxCharacters,
            IgnoreWhitespace = false, IgnoreComments = false, IgnoreProcessingInstructions = false });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (document.Root is not { } root || document.Nodes().Any(n => n is not XElement && n is not XText))
            throw new ArgumentException("Unsupported XHTML document node.");
        var nodes = 0;
        void Visit(XElement element, int depth)
        {
            if (depth > MaxDepth || ++nodes > MaxNodes || element.Name.Namespace != H)
                throw new ArgumentException("Unsupported XHTML shape.");
            foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
                if (attribute.Name.Namespace != XNamespace.None && attribute.Name != XNamespace.Xml + "lang" && attribute.Name != XNamespace.Xml + "space")
                    throw new ArgumentException("Unsupported XHTML attribute namespace.");
            foreach (var node in element.Nodes())
            {
                if (node is XElement child) Visit(child, depth + 1);
                else if (node is XText) { if (++nodes > MaxNodes) throw new ArgumentException("Too many XHTML nodes."); }
                else throw new ArgumentException("Unsupported XHTML node.");
            }
        }
        Visit(root, 1); return root;
    }
}
