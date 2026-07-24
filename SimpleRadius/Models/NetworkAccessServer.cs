using System.ComponentModel.DataAnnotations;

namespace SimpleRadius.Models;

/// <summary>A router, gateway or access point allowed to send RADIUS requests to this server.</summary>
public class NetworkAccessServer
{
    public int Id { get; set; }

    [Required]
    [StringLength(128)]
    [Display(Name = "Name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Source address requests must arrive from; this is how a NAS is identified.</summary>
    [Required]
    [StringLength(45)]
    [Display(Name = "IP address")]
    public string IpAddress { get; set; } = string.Empty;

    [Required]
    [StringLength(128)]
    [Display(Name = "Shared secret")]
    public string SharedSecret { get; set; } = string.Empty;

    [Display(Name = "Store accounting")]
    public bool AccountingEnabled { get; set; } = true;

    /// <summary>Cleared to stop answering this NAS without deleting its configuration.</summary>
    [Display(Name = "Enabled")]
    public bool IsEnabled { get; set; } = true;

    [Display(Name = "Auto-registered")]
    public bool IsAutoRegistered { get; set; }

    /// <summary>Free-text operator notes, shown when editing the NAS.</summary>
    [StringLength(2000)]
    [Display(Name = "Notes")]
    public string? Notes { get; set; }

    [Display(Name = "Last seen")]
    public DateTime? LastSeenUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
