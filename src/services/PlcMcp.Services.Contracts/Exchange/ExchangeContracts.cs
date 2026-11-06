using PlcMcp.Core.Workspaces;

namespace PlcMcp.Services.Contracts.Exchange;

public static class ExchangeOperations
{
    public const string Export = "project.export", Import = "project.import", ReadGraph = "pou.graph.read", WriteGraph = "pou.graph.write";
    public static bool IsRead(string op) => op is Export or ReadGraph;
    public static bool IsWrite(string op) => op is Import or WriteGraph;
    public const int MaxXmlBytes = 65536;
    public static bool Format(string? value) => value is "plcopen" or "codesys-native";
}
public sealed record ExchangeQuery(string Format, string[] ObjectIds, bool Recursive = true);
public sealed record ExchangeChange(string ArtifactId, string Format, string Xml, string ConflictPolicy = "reject",
    bool AllowWarnings = false, string? ExpectedGraphSha256 = null);
public sealed record ExchangeArtifact(string ArtifactId, string Sha256, string Format, int ByteLength);
public sealed record ExchangeDocument(ExchangeArtifact Artifact, string Xml);
public sealed record ExchangeChunk(ExchangeArtifact Artifact, int Offset, int NextOffset, bool Complete, string Text);
public sealed record ExchangeInspection(string Format, string RootName, string[] PouNames, string[] Languages,
    bool HasExtensions, string ValidationScope, string[] Limitations);
public sealed record GraphDocument(GraphicalProgram Program, string SemanticSha256, string Profile = "graph-v1");
public sealed record GeneratedGraph(ExchangeArtifact Artifact, GraphDocument Graph);
public interface IExchangeArtifacts
{
    Task<ExchangeArtifact> PutAsync(string format, string xml, CancellationToken token);
    Task<ExchangeDocument> GetAsync(string id, CancellationToken token);
}
