using PlcMcp.Core.Workspaces;

namespace PlcMcp.Services.Contracts.Exchange;

public static class ExchangeOperations
{
    public const string Export = "project.export", Import = "project.import", ReadGraph = "pou.graph.read", WriteGraph = "pou.graph.write";
    public static bool IsRead(string op) => op is Export or ReadGraph;
    public static bool IsWrite(string op) => op is Import or WriteGraph;
    public const int MaxInlineXmlBytes = 65536, DefaultMaxXmlBytes = 8 * 1024 * 1024, MaxXmlBytes = 64 * 1024 * 1024;
    public static bool Format(string? value) => value is "plcopen" or "codesys-native";
}
public sealed record ExchangeQuery(string Format, string[] ObjectIds, bool Recursive = true);
public sealed record ExchangeChange(string ArtifactId, string Format, string? Xml = null, string ConflictPolicy = "reject",
    bool AllowWarnings = false, string? ExpectedGraphSha256 = null, ExchangeFile? File = null);
public sealed record ExchangeFile(string Name, string Sha256, int ByteLength);
public sealed record ExchangeArtifact(string ArtifactId, string Sha256, string Format, int ByteLength);
public sealed record ExchangeDocument(ExchangeArtifact Artifact, string Xml);
public sealed record ExchangeChunk(ExchangeArtifact Artifact, int Offset, int NextOffset, bool Complete, string Text);
public sealed record ExchangeInspection(string Format, string RootName, string[] PouNames, string[] Languages,
    bool HasExtensions, string ValidationScope, string[] Limitations);
public sealed record GraphDocument(GraphicalProgram Program, string SemanticSha256, string Profile = "graph-v1");
public sealed record GeneratedGraph(ExchangeArtifact Artifact, GraphDocument Graph);
// Vendor/IDE restrictions are evaluated separately from the portable TC6 representation.
public interface IPlcGraphPolicy
{
    string? Validate(GraphicalProgram program);
}
public sealed record UploadSession(string UploadId, string Format, int ByteLength, string Sha256, int NextOffset, ExchangeArtifact? Artifact = null);
public interface IExchangeUploads
{
    Task<UploadSession> BeginAsync(string key, string format, int byteLength, string sha256, CancellationToken token);
    Task<UploadSession> AppendAsync(string id, int offset, string base64, CancellationToken token);
    Task<UploadSession> CompleteAsync(string id, CancellationToken token);
    Task AbortAsync(string id, CancellationToken token);
}
public interface IExchangeArtifacts
{
    Task<ExchangeArtifact> PutAsync(string format, string xml, CancellationToken token);
    Task<ExchangeDocument> GetAsync(string id, CancellationToken token);
}
