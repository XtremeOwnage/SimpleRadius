using Microsoft.EntityFrameworkCore;
using SimpleRadius.Data;
using SimpleRadius.Models;
using SimpleRadius.Radius;

namespace SimpleRadius.Services;

/// <summary>
/// Decides who may connect and on which VLAN. Unknown identities are created on first request and given
/// the configured default VLAN, which is what lets a new device onto the network without manual setup.
/// </summary>
public class RadiusPolicyService
{
    private readonly RadiusDbContext _db;
    private readonly ServerSettings _settings;
    private readonly string _defaultVlanName;
    private readonly ILogger<RadiusPolicyService>? _logger;

    public RadiusPolicyService(
        RadiusDbContext db,
        ServerSettings settings,
        string defaultVlanName = "Default",
        ILogger<RadiusPolicyService>? logger = null)
    {
        _db = db;
        _settings = settings;
        _defaultVlanName = defaultVlanName;
        _logger = logger;
    }

    /// <summary>
    /// Returns the VLAN row for <see cref="ServerSettings.DefaultVlanId"/>, creating it if the
    /// operator has not defined one yet.
    /// </summary>
    public async Task<VlanDefinition> GetOrCreateDefaultVlanAsync(CancellationToken cancellationToken = default)
    {
        var vlan = await _db.VlanDefinitions
            .FirstOrDefaultAsync(v => v.VlanId == _settings.DefaultVlanId, cancellationToken);

        if (vlan is not null)
        {
            return vlan;
        }

        vlan = new VlanDefinition
        {
            Name = _defaultVlanName,
            VlanId = _settings.DefaultVlanId,
            Description = "Fallback VLAN assigned to devices seen for the first time."
        };

        _db.VlanDefinitions.Add(vlan);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another request seeded it between the read and the write.
            _db.Entry(vlan).State = EntityState.Detached;
            vlan = await _db.VlanDefinitions
                .FirstAsync(v => v.VlanId == _settings.DefaultVlanId, cancellationToken);
        }

        return vlan;
    }

    /// <summary>
    /// Resolves the NAS a request arrived from. Unknown sources return null unless
    /// <see cref="ServerSettings.AutoRegisterUnknownNas"/> is enabled.
    /// </summary>
    public async Task<NetworkAccessServer?> ResolveNasAsync(string sourceIpAddress, CancellationToken cancellationToken = default)
    {
        var nas = await _db.NetworkAccessServers
            .FirstOrDefaultAsync(n => n.IpAddress == sourceIpAddress, cancellationToken);

        if (nas is not null || !_settings.AutoRegisterUnknownNas)
        {
            return nas;
        }

        nas = new NetworkAccessServer
        {
            Name = sourceIpAddress,
            IpAddress = sourceIpAddress,
            SharedSecret = _settings.DefaultSharedSecret,
            AccountingEnabled = true,
            IsEnabled = true,
            IsAutoRegistered = true
        };

        _db.NetworkAccessServers.Add(nas);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            _logger?.LogWarning(
                "Auto-registered NAS {IpAddress} with the default shared secret. Set its real secret on the NAS page.",
                sourceIpAddress);
        }
        catch (DbUpdateException)
        {
            _db.Entry(nas).State = EntityState.Detached;
            nas = await _db.NetworkAccessServers
                .FirstOrDefaultAsync(n => n.IpAddress == sourceIpAddress, cancellationToken);
        }

        return nas;
    }

    /// <summary>
    /// Applies client policy to an authentication request, creating the client on first sight when
    /// auto-creation is enabled.
    /// </summary>
    public async Task<AuthorizationResult> AuthorizeAsync(
        string identity,
        string? nasIpAddress,
        CancellationToken cancellationToken = default)
    {
        var name = MacAddress.Normalize(identity);
        if (string.IsNullOrWhiteSpace(name))
        {
            return AuthorizationResult.Reject("request carried no usable identity");
        }

        var client = await _db.ClientDevices
            .Include(c => c.VlanDefinition)
            .FirstOrDefaultAsync(c => c.Name == name, cancellationToken);

        if (client is null)
        {
            if (!_settings.AutoCreateUnknownClients)
            {
                return AuthorizationResult.Reject("unknown client and auto-creation is disabled");
            }

            client = await CreateClientAsync(name, cancellationToken);
        }

        if (!client.IsEnabled)
        {
            return AuthorizationResult.Reject("client is disabled", client);
        }

        var vlan = client.VlanDefinition
            ?? await _db.VlanDefinitions.FirstOrDefaultAsync(v => v.Id == client.VlanDefinitionId, cancellationToken);

        if (vlan is null)
        {
            // The assignment disappeared underneath us; fall back rather than deny the device.
            vlan = await GetOrCreateDefaultVlanAsync(cancellationToken);
            client.VlanDefinitionId = vlan.Id;
            client.VlanDefinition = vlan;
        }

        client.LastSeenUtc = DateTime.UtcNow;
        client.UpdatedUtc = client.LastSeenUtc.Value;
        client.LastNasIpAddress = nasIpAddress;
        client.AuthenticationCount++;
        await _db.SaveChangesAsync(cancellationToken);

        return AuthorizationResult.Accept(client, vlan.VlanId);
    }

    /// <summary>Looks up a client without changing it. Used by the accounting path.</summary>
    public Task<ClientDevice?> FindClientAsync(string identity, CancellationToken cancellationToken = default)
    {
        var name = MacAddress.Normalize(identity);
        return _db.ClientDevices
            .Include(c => c.VlanDefinition)
            .FirstOrDefaultAsync(c => c.Name == name, cancellationToken);
    }

    private async Task<ClientDevice> CreateClientAsync(string name, CancellationToken cancellationToken)
    {
        var vlan = await GetOrCreateDefaultVlanAsync(cancellationToken);

        var client = new ClientDevice
        {
            Name = name,
            Description = "Seen for the first time; assigned the default VLAN.",
            VlanDefinitionId = vlan.Id,
            VlanDefinition = vlan,
            IsAutoCreated = true,
            IsEnabled = true
        };

        _db.ClientDevices.Add(client);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            _logger?.LogInformation("Created client {Client} on default VLAN {VlanId}", name, vlan.VlanId);
        }
        catch (DbUpdateException)
        {
            // A concurrent request for the same device won the unique index; use the row it wrote.
            _db.Entry(client).State = EntityState.Detached;
            client = await _db.ClientDevices
                .Include(c => c.VlanDefinition)
                .FirstAsync(c => c.Name == name, cancellationToken);
        }

        return client;
    }
}
