namespace SimpleRadius.Radius;

/// <summary>Standard RADIUS attribute type codes referenced by this server.</summary>
public static class RadiusAttributeType
{
    public const byte UserName = 1;
    public const byte UserPassword = 2;
    public const byte NasIpAddress = 4;
    public const byte NasPort = 5;
    public const byte ServiceType = 6;
    public const byte ReplyMessage = 18;
    public const byte CalledStationId = 30;
    public const byte CallingStationId = 31;
    public const byte NasIdentifier = 32;
    public const byte AcctStatusType = 40;
    public const byte AcctInputOctets = 42;
    public const byte AcctOutputOctets = 43;
    public const byte AcctSessionId = 44;
    public const byte AcctSessionTime = 46;
    public const byte AcctInputPackets = 47;
    public const byte AcctOutputPackets = 48;
    public const byte AcctTerminateCause = 49;
    public const byte AcctInputGigawords = 52;
    public const byte AcctOutputGigawords = 53;
    public const byte NasPortType = 61;
    public const byte TunnelType = 64;
    public const byte TunnelMediumType = 65;
    public const byte MessageAuthenticator = 80;
    public const byte TunnelPrivateGroupId = 81;

    /// <summary>RFC 2869: how often the NAS should send interim accounting updates.</summary>
    public const byte AcctInterimInterval = 85;

    /// <summary>RFC 4675: an alternative way to express the assigned VLAN.</summary>
    public const byte EgressVlanId = 56;
}

/// <summary>Values used with <see cref="RadiusAttributeType.TunnelType"/> and friends.</summary>
public static class TunnelValues
{
    /// <summary>Tunnel-Type = VLAN.</summary>
    public const uint TypeVlan = 13;

    /// <summary>Tunnel-Medium-Type = IEEE-802.</summary>
    public const uint MediumIeee802 = 6;

    /// <summary>Tag applied to the VLAN tunnel attribute group so a NAS reads them as one set.</summary>
    public const byte VlanTag = 1;
}
