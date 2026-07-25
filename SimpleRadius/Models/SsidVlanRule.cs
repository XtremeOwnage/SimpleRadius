using System.ComponentModel.DataAnnotations;

namespace SimpleRadius.Models;

/// <summary>
/// Maps a wireless SSID to the default VLAN new devices on that SSID are placed on. Applied only when a
/// device is unknown and being auto-created — a device with its own VLAN assignment keeps it. When no rule
/// matches the SSID (or the request carried none), the global default VLAN is used.
/// </summary>
public class SsidVlanRule
{
    public int Id { get; set; }

    /// <summary>Matched exactly against the SSID from Called-Station-Id. SSIDs are case-sensitive.</summary>
    [Required]
    [StringLength(64)]
    [Display(Name = "SSID")]
    public string Ssid { get; set; } = string.Empty;

    [Required]
    [Display(Name = "Default VLAN")]
    public int VlanDefinitionId { get; set; }

    public VlanDefinition? VlanDefinition { get; set; }

    [StringLength(500)]
    [Display(Name = "Notes")]
    public string? Notes { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
