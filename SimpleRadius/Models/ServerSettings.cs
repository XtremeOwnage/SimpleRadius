using System.ComponentModel.DataAnnotations;

namespace SimpleRadius.Models;

/// <summary>
/// Policy and protocol settings that can be changed while the server runs, stored as a single row so the
/// settings page can edit them. Listener ports and the database path are not here: those are read once at
/// startup and live in appsettings.json.
/// </summary>
public class ServerSettings
{
    /// <summary>There is only ever one row.</summary>
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    [Range(1, 4094)]
    [Display(Name = "Default VLAN ID", Description = "VLAN given to devices that have no explicit assignment.")]
    public int DefaultVlanId { get; set; } = 10;

    [Display(Name = "Auto-create unknown clients", Description = "Add a device on its first request instead of rejecting it.")]
    public bool AutoCreateUnknownClients { get; set; } = true;

    [Display(Name = "Auto-register unknown routers", Description = "Accept a NAS that is not configured yet, using the default shared secret below. A router whose secret is unknown cannot be authenticated, so leave this off unless you are testing.")]
    public bool AutoRegisterUnknownNas { get; set; }

    [StringLength(128)]
    [Display(Name = "Default shared secret", Description = "Secret assigned to auto-registered routers.")]
    public string DefaultSharedSecret { get; set; } = "change-me";

    [Display(Name = "Require Message-Authenticator", Description = "Reject an Access-Request that omits the attribute. Turn on only if every router you use sends it.")]
    public bool RequireMessageAuthenticator { get; set; }

    [Display(Name = "MAC address format", Description = "How MAC addresses are displayed. Matching ignores formatting, so this never affects whether a device authenticates.")]
    public MacAddressFormat MacAddressFormat { get; set; } = MacAddressFormat.ColonLower;

    [Display(Name = "Send tunnel attributes", Description = "Tunnel-Type, Tunnel-Medium-Type and Tunnel-Private-Group-Id. This is how a VLAN is assigned; leave on unless you are debugging.")]
    public bool SendTunnelAttributes { get; set; } = true;

    [Range(0, 31)]
    [Display(Name = "Tunnel tag", Description = "Tag binding the three tunnel attributes together (RFC 2868). Use 1 for most hardware, or 0 to send them untagged for equipment that rejects a tag.")]
    public int TunnelTag { get; set; } = 1;

    [Display(Name = "Send Egress-VLANID", Description = "Adds the RFC 4675 attribute alongside the tunnel attributes. Some switches want this instead.")]
    public bool SendEgressVlanId { get; set; }

    [Display(Name = "Send Service-Type", Description = "Adds Service-Type = Framed to the Access-Accept. A few NAS models require it before applying a VLAN.")]
    public bool SendServiceType { get; set; }

    [Range(0, 86400)]
    [Display(Name = "Interim accounting interval (seconds)", Description = "Asks the router to send interim accounting updates at this interval, which is what keeps live session counters moving. 0 leaves the router's own setting alone.")]
    public int AcctInterimIntervalSeconds { get; set; } = 600;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}
