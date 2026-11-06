using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Exchange;
using PlcMcp.Services.Contracts.Workers;

namespace PlcMcp.Execution;

/// <summary>Resumable bounded uploads. Offsets are bytes; only a complete, hashed UTF-8 document is accepted.</summary>
public sealed class ExchangeUploads(string directory, Func<string, string, CancellationToken, Task<ExchangeArtifact>> accept,
    int maxBytes = ExchangeOperations.DefaultMaxXmlBytes) : IExchangeUploads
{
    private readonly string root = Path.GetFullPath(directory);
    private readonly SemaphoreSlim gate = new(1, 1);
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private string PathFor(string id, string suffix)
    {
        if (!Digest(id)) throw new ProjectEditException("invalid_upload_id");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, id + suffix);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0
            || File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ProjectEditException("upload_path_not_allowed");
        return path;
    }
    private async Task<UploadSession> Load(string id, CancellationToken token)
    {
        if (File.Exists(PathFor(id, ".expired.json"))) throw new ProjectEditException("upload_expired");
        var path = PathFor(id, ".json");
        if (!File.Exists(path)) throw new ProjectEditException("upload_not_found");
        if (new FileInfo(path).Length > 4096) throw new ProjectEditException("upload_invalid");
        var value = JsonSerializer.Deserialize<UploadSession>(await File.ReadAllTextAsync(path, token), WorkerJson.Options);
        if (value is null || value.UploadId != id || !ExchangeOperations.Format(value.Format) || !Digest(value.Sha256)
            || value.ByteLength is <= 0 || value.ByteLength > maxBytes) throw new ProjectEditException("upload_invalid");
        var data = PathFor(id, ".part");
        var length = File.Exists(data) ? new FileInfo(data).Length : 0;
        if (length > value.ByteLength) throw new ProjectEditException("upload_invalid");
        return value with { NextOffset = value.Artifact is null ? (int)length : value.ByteLength };
    }
    private async Task Save(UploadSession value, CancellationToken token)
    {
        var target = PathFor(value.UploadId, ".json"); var temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, WorkerJson.Options), token); File.Move(temporary, target, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<UploadSession> BeginAsync(string key, string format, int byteLength, string sha256, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Contains('\0') || !ExchangeOperations.Format(format)
            || !Digest(sha256) || byteLength <= 0 || byteLength > maxBytes) throw new ProjectEditException("invalid_upload_request");
        var id = Hash(Encoding.UTF8.GetBytes(key));
        await gate.WaitAsync(token);
        try
        {
            if (File.Exists(PathFor(id, ".expired.json"))) throw new ProjectEditException("upload_expired");
            if (File.Exists(PathFor(id, ".json")))
            {
                var prior = await Load(id, token);
                if (prior.Format != format || prior.ByteLength != byteLength || prior.Sha256 != sha256) throw new ProjectEditException("idempotency_conflict");
                return prior;
            }
            var active = 0;
            foreach (var path in Directory.EnumerateFiles(root, "*.json"))
                if ((await Load(Path.GetFileNameWithoutExtension(path), token)).Artifact is null && ++active >= 32) throw new ProjectEditException("upload_limit_reached");
            var created = new UploadSession(id, format, byteLength, sha256, 0);
            await Save(created, token); return created;
        }
        finally { gate.Release(); }
    }
    public async Task<UploadSession> AppendAsync(string id, int offset, string base64, CancellationToken token)
    {
        if (offset < 0 || base64 is null || base64.Length > 44000) throw new ProjectEditException("invalid_upload_chunk");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { throw new ProjectEditException("invalid_upload_chunk"); }
        if (bytes.Length is < 1 or > 32768) throw new ProjectEditException("invalid_upload_chunk");
        await gate.WaitAsync(token);
        try
        {
            var upload = await Load(id, token);
            if (upload.Artifact is not null) throw new ProjectEditException("upload_already_complete");
            if ((long)offset + bytes.Length > upload.ByteLength) throw new ProjectEditException("upload_size_exceeded");
            await using var file = new FileStream(PathFor(id, ".part"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 32768, true);
            if (offset < file.Length && (long)offset + bytes.Length <= file.Length)
            {
                file.Position = offset; var existing = new byte[bytes.Length]; await file.ReadExactlyAsync(existing, token);
                if (!existing.AsSpan().SequenceEqual(bytes)) throw new ProjectEditException("upload_chunk_conflict");
                return upload;
            }
            if (offset != file.Length) throw new ProjectEditException("upload_offset_conflict");
            file.Position = file.Length; await file.WriteAsync(bytes, token); await file.FlushAsync(token); file.Flush(true);
            return upload with { NextOffset = checked((int)file.Length) };
        }
        finally { gate.Release(); }
    }
    public async Task<UploadSession> CompleteAsync(string id, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var upload = await Load(id, token);
            if (upload.Artifact is not null) return upload;
            if (upload.NextOffset != upload.ByteLength) throw new ProjectEditException("upload_incomplete");
            byte[] bytes;
            await using (var stream = new FileStream(PathFor(id, ".part"), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
            {
                if (stream.Length != upload.ByteLength) throw new ProjectEditException("upload_hash_mismatch");
                bytes = new byte[upload.ByteLength]; await stream.ReadExactlyAsync(bytes, token);
                if (stream.ReadByte() != -1) throw new ProjectEditException("upload_hash_mismatch");
            }
            if (bytes.Length != upload.ByteLength || Hash(bytes) != upload.Sha256) throw new ProjectEditException("upload_hash_mismatch");
            string xml;
            try { xml = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { throw new ProjectEditException("upload_requires_utf8"); }
            var artifact = await accept(upload.Format, xml, token);
            var completed = upload with { Artifact = artifact };
            await Save(completed, token); File.Delete(PathFor(id, ".part")); return completed;
        }
        finally { gate.Release(); }
    }
    public async Task AbortAsync(string id, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { File.Delete(PathFor(id, ".part")); File.Delete(PathFor(id, ".json")); File.Delete(PathFor(id, ".expired.json")); }
        finally { gate.Release(); }
    }
}
