using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Exchange;
using PlcMcp.Services.Contracts.Workers;

namespace PlcMcp.Execution;

/// <summary>Immutable content-addressed exchange documents, separate from managed project files.</summary>
public sealed class ExchangeArtifacts(string directory) : IExchangeArtifacts
{
    private readonly string root = Path.GetFullPath(directory);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static ExchangeArtifact Describe(string format, string xml) => new(Hash(format + "\0" + xml), Hash(xml), format, Encoding.UTF8.GetByteCount(xml));
    private void CheckRoot()
    {
        Directory.CreateDirectory(root);
        // The configured storage root is trusted, like WorkspaceFiles. System ancestors
        // may be aliases (e.g. macOS /var -> /private/var); the root and artifact may not be links.
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new ProjectEditException("artifact_path_not_allowed");
    }
    public async Task<ExchangeArtifact> PutAsync(string format, string xml, CancellationToken token)
    {
        if (!ExchangeOperations.Format(format) || string.IsNullOrWhiteSpace(xml) || Encoding.UTF8.GetByteCount(xml) > ExchangeOperations.MaxXmlBytes)
            throw new ProjectEditException("exchange_size_or_format_invalid");
        CheckRoot(); var descriptor = Describe(format, xml); var target = Path.Combine(root, descriptor.ArtifactId + ".json");
        var temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new ExchangeDocument(descriptor, xml), WorkerJson.Options), token);
            try { File.Move(temporary, target); }
            catch (IOException) when (File.Exists(target)) { }
            var stored = await GetAsync(descriptor.ArtifactId, token);
            return stored.Artifact;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<ExchangeDocument> GetAsync(string id, CancellationToken token)
    {
        if (id is not { Length: 64 } || !id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) throw new ProjectEditException("invalid_artifact_id");
        CheckRoot(); var path = Path.Combine(root, id + ".json"); var info = new FileInfo(path);
        if (!info.Exists) throw new ProjectEditException("artifact_not_found");
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > ExchangeOperations.MaxXmlBytes * 7L) throw new ProjectEditException("artifact_invalid");
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, token);
            var value = JsonSerializer.Deserialize<ExchangeDocument>(bytes, WorkerJson.Options);
            if (stream.ReadByte() != -1 || value is null || value.Artifact is null || value.Xml is null
                || !ExchangeOperations.Format(value.Artifact.Format) || Encoding.UTF8.GetByteCount(value.Xml) > ExchangeOperations.MaxXmlBytes
                || Describe(value.Artifact.Format, value.Xml) != value.Artifact || value.Artifact.ArtifactId != id) throw new ProjectEditException("artifact_integrity_mismatch");
            return value;
        }
        catch (JsonException) { throw new ProjectEditException("artifact_invalid"); }
    }
}
