namespace SimpleRadius.Models;

/// <summary>
/// How MAC addresses are written in the admin UI. Inbound matching never depends on this — a request is
/// normalised before lookup, so a NAS may send any of these spellings regardless of what is selected here.
/// The names mirror the MAC address format list a UniFi or MikroTik controller offers.
/// </summary>
public enum MacAddressFormat
{
    /// <summary>aa:bb:cc:dd:ee:ff</summary>
    ColonLower,

    /// <summary>AA:BB:CC:DD:EE:FF</summary>
    ColonUpper,

    /// <summary>aa-bb-cc-dd-ee-ff</summary>
    HyphenLower,

    /// <summary>AA-BB-CC-DD-EE-FF</summary>
    HyphenUpper,

    /// <summary>aabbccddeeff</summary>
    PlainLower,

    /// <summary>AABBCCDDEEFF</summary>
    PlainUpper
}
