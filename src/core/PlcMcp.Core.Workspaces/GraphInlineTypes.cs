using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace PlcMcp.Core.Workspaces;

/// <summary>TC6 inline enumerations and integer subranges. Values retain their exact decimal spelling.</summary>
public static class GraphInlineTypes
{
    public static bool Unsigned(string type) => type is "USINT" or "UINT" or "UDINT" or "ULINT" or "BYTE" or "WORD" or "DWORD" or "LWORD";
    public static (BigInteger Min, BigInteger Max)? Bounds(string type) => type switch
    {
        "SINT" => (sbyte.MinValue, sbyte.MaxValue), "INT" => (short.MinValue, short.MaxValue),
        "DINT" => (int.MinValue, int.MaxValue), "LINT" => (long.MinValue, long.MaxValue),
        "USINT" or "BYTE" => (byte.MinValue, byte.MaxValue), "UINT" or "WORD" => (ushort.MinValue, ushort.MaxValue),
        "UDINT" or "DWORD" => (uint.MinValue, uint.MaxValue), "ULINT" or "LWORD" => (ulong.MinValue, ulong.MaxValue), _ => null
    };
    private static bool Number(string? text, out BigInteger value)
    {
        value = 0;
        return text is { Length: > 0 and <= 21 } && Regex.IsMatch(text, @"\A-?(?:0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant)
            && BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }
    public static bool Valid(GraphDataType type)
    {
        if (type.EnumValues is null && type.Subrange is null) return true;
        if (type.StringLength is not null || type.EnumValues is not null && type.Subrange is not null) return false;
        if (type.EnumValues is { } values)
        {
            var bounds = Bounds(type.Type == "ENUM" ? "INT" : type.Type);
            if (bounds is null || values.Length is < 1 or > 256 || values.Any(v => v is null || !GraphRules.Identifier(v.Name))
                || values.Select(v => v.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length) return false;
            BigInteger next = 0;
            foreach (var entry in values)
            {
                var current = next;
                if (entry.Value is not null && !Number(entry.Value, out current)) return false;
                if (current < bounds.Value.Min || current > bounds.Value.Max) return false;
                next = current + 1;
            }
            return true;
        }
        return Bounds(type.Type) is { } limits && Number(type.Subrange!.Lower, out var lower) && Number(type.Subrange.Upper, out var upper)
            && lower >= limits.Min && upper <= limits.Max && lower <= upper;
    }
}
