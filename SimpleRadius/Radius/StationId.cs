using System.Text.RegularExpressions;

namespace SimpleRadius.Radius;

/// <summary>
/// Helpers for the station-identity attributes a NAS reports. Called-Station-Id, for wireless, follows
/// RFC 3580: the AP's MAC in hyphenated form, optionally followed by ":SSID" — e.g.
/// "24-A4-3C-00-11-22:IoT". This pulls the SSID out and names the NAS-Port-Type code.
/// </summary>
public static partial class StationId
{
    /// <summary>
    /// Returns the SSID from a Called-Station-Id, or null when there is none (a wired port, or a bare MAC).
    /// The SSID is everything after the first colon; a value that is just a colon-separated MAC has none.
    /// </summary>
    public static string? ExtractSsid(string? calledStationId)
    {
        if (string.IsNullOrWhiteSpace(calledStationId))
        {
            return null;
        }

        var value = calledStationId.Trim();

        // A plain colon-separated MAC (aa:bb:cc:dd:ee:ff) carries no SSID; the colons are separators.
        if (ColonMac().IsMatch(value))
        {
            return null;
        }

        var colon = value.IndexOf(':');
        if (colon < 0 || colon == value.Length - 1)
        {
            return null;
        }

        var ssid = value[(colon + 1)..].Trim();
        return ssid.Length == 0 ? null : ssid;
    }

    /// <summary>Names the common NAS-Port-Type (RFC 2865) codes; unknown values fall back to "Type N".</summary>
    public static string? DescribeNasPortType(uint? value) => value switch
    {
        null => null,
        0 => "Async",
        5 => "Virtual",
        15 => "Ethernet",
        18 => "Wireless (other)",
        19 => "Wireless 802.11",
        _ => $"Type {value}"
    };

    [GeneratedRegex("^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$")]
    private static partial Regex ColonMac();
}
