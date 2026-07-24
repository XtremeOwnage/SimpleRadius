using SimpleRadius.Models;

namespace SimpleRadius.Radius;

/// <summary>
/// Builds the attributes an Access-Accept carries. The VLAN itself travels in Tunnel-Private-Group-Id;
/// the other tunnel attributes are what make a NAS act on it, which is why they are sent as a group.
/// The toggles exist because hardware disagrees about tags and about which extra attributes it wants.
/// </summary>
public static class AccessAcceptAttributes
{
    /// <summary>Service-Type = Framed.</summary>
    private const uint ServiceTypeFramed = 2;

    /// <summary>RFC 4675 tags the VLAN id with 0x31 ("tagged") in the top byte.</summary>
    private const uint EgressVlanTagged = 0x31u << 24;

    public static IReadOnlyList<RadiusAttribute> Build(int vlanId, ServerSettings settings)
    {
        var attributes = new List<RadiusAttribute>(6);

        if (settings.SendServiceType)
        {
            attributes.Add(RadiusAttribute.FromUInt32(RadiusAttributeType.ServiceType, ServiceTypeFramed));
        }

        if (settings.SendTunnelAttributes)
        {
            var tag = (byte)settings.TunnelTag;
            if (tag == 0)
            {
                // Tag 0 means "untagged" in RFC 2868: the tag byte is omitted entirely. Some equipment
                // rejects the attributes outright if a tag is present.
                attributes.Add(RadiusAttribute.FromUInt32(RadiusAttributeType.TunnelType, TunnelValues.TypeVlan));
                attributes.Add(RadiusAttribute.FromUInt32(RadiusAttributeType.TunnelMediumType, TunnelValues.MediumIeee802));
                attributes.Add(RadiusAttribute.FromString(RadiusAttributeType.TunnelPrivateGroupId, vlanId.ToString()));
            }
            else
            {
                attributes.Add(RadiusAttribute.TaggedUInt32(RadiusAttributeType.TunnelType, tag, TunnelValues.TypeVlan));
                attributes.Add(RadiusAttribute.TaggedUInt32(RadiusAttributeType.TunnelMediumType, tag, TunnelValues.MediumIeee802));
                attributes.Add(RadiusAttribute.TaggedString(RadiusAttributeType.TunnelPrivateGroupId, tag, vlanId.ToString()));
            }
        }

        if (settings.SendEgressVlanId)
        {
            attributes.Add(RadiusAttribute.FromUInt32(RadiusAttributeType.EgressVlanId, EgressVlanTagged | (uint)vlanId));
        }

        if (settings.AcctInterimIntervalSeconds > 0)
        {
            attributes.Add(RadiusAttribute.FromUInt32(
                RadiusAttributeType.AcctInterimInterval,
                (uint)settings.AcctInterimIntervalSeconds));
        }

        return attributes;
    }
}
