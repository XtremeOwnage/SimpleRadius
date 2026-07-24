using Microsoft.EntityFrameworkCore;
using SimpleRadius.Data;
using SimpleRadius.Models;

namespace SimpleRadius.Services;

/// <summary>
/// Reads and writes the single settings row. Every request reads it fresh, so a change on the settings
/// page applies to the next packet without restarting the listener.
/// </summary>
public class SettingsService
{
    private readonly RadiusDbContext _db;

    public SettingsService(RadiusDbContext db)
    {
        _db = db;
    }

    /// <summary>Returns the settings row, creating it from <paramref name="seed"/> if it is missing.</summary>
    public async Task<ServerSettings> GetAsync(ServerSettings? seed = null, CancellationToken cancellationToken = default)
    {
        var settings = await _db.ServerSettings
            .FirstOrDefaultAsync(s => s.Id == ServerSettings.SingletonId, cancellationToken);

        if (settings is not null)
        {
            return settings;
        }

        settings = seed ?? new ServerSettings();
        settings.Id = ServerSettings.SingletonId;
        settings.UpdatedUtc = DateTime.UtcNow;
        _db.ServerSettings.Add(settings);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Another request created it first; use that row.
            _db.Entry(settings).State = EntityState.Detached;
            settings = await _db.ServerSettings.FirstAsync(s => s.Id == ServerSettings.SingletonId, cancellationToken);
        }

        return settings;
    }

    /// <summary>Reads the settings without tracking, for pages that only display them.</summary>
    public async Task<ServerSettings> GetReadOnlyAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _db.ServerSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == ServerSettings.SingletonId, cancellationToken);

        return settings ?? await GetAsync(cancellationToken: cancellationToken);
    }

    public async Task SaveAsync(ServerSettings updated, CancellationToken cancellationToken = default)
    {
        var settings = await GetAsync(cancellationToken: cancellationToken);

        settings.DefaultVlanId = updated.DefaultVlanId;
        settings.AutoCreateUnknownClients = updated.AutoCreateUnknownClients;
        settings.AutoRegisterUnknownNas = updated.AutoRegisterUnknownNas;
        settings.DefaultSharedSecret = updated.DefaultSharedSecret;
        settings.RequireMessageAuthenticator = updated.RequireMessageAuthenticator;
        settings.MacAddressFormat = updated.MacAddressFormat;
        settings.SendTunnelAttributes = updated.SendTunnelAttributes;
        settings.TunnelTag = updated.TunnelTag;
        settings.SendEgressVlanId = updated.SendEgressVlanId;
        settings.SendServiceType = updated.SendServiceType;
        settings.AcctInterimIntervalSeconds = updated.AcctInterimIntervalSeconds;
        settings.UpdatedUtc = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
    }
}
