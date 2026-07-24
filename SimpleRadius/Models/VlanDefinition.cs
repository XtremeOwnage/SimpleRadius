using System.ComponentModel.DataAnnotations;

namespace SimpleRadius.Models;

/// <summary>A VLAN that clients can be assigned to and that is returned in the Access-Accept.</summary>
public class VlanDefinition
{
    public int Id { get; set; }

    [Required]
    [StringLength(100)]
    [Display(Name = "Name")]
    public string Name { get; set; } = string.Empty;

    [Range(1, 4094)]
    [Display(Name = "VLAN ID")]
    public int VlanId { get; set; }

    [StringLength(256)]
    [Display(Name = "Description")]
    public string? Description { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public ICollection<ClientDevice> Clients { get; set; } = new List<ClientDevice>();
}
