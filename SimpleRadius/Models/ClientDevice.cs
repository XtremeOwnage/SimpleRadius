using System.ComponentModel.DataAnnotations;

namespace SimpleRadius.Models;

/// <summary>
/// A device the server authenticates. <see cref="Name"/> is the identity the NAS sends — for UniFi MAC
/// authentication that is the calling MAC, normalised to lower-case colon form.
/// </summary>
public class ClientDevice
{
    public int Id { get; set; }

    [Required]
    [StringLength(128)]
    [Display(Name = "Identity (MAC or user name)")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Friendly name shown in the UI, e.g. "Office Lamp". The column is still "Description" so existing
    /// data is preserved; only the label changed. <see cref="Name"/> remains the RADIUS identity.
    /// </summary>
    [StringLength(256)]
    [Display(Name = "Name")]
    public string? Description { get; set; }

    /// <summary>Free-text operator notes, shown when editing the client.</summary>
    [StringLength(2000)]
    [Display(Name = "Notes")]
    public string? Notes { get; set; }

    [Required]
    [Display(Name = "VLAN")]
    public int VlanDefinitionId { get; set; }

    public VlanDefinition? VlanDefinition { get; set; }

    /// <summary>Cleared to deny access without deleting the device's history.</summary>
    [Display(Name = "Enabled")]
    public bool IsEnabled { get; set; } = true;

    /// <summary>True when the device was created by the default-VLAN fallback rather than by an operator.</summary>
    [Display(Name = "Auto-created")]
    public bool IsAutoCreated { get; set; }

    /// <summary>Address of the NAS that last authenticated this device.</summary>
    [StringLength(45)]
    [Display(Name = "Last NAS")]
    public string? LastNasIpAddress { get; set; }

    [Display(Name = "Last seen")]
    public DateTime? LastSeenUtc { get; set; }

    [Display(Name = "Successful authentications")]
    public int AuthenticationCount { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
