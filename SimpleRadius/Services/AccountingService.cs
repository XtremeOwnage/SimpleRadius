using Microsoft.EntityFrameworkCore;
using SimpleRadius.Data;
using SimpleRadius.Models;
using SimpleRadius.Radius;

namespace SimpleRadius.Services;

/// <summary>
/// Persists the accounting stream sent by a NAS: sessions open on Start, are refreshed by interim
/// updates and close on Stop, so the admin UI can show both live sessions and history.
/// </summary>
public class AccountingService
{
    private readonly RadiusDbContext _db;
    private readonly ILogger<AccountingService>? _logger;

    public AccountingService(RadiusDbContext db, ILogger<AccountingService>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Applies one Accounting-Request to the session store. Returns the affected session, or null when
    /// the packet carried no session to record.
    /// </summary>
    public async Task<AccountingSession?> RecordAsync(
        RadiusPacket packet,
        NetworkAccessServer nas,
        ClientDevice? client,
        string identity,
        CancellationToken cancellationToken = default)
    {
        var statusType = (int)(packet.GetUInt32(RadiusAttributeType.AcctStatusType) ?? 0);

        if (statusType is (int)AcctStatusType.AccountingOn or (int)AcctStatusType.AccountingOff)
        {
            await CloseSessionsForNasAsync(nas, cancellationToken);
            return null;
        }

        var sessionId = packet.GetString(RadiusAttributeType.AcctSessionId);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            _logger?.LogWarning("Accounting request from {Nas} carried no Acct-Session-Id; nothing recorded", nas.IpAddress);
            return null;
        }

        var now = DateTime.UtcNow;
        var session = await _db.AccountingSessions
            .FirstOrDefaultAsync(a => a.NasIpAddress == nas.IpAddress && a.SessionId == sessionId, cancellationToken);

        if (session is null)
        {
            session = new AccountingSession
            {
                SessionId = sessionId,
                NasIpAddress = nas.IpAddress,
                StartTime = now,
                CreatedUtc = now
            };
            _db.AccountingSessions.Add(session);
        }

        var calledStation = packet.GetString(RadiusAttributeType.CalledStationId);

        session.ClientName = identity;
        session.ClientDeviceId = client?.Id;
        session.CallingStationId = packet.GetString(RadiusAttributeType.CallingStationId);
        session.CalledStationId = calledStation;
        session.Ssid = StationId.ExtractSsid(calledStation);
        session.NasIdentifier = packet.GetString(RadiusAttributeType.NasIdentifier);
        session.NasPortType = StationId.DescribeNasPortType(packet.GetUInt32(RadiusAttributeType.NasPortType));
        session.NasName = nas.Name;
        session.VlanId = client?.VlanDefinition?.VlanId ?? session.VlanId;
        session.AcctStatusType = statusType;
        session.LastUpdateTime = now;

        session.BytesIn = CombineOctets(
            packet.GetUInt32(RadiusAttributeType.AcctInputOctets),
            packet.GetUInt32(RadiusAttributeType.AcctInputGigawords)) ?? session.BytesIn;
        session.BytesOut = CombineOctets(
            packet.GetUInt32(RadiusAttributeType.AcctOutputOctets),
            packet.GetUInt32(RadiusAttributeType.AcctOutputGigawords)) ?? session.BytesOut;
        session.PacketsIn = packet.GetUInt32(RadiusAttributeType.AcctInputPackets) ?? session.PacketsIn;
        session.PacketsOut = packet.GetUInt32(RadiusAttributeType.AcctOutputPackets) ?? session.PacketsOut;
        session.SessionSeconds = packet.GetUInt32(RadiusAttributeType.AcctSessionTime) ?? session.SessionSeconds;

        if (statusType == (int)AcctStatusType.Stop)
        {
            session.IsActive = false;
            session.StopTime = now;
            session.TerminateCause = packet.GetUInt32(RadiusAttributeType.AcctTerminateCause);
        }
        else
        {
            session.IsActive = true;
            session.StopTime = null;
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger?.LogInformation(
            "Accounting {Status} for session {SessionId} ({Client}) on {Nas}",
            DescribeStatus(statusType),
            sessionId,
            identity,
            nas.Name);

        return session;
    }

    /// <summary>Closes every live session for a NAS, used when it reports Accounting-On/Off after a restart.</summary>
    private async Task CloseSessionsForNasAsync(NetworkAccessServer nas, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var active = await _db.AccountingSessions
            .Where(a => a.NasIpAddress == nas.IpAddress && a.IsActive)
            .ToListAsync(cancellationToken);

        foreach (var session in active)
        {
            session.IsActive = false;
            session.StopTime = now;
            session.LastUpdateTime = now;
            session.AcctStatusType = (int)AcctStatusType.Stop;
        }

        if (active.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        _logger?.LogInformation("{Nas} reported an accounting restart; closed {Count} session(s)", nas.Name, active.Count);
    }

    /// <summary>Counters wrap at 32 bits, so the gigawords attribute carries the high order bits.</summary>
    private static long? CombineOctets(uint? octets, uint? gigawords)
    {
        if (octets is null)
        {
            return null;
        }

        return ((long)(gigawords ?? 0) << 32) + octets.Value;
    }

    public static string DescribeStatus(int statusType) => statusType switch
    {
        (int)AcctStatusType.Start => "Start",
        (int)AcctStatusType.Stop => "Stop",
        (int)AcctStatusType.InterimUpdate => "Interim-Update",
        (int)AcctStatusType.AccountingOn => "Accounting-On",
        (int)AcctStatusType.AccountingOff => "Accounting-Off",
        _ => $"Type {statusType}"
    };
}
