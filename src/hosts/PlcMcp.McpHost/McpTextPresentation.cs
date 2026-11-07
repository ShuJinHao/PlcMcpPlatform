using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ModelContextProtocol.Protocol;
using PlcMcp.Services.Contracts.Workers;

namespace PlcMcp.McpHost;

/// <summary>Readable model-facing JSON only. Worker payloads, stored bytes and structured results are unchanged.</summary>
public static class McpTextPresentation
{
    private static readonly JsonSerializerOptions TextOptions = new(WorkerJson.Options)
    {
        // Keep HTML-sensitive characters escaped; do not use UnsafeRelaxedJsonEscaping.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public static CallToolResult Present(CallToolResult reply)
    {
        // Current tools return one text mirror of structured JSON. Do not discard
        // a future rich/multipart result or reinterpret text-only error messages.
        if (reply.StructuredContent is { } content && reply.Content.Count == 1 && reply.Content[0] is TextContentBlock)
            reply.Content = [new TextContentBlock { Text = JsonSerializer.Serialize(content, TextOptions) }];
        return reply;
    }
}
