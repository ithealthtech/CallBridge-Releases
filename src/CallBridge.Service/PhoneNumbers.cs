using System.Text.RegularExpressions;

namespace CallBridge.Service;

public static partial class PhoneNumbers
{
    public static string Normalize(string? value, string defaultCountryCode = "1")
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var raw = value.Trim();
        if (raw.Length > 64) return "";
        if (raw.StartsWith('+')) return "+" + NonDigit().Replace(raw[1..], "");
        var digits = NonDigit().Replace(raw, "");
        if (digits.StartsWith("00", StringComparison.Ordinal)) return "+" + digits[2..];
        if (digits.Length == 10) return "+" + defaultCountryCode + digits;
        if (digits.Length == 11 && digits.StartsWith(defaultCountryCode, StringComparison.Ordinal)) return "+" + digits;
        return digits.Length is > 0 and <= 15 ? "+" + digits : "";
    }

    public static IReadOnlyList<string> Variants(string? value)
    {
        var normalized = Normalize(value);
        if (normalized.Length == 0) return [];
        var digits = NonDigit().Replace(normalized, "");
        var variants = new HashSet<string>(StringComparer.Ordinal) { normalized, digits };
        if (digits.Length > 10) variants.Add(digits[^10..]);
        if (digits.Length == 10) variants.Add("1" + digits);
        return [.. variants];
    }

    [GeneratedRegex("[^0-9]")]
    private static partial Regex NonDigit();
}
