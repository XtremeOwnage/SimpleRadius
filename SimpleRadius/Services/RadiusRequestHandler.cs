using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleRadius.Data;
using SimpleRadius.Models;
using SimpleRadius.Radius;

namespace SimpleRadius.Services;

/// <summary>Which UDP listener a datagram arrived on.</summary>
public enum RadiusListenerRole
{
    Authentication,
    Accounting
}

/// <summary>
/// Turns an inbound datagram into a reply. Kept free of sockets so the whole request path can be
/// exercised directly against an in-memory database.
/// </summary>
public sealed class RadiusRequestHandler
{
    private readonly RadiusDbContext _db;
    private readonly RadiusServerSettings _startup;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;

    public RadiusRequestHandler(
        RadiusDbContext db,
        RadiusServerSettings? startup = null,
        ILoggerFactory? loggerFactory = null)
    {
        _db = db;
        _startup = startup ?? new RadiusServerSettings();

        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _logger = _loggerFactory.CreateLogger<RadiusRequestHandler>();
        Settings = new SettingsService(db);
        Accounting = new AccountingService(db, _loggerFactory.CreateLogger<AccountingService>());
    }

    public SettingsService Settings { get; }

    public AccountingService Accounting { get; }

    /// <summary>
    /// Processes one datagram and returns the bytes to send back, or null when the packet must be
    /// silently discarded (RFC 2865 requires silence for unauthenticated or malformed traffic).
    /// </summary>
    public async Task<byte[]?> HandleAsync(
        byte[] datagram,
        IPAddress source,
        RadiusListenerRole role,
        CancellationToken cancellationToken = default)
    {
        if (!RadiusPacket.TryParse(datagram, out var packet, out var parseError))
        {
            _logger.LogWarning("Discarded malformed packet from {Source}: {Error}", source, parseError);
            return null;
        }

        // Read fresh each request so a change on the settings page takes effect immediately.
        var settings = await Settings.GetAsync(_startup.Seed, cancellationToken);
        var policy = new RadiusPolicyService(
            _db,
            settings,
            _startup.DefaultVlanName,
            _loggerFactory.CreateLogger<RadiusPolicyService>());

        var sourceAddress = source.ToString();
        var nas = await policy.ResolveNasAsync(sourceAddress, cancellationToken);
        if (nas is null)
        {
            _logger.LogWarning(
                "Discarded {Code} from unconfigured NAS {Source}. Add it under Network Access Servers to allow it.",
                packet.Code,
                sourceAddress);
            return null;
        }

        if (!nas.IsEnabled)
        {
            _logger.LogWarning("Discarded {Code} from disabled NAS {Nas}", packet.Code, nas.Name);
            return null;
        }

        var expectedCode = role == RadiusListenerRole.Authentication
            ? RadiusCode.AccessRequest
            : RadiusCode.AccountingRequest;

        if (packet.Code != expectedCode)
        {
            _logger.LogWarning("Discarded {Code} received on the {Role} port from {Nas}", packet.Code, role, nas.Name);
            return null;
        }

        nas.LastSeenUtc = DateTime.UtcNow;

        return role == RadiusListenerRole.Authentication
            ? await HandleAccessRequestAsync(packet, nas, policy, settings, cancellationToken)
            : await HandleAccountingRequestAsync(packet, nas, policy, cancellationToken);
    }

    private async Task<byte[]?> HandleAccessRequestAsync(
        RadiusPacket packet,
        NetworkAccessServer nas,
        RadiusPolicyService policy,
        ServerSettings settings,
        CancellationToken cancellationToken)
    {
        switch (packet.VerifyMessageAuthenticator(nas.SharedSecret))
        {
            case MessageAuthenticatorState.Invalid:
                _logger.LogWarning(
                    "Discarded Access-Request from {Nas}: Message-Authenticator failed, the shared secret does not match",
                    nas.Name);
                return null;

            case MessageAuthenticatorState.Absent when settings.RequireMessageAuthenticator:
                _logger.LogWarning(
                    "Discarded Access-Request from {Nas}: no Message-Authenticator and the setting requires one",
                    nas.Name);
                return null;
        }

        var identity = packet.GetString(RadiusAttributeType.UserName)
            ?? packet.GetString(RadiusAttributeType.CallingStationId);

        if (string.IsNullOrWhiteSpace(identity))
        {
            _logger.LogWarning("Rejected Access-Request from {Nas}: no User-Name or Calling-Station-Id", nas.Name);
            return Reject(packet, nas, "No client identity supplied");
        }

        var result = await policy.AuthorizeAsync(identity, nas.IpAddress, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        if (!result.IsAccepted)
        {
            _logger.LogInformation("Rejected {Identity} from {Nas}: {Reason}", identity, nas.Name, result.Reason);
            return Reject(packet, nas, result.Reason ?? "Access denied");
        }

        _logger.LogInformation(
            "Accepted {Identity} from {Nas} on VLAN {VlanId}",
            result.Client!.Name,
            nas.Name,
            result.VlanId);

        return packet.BuildResponse(
            RadiusCode.AccessAccept,
            AccessAcceptAttributes.Build(result.VlanId, settings),
            nas.SharedSecret);
    }

    private async Task<byte[]?> HandleAccountingRequestAsync(
        RadiusPacket packet,
        NetworkAccessServer nas,
        RadiusPolicyService policy,
        CancellationToken cancellationToken)
    {
        // Unlike Access-Request, an accounting request authenticator is a keyed digest, so a wrong
        // shared secret is detectable from the packet alone.
        if (!packet.HasValidRequestAuthenticator(nas.SharedSecret))
        {
            _logger.LogWarning(
                "Discarded Accounting-Request from {Nas}: request authenticator failed, the shared secret does not match",
                nas.Name);
            return null;
        }

        if (!nas.AccountingEnabled)
        {
            // Acknowledge anyway; otherwise the NAS retries the same record indefinitely.
            _logger.LogDebug("Accounting is disabled for {Nas}; acknowledging without storing", nas.Name);
            await _db.SaveChangesAsync(cancellationToken);
            return packet.BuildResponse(RadiusCode.AccountingResponse, [], nas.SharedSecret, includeMessageAuthenticator: false);
        }

        var identity = packet.GetString(RadiusAttributeType.UserName)
            ?? packet.GetString(RadiusAttributeType.CallingStationId)
            ?? string.Empty;

        var normalized = MacAddress.Normalize(identity);
        var client = string.IsNullOrEmpty(normalized)
            ? null
            : await policy.FindClientAsync(normalized, cancellationToken);

        await Accounting.RecordAsync(packet, nas, client, normalized, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        return packet.BuildResponse(RadiusCode.AccountingResponse, [], nas.SharedSecret, includeMessageAuthenticator: false);
    }

    private static byte[] Reject(RadiusPacket packet, NetworkAccessServer nas, string reason) =>
        packet.BuildResponse(
            RadiusCode.AccessReject,
            [RadiusAttribute.FromString(RadiusAttributeType.ReplyMessage, reason)],
            nas.SharedSecret);
}
