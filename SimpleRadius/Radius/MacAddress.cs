using System.Text;
using SimpleRadius.Models;

namespace SimpleRadius.Radius;

/// <summary>
/// Normalises the many MAC address spellings a NAS may send. UniFi presents the calling MAC as the
/// RADIUS user name, but the separator and casing vary by firmware ("AABBCCDDEEFF", "aa-bb-cc-dd-ee-ff",
/// "AA:BB:CC:DD:EE:FF"), so identities are canonicalised before they are stored or looked up.
/// </summary>
public static class MacAddress
{
    /// <summary>
    /// Returns the lower-case colon-separated form of <paramref name="value"/> when it is a MAC address,
    /// otherwise returns the trimmed input unchanged so non-MAC user names still work.
    /// </summary>
    public static string Normalize(string value)
    {
        var trimmed = value.Trim();
        return TryNormalize(trimmed, out var normalized) ? normalized : trimmed;
    }

    public static bool TryNormalize(string value, out string normalized)
    {
        normalized = string.Empty;

        Span<char> digits = stackalloc char[12];
        var count = 0;
        foreach (var c in value)
        {
            if (c is '-' or ':' or '.' or ' ')
            {
                continue;
            }

            if (!Uri.IsHexDigit(c) || count == digits.Length)
            {
                return false;
            }

            digits[count++] = char.ToLowerInvariant(c);
        }

        if (count != digits.Length)
        {
            return false;
        }

        var builder = new StringBuilder(17);
        for (var i = 0; i < digits.Length; i += 2)
        {
            if (i > 0)
            {
                builder.Append(':');
            }

            builder.Append(digits[i]).Append(digits[i + 1]);
        }

        normalized = builder.ToString();
        return true;
    }

    /// <summary>
    /// Rewrites a stored identity in the operator's preferred spelling for display. Values that are not MAC
    /// addresses — ordinary user names — are returned unchanged.
    /// </summary>
    public static string Format(string value, MacAddressFormat format)
    {
        if (!TryNormalize(value, out var normalized))
        {
            return value;
        }

        var digits = normalized.Replace(":", string.Empty);
        var separator = format switch
        {
            MacAddressFormat.ColonLower or MacAddressFormat.ColonUpper => ":",
            MacAddressFormat.HyphenLower or MacAddressFormat.HyphenUpper => "-",
            _ => string.Empty
        };

        var upper = format is MacAddressFormat.ColonUpper or MacAddressFormat.HyphenUpper or MacAddressFormat.PlainUpper;
        if (separator.Length == 0)
        {
            return upper ? digits.ToUpperInvariant() : digits;
        }

        var builder = new StringBuilder(17);
        for (var i = 0; i < digits.Length; i += 2)
        {
            if (i > 0)
            {
                builder.Append(separator);
            }

            builder.Append(digits[i]).Append(digits[i + 1]);
        }

        var result = builder.ToString();
        return upper ? result.ToUpperInvariant() : result;
    }

    /// <summary>An example of each format, for the settings page.</summary>
    public static string Sample(MacAddressFormat format) => Format("aa:bb:cc:dd:ee:ff", format);
}
