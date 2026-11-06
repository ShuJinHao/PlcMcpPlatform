using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Exchange;
using PlcMcp.Services.Contracts.Workers;

namespace PlcMcp.Execution;

/// <summary>Only two fixed private filenames are allowed; no caller-controlled paths cross the worker boundary.</summary>
public static class WorkerExchangeFiles
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static async Task Write(JsonObject exchange, string directory, string name, int maxBytes, CancellationToken token)
    {
        if (exchange["file"] is not null) throw new ProjectEditException("unexpected_exchange_file");
        if (exchange["xml"] is not JsonValue value || !value.TryGetValue<string>(out var xml)) return;
        var bytes = Utf8.GetBytes(xml);
        if (bytes.Length > maxBytes) throw new ProjectEditException("exchange_size_invalid");
        if (bytes.Length <= ExchangeOperations.MaxInlineXmlBytes) return;
        var path = Path.Combine(directory, name);
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
        { await file.WriteAsync(bytes, token); await file.FlushAsync(token); file.Flush(true); }
        exchange["file"] = JsonSerializer.SerializeToNode(new ExchangeFile(name, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length), WorkerJson.Options);
        exchange["xml"] = null;
    }
    private static async Task Read(JsonObject exchange, string directory, string name, int maxBytes, CancellationToken token)
    {
        if (exchange["file"] is null) return;
        if (exchange["xml"] is not null) throw new ProjectEditException("ambiguous_exchange_transport");
        var reference = exchange["file"]!.Deserialize<ExchangeFile>(WorkerJson.Options)!;
        if (reference.Name != name || reference.ByteLength <= 0 || reference.ByteLength > maxBytes) throw new ProjectEditException("invalid_exchange_file");
        var path = Path.Combine(directory, name); var info = new FileInfo(path);
        if (!info.Exists || info.Length != reference.ByteLength || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new ProjectEditException("invalid_exchange_file");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (stream.Length != reference.ByteLength) throw new ProjectEditException("invalid_exchange_file");
        var bytes = new byte[reference.ByteLength]; await stream.ReadExactlyAsync(bytes, token);
        if (stream.ReadByte() != -1 || Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != reference.Sha256)
            throw new ProjectEditException("exchange_file_integrity_mismatch");
        exchange["xml"] = Utf8.GetString(bytes); exchange.Remove("file");
    }
    public static async Task<WorkerRequest> PrepareRequestAsync(WorkerRequest request, string directory, int maximum, CancellationToken token)
    {
        var payload = JsonNode.Parse(request.Payload.GetRawText())!.AsObject();
        if (payload["edit"]?["exchange"] is JsonObject exchange) await Write(exchange, directory, "exchange-input.xml", maximum, token);
        return request with { Payload = JsonSerializer.SerializeToElement(payload, WorkerJson.Options) };
    }
    public static async Task<WorkerRequest> ReadRequestAsync(WorkerRequest request, string directory, int maximum, CancellationToken token)
    {
        var payload = JsonNode.Parse(request.Payload.GetRawText())!.AsObject();
        if (payload["edit"]?["exchange"] is JsonObject exchange) await Read(exchange, directory, "exchange-input.xml", maximum, token);
        return request with { Payload = JsonSerializer.SerializeToElement(payload, WorkerJson.Options) };
    }
    public static async Task<WorkerResponse> PrepareResponseAsync(WorkerResponse response, string directory, int maximum, CancellationToken token)
    {
        if (response.Payload is not { ValueKind: JsonValueKind.Object } data) return response;
        var payload = JsonNode.Parse(data.GetRawText())!.AsObject();
        if (payload["exchange"] is JsonObject exchange) await Write(exchange, directory, "exchange-output.xml", maximum, token);
        return response with { Payload = JsonSerializer.SerializeToElement(payload, WorkerJson.Options) };
    }
    public static async Task<WorkerResponse> ReadResponseAsync(WorkerResponse response, string directory, int maximum, CancellationToken token)
    {
        if (response.Payload is not { ValueKind: JsonValueKind.Object } data) return response;
        var payload = JsonNode.Parse(data.GetRawText())!.AsObject();
        if (payload["exchange"] is JsonObject exchange) await Read(exchange, directory, "exchange-output.xml", maximum, token);
        return response with { Payload = JsonSerializer.SerializeToElement(payload, WorkerJson.Options) };
    }
}
