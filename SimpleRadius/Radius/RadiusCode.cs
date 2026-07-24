namespace SimpleRadius.Radius;

/// <summary>RADIUS packet codes (RFC 2865 / RFC 2866) used by this server.</summary>
public enum RadiusCode : byte
{
    AccessRequest = 1,
    AccessAccept = 2,
    AccessReject = 3,
    AccountingRequest = 4,
    AccountingResponse = 5
}

/// <summary>Values of the Acct-Status-Type attribute (RFC 2866).</summary>
public enum AcctStatusType
{
    Start = 1,
    Stop = 2,
    InterimUpdate = 3,
    AccountingOn = 7,
    AccountingOff = 8
}
