using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using PlcMcp.Services.Contracts.Delivery;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Workers;

namespace PlcMcp.Execution;

public sealed class DeliveryArtifacts(string directory) : IDeliveryArtifacts
{
    private const long Maximum = 272L * 1024 * 1024;
    private readonly string root = Path.GetFullPath(directory);
    private void GuardRoot()
    {
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new ProjectEditException("delivery_path_not_allowed");
    }
    private string PathFor(string id, string extension)
    {
        if (id is not { Length: 64 } || !id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) throw new ProjectEditException("invalid_delivery_id");
        GuardRoot(); var path = Path.Combine(root, id + extension);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ProjectEditException("delivery_path_not_allowed");
        return path;
    }
    public async Task<DeliveryArtifact> CreateAsync(ProjectSnapshot source, DeliveryManifest manifest, JsonElement compilation, JsonElement tasks, CancellationToken token)
    {
        GuardRoot(); var temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, true))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    long total = 0;
                    foreach (var stamp in source.Files)
                    {
                        if (Path.IsPathRooted(stamp.Path) || stamp.Path.Contains('\\') || stamp.Path.Contains(':') || stamp.Path.Split('/').Any(p => p is "" or "." or "..")) throw new ProjectEditException("path_not_allowed");
                        if ((total += stamp.Length) > 256L * 1024 * 1024 || stamp.Length < 0) throw new ProjectEditException("project_too_large");
                        var current = source.Directory;
                        foreach (var part in stamp.Path.Split('/'))
                        { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ProjectEditException("path_not_allowed"); current = Path.Combine(current, part); }
                        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ProjectEditException("path_not_allowed");
                        await using var input = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                        if (input.Length != stamp.Length) throw new ProjectEditException("workspace_revision_conflict");
                        var entry = zip.CreateEntry("project/" + stamp.Path, CompressionLevel.Optimal); entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        await using var target = entry.Open(); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var buffer = new byte[65536]; long copied = 0; int read;
                        while ((read = await input.ReadAsync(buffer, token)) > 0)
                        {
                            copied += read; if (copied > stamp.Length) throw new ProjectEditException("workspace_revision_conflict");
                            hash.AppendData(buffer, 0, read); await target.WriteAsync(buffer.AsMemory(0, read), token);
                        }
                        if (copied != stamp.Length || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != stamp.Sha256) throw new ProjectEditException("workspace_revision_conflict");
                    }
                    async Task Add<T>(string name, T value)
                    {
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, WorkerJson.Options);
                        if (bytes.Length > WorkerJson.MaxResponseBytes) throw new ProjectEditException("delivery_report_too_large");
                        var entry = zip.CreateEntry(name); entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        await using var stream = entry.Open(); await stream.WriteAsync(bytes, token);
                    }
                    await Add("manifest.json", manifest); await Add("reports/compile.json", compilation); await Add("reports/tasks.json", tasks);
                }
                await output.FlushAsync(token); output.Flush(true);
            }
            await using var verify = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (verify.Length > Maximum) throw new ProjectEditException("delivery_too_large");
            var sha = Convert.ToHexString(await SHA256.HashDataAsync(verify, token)).ToLowerInvariant(); var length = verify.Length;
            await verify.DisposeAsync();
            var descriptor = new DeliveryArtifact(sha, sha, length, manifest.WorkspaceName, manifest.Revision, manifest.ProjectSha256,
                manifest.ExecutionMode, manifest.EvidenceKind, false, source.Files.Length);
            var targetPath = PathFor(sha, ".zip");
            try { File.Move(temporary, targetPath); }
            catch (IOException) when (File.Exists(targetPath))
            {
                await using var prior = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (prior.Length != length || Convert.ToHexString(await SHA256.HashDataAsync(prior, token)).ToLowerInvariant() != sha) throw new ProjectEditException("delivery_integrity_mismatch");
            }
            var metadata = PathFor(sha, ".json"); var metadataTemp = metadata + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllTextAsync(metadataTemp, JsonSerializer.Serialize(descriptor, WorkerJson.Options), token); File.Move(metadataTemp, metadata, true); }
            finally { if (File.Exists(metadataTemp)) File.Delete(metadataTemp); }
            return descriptor;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<DeliveryChunk> ReadAsync(string id, long offset, int maxBytes, CancellationToken token)
    {
        var metadata = PathFor(id, ".json");
        if (!File.Exists(metadata) || new FileInfo(metadata).Length > 4096) throw new ProjectEditException("delivery_not_found");
        var artifact = JsonSerializer.Deserialize<DeliveryArtifact>(await File.ReadAllTextAsync(metadata, token), WorkerJson.Options);
        if (artifact is null || artifact.ArtifactId != id || artifact.Sha256 != id || artifact.ByteLength is <= 0 or > Maximum) throw new ProjectEditException("delivery_invalid");
        if (offset < 0 || offset > artifact.ByteLength || maxBytes is < 1 or > 32768) throw new ProjectEditException("invalid_delivery_range");
        await using var file = new FileStream(PathFor(id, ".zip"), FileMode.Open, FileAccess.Read, FileShare.Read, 32768, true);
        if (file.Length != artifact.ByteLength) throw new ProjectEditException("delivery_integrity_mismatch");
        file.Position = offset; var bytes = new byte[(int)Math.Min(maxBytes, artifact.ByteLength - offset)];
        await file.ReadExactlyAsync(bytes, token); var next = offset + bytes.Length;
        return new(artifact, offset, next, next == artifact.ByteLength, Convert.ToBase64String(bytes), Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }
}
