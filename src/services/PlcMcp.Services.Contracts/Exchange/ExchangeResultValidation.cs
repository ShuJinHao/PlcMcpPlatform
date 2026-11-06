using System.Text.Json;
using PlcMcp.Services.Contracts.Execution;

namespace PlcMcp.Services.Contracts.Exchange;

public static class ExchangeResultValidation
{
    public static bool IsValid(string op, EngineeringInput input, JsonElement payload)
    {
        var data = payload.GetProperty("exchange");
        var expectedFormat = op == ExchangeOperations.Export ? input.Exchange!.Format : op == ExchangeOperations.Import ? input.Edit!.Exchange!.Format : "plcopen";
        if (data.GetProperty("format").GetString() != expectedFormat || data.GetProperty("report").ValueKind != JsonValueKind.Array
            || data.GetProperty("report").GetArrayLength() > 256 || data.GetProperty("objectIds").ValueKind != JsonValueKind.Array
            || data.GetProperty("objectIds").GetArrayLength() > 2000) return false;
        foreach (var id in data.GetProperty("objectIds").EnumerateArray())
            if (!Guid.TryParse(id.GetString(), out var value) || value == Guid.Empty) return false;
        foreach (var entry in data.GetProperty("report").EnumerateArray())
            if (entry.GetProperty("severity").GetString() is not ("information" or "warning")
                || entry.GetProperty("message").ValueKind != JsonValueKind.String) return false;
        if (ExchangeOperations.IsWrite(op) && (data.GetProperty("sourceArtifactId").GetString() != input.Edit!.Exchange!.ArtifactId
            || !data.GetProperty("reexportVerified").GetBoolean())) return false;
        if (op == ExchangeOperations.ReadGraph && (data.GetProperty("objectIds").GetArrayLength() != 1
            || data.GetProperty("objectIds")[0].GetString() != input.ObjectId)) return false;
        if (data.TryGetProperty("xml", out var xml)) return xml.ValueKind == JsonValueKind.String
            && System.Text.Encoding.UTF8.GetByteCount(xml.GetString()!) is > 0 and <= ExchangeOperations.MaxXmlBytes;
        var artifact = data.GetProperty("artifact");
        return artifact.GetProperty("format").GetString() == expectedFormat && artifact.GetProperty("artifactId").GetString() is { Length: 64 } aid
            && aid.All(Uri.IsHexDigit) && artifact.GetProperty("byteLength").GetInt32() is > 0 and <= ExchangeOperations.MaxXmlBytes;
    }
}
