using System.Security.Cryptography;
using System.Text.Json;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Incremental;
using PlcMcp.Services.Contracts.Workers;

namespace PlcMcp.EngineeringService.Incremental;

// A query-bound continuation, not authorization. Every page still verifies the workspace and immutable version.
public static class EngineeringCursor
{
    private sealed record Continuation(string QuerySha256, int Offset);
    private static string Hash(string head, IncrementalQuery query) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { head, query = query with { Offset = 0, Cursor = null } }, WorkerJson.Options)));
    public static string Encode(string head, IncrementalQuery query, int offset) => Convert.ToBase64String(
        JsonSerializer.SerializeToUtf8Bytes(new Continuation(Hash(head, query), offset), WorkerJson.Options)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static IncrementalQuery Resume(string head, IncrementalQuery query)
    {
        if (query.Cursor is null) return query;
        try
        {
            if (query.Offset != 0 || query.Cursor.Length > 1024) throw new ProjectEditException("invalid_engineering_cursor");
            var raw = query.Cursor.Replace('-', '+').Replace('_', '/');
            var value = JsonSerializer.Deserialize<Continuation>(Convert.FromBase64String(raw.PadRight((raw.Length + 3) / 4 * 4, '=')), WorkerJson.Options);
            if (value is null || value.Offset is < 1 or > 1000000 || value.QuerySha256 != Hash(head, query)) throw new ProjectEditException("engineering_cursor_query_mismatch");
            return query with { Offset = value.Offset, Cursor = null };
        }
        catch (Exception e) when (e is FormatException or JsonException) { throw new ProjectEditException("invalid_engineering_cursor"); }
    }
}
