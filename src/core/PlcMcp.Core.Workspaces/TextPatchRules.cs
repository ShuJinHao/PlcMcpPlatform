using System.Security.Cryptography;
using System.Text;

namespace PlcMcp.Core.Workspaces;

/// <summary>Exact, unique replacement bound to a complete normalized source section.</summary>
public static class TextPatchRules
{
    public static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(text)))).ToLowerInvariant();
    public static (string? Source, string? Error) Apply(string source, string expectedHash, string oldText, string newText)
    {
        source = Normalize(source); oldText = Normalize(oldText); newText = Normalize(newText);
        if (Hash(source) != expectedHash) return (null, "source_hash_conflict");
        if (oldText.Length == 0) return (null, "patch_old_text_required");
        var at = source.IndexOf(oldText, StringComparison.Ordinal);
        if (at < 0) return (null, "patch_old_text_missing");
        if (source.IndexOf(oldText, at + 1, StringComparison.Ordinal) >= 0) return (null, "patch_ambiguous");
        var result = source[..at] + newText + source[(at + oldText.Length)..];
        return result.Length > 65536 || result.Contains('\0') ? (null, "invalid_source") : (result, null);
    }
}
