using System.ComponentModel.DataAnnotations;

namespace SimpleRadius.Models;

/// <summary>
/// One accounting session as reported by a NAS. A session is created on Acct-Status-Type=Start, refreshed
/// by interim updates, and closed on Stop; <see cref="IsActive"/> distinguishes live sessions from history.
/// </summary>
public class AccountingSession
{
    public int Id { get; set; }

    /// <summary>Acct-Session-Id. Only unique per NAS, so it is keyed together with <see cref="NasIpAddress"/>.</summary>
    [Required]
    [StringLength(128)]
    [Display(Name = "Session ID")]
    public string SessionId { get; set; } = string.Empty;

    [Required]
    [StringLength(128)]
    [Display(Name = "Client")]
    public string ClientName { get; set; } = string.Empty;

    public int? ClientDeviceId { get; set; }

    public ClientDevice? ClientDevice { get; set; }

    /// <summary>Calling-Station-Id, the MAC of the connecting device as reported by the NAS.</summary>
    [StringLength(128)]
    [Display(Name = "Calling station")]
    public string? CallingStationId { get; set; }

    /// <summary>Called-Station-Id, typically the AP's MAC plus SSID, or the NAS port MAC.</summary>
    [StringLength(128)]
    [Display(Name = "Called station")]
    public string? CalledStationId { get; set; }

    /// <summary>SSID parsed from Called-Station-Id, when the connection is wireless.</summary>
    [StringLength(64)]
    [Display(Name = "SSID")]
    public string? Ssid { get; set; }

    /// <summary>NAS-Identifier — the name the reporting NAS calls itself (often a hostname or AP name).</summary>
    [StringLength(128)]
    [Display(Name = "NAS identifier")]
    public string? NasIdentifier { get; set; }

    /// <summary>NAS-Port-Type as a readable name, e.g. "Wireless 802.11" or "Ethernet".</summary>
    [StringLength(32)]
    [Display(Name = "Port type")]
    public string? NasPortType { get; set; }

    [Required]
    [StringLength(128)]
    [Display(Name = "NAS")]
    public string NasName { get; set; } = string.Empty;

    [Required]
    [StringLength(45)]
    [Display(Name = "NAS IP")]
    public string NasIpAddress { get; set; } = string.Empty;

    [Display(Name = "VLAN")]
    public int? VlanId { get; set; }

    [Display(Name = "Status")]
    public int AcctStatusType { get; set; }

    [Display(Name = "Active")]
    public bool IsActive { get; set; }

    [Display(Name = "Bytes in")]
    public long BytesIn { get; set; }

    [Display(Name = "Bytes out")]
    public long BytesOut { get; set; }

    [Display(Name = "Packets in")]
    public long PacketsIn { get; set; }

    [Display(Name = "Packets out")]
    public long PacketsOut { get; set; }

    [Display(Name = "Session time")]
    public long SessionSeconds { get; set; }

    [Display(Name = "Terminate cause")]
    public uint? TerminateCause { get; set; }

    [Display(Name = "Started")]
    public DateTime StartTime { get; set; } = DateTime.UtcNow;

    [Display(Name = "Last update")]
    public DateTime LastUpdateTime { get; set; } = DateTime.UtcNow;

    [Display(Name = "Stopped")]
    public DateTime? StopTime { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
