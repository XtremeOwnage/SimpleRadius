using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SimpleRadius.Data;
using SimpleRadius.Models;
using SimpleRadius.Radius;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SimpleRadius.Services;

/// <summary>
/// Exports and imports the configuration — VLANs, clients, network access servers and settings — as JSON
/// or YAML. Import is an upsert keyed on natural identifiers (VLAN number, client identity, NAS address),
/// so the same file both restores onto an empty instance and merges into a populated one.
/// </summary>
public class BackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly RadiusDbContext _db;
    private readonly SettingsService _settings;
    private readonly ILogger<BackupService>? _logger;

    public BackupService(RadiusDbContext db, SettingsService settings, ILogger<BackupService>? logger = null)
    {
        _db = db;
        _settings = settings;
        _logger = logger;
    }

    public async Task<BackupDocument> ExportAsync(bool includeAccounting = false, CancellationToken cancellationToken = default)
    {
        var vlans = await _db.VlanDefinitions
            .AsNoTracking()
            .OrderBy(v => v.VlanId)
            .Select(v => new BackupVlan
            {
                Name = v.Name,
                VlanId = v.VlanId,
                Group = v.Group,
                Description = v.Description,
                Notes = v.Notes
            })
            .ToListAsync(cancellationToken);

        var clients = await _db.ClientDevices
            .AsNoTracking()
            .Include(c => c.VlanDefinition)
            .OrderBy(c => c.Name)
            .Select(c => new BackupClient
            {
                Identity = c.Name,
                Name = c.Description,
                VlanId = c.VlanDefinition!.VlanId,
                Enabled = c.IsEnabled,
                Notes = c.Notes
            })
            .ToListAsync(cancellationToken);

        var nas = await _db.NetworkAccessServers
            .AsNoTracking()
            .OrderBy(n => n.Name)
            .Select(n => new BackupNas
            {
                Name = n.Name,
                IpAddress = n.IpAddress,
                SharedSecret = n.SharedSecret,
                AccountingEnabled = n.AccountingEnabled,
                Enabled = n.IsEnabled,
                Notes = n.Notes
            })
            .ToListAsync(cancellationToken);

        var ssidRules = await _db.SsidVlanRules
            .AsNoTracking()
            .Include(r => r.VlanDefinition)
            .OrderBy(r => r.Ssid)
            .Select(r => new BackupSsidRule
            {
                Ssid = r.Ssid,
                VlanId = r.VlanDefinition!.VlanId,
                Notes = r.Notes
            })
            .ToListAsync(cancellationToken);

        List<BackupSession>? sessions = null;
        if (includeAccounting)
        {
            sessions = await _db.AccountingSessions
                .AsNoTracking()
                .OrderBy(s => s.StartTime)
                .Select(s => new BackupSession
                {
                    SessionId = s.SessionId,
                    ClientName = s.ClientName,
                    CallingStationId = s.CallingStationId,
                    CalledStationId = s.CalledStationId,
                    Ssid = s.Ssid,
                    NasIdentifier = s.NasIdentifier,
                    NasPortType = s.NasPortType,
                    NasName = s.NasName,
                    NasIpAddress = s.NasIpAddress,
                    VlanId = s.VlanId,
                    AcctStatusType = s.AcctStatusType,
                    IsActive = s.IsActive,
                    BytesIn = s.BytesIn,
                    BytesOut = s.BytesOut,
                    PacketsIn = s.PacketsIn,
                    PacketsOut = s.PacketsOut,
                    SessionSeconds = s.SessionSeconds,
                    TerminateCause = s.TerminateCause,
                    StartTime = s.StartTime,
                    LastUpdateTime = s.LastUpdateTime,
                    StopTime = s.StopTime
                })
                .ToListAsync(cancellationToken);
        }

        var settings = await _settings.GetReadOnlyAsync(cancellationToken);

        return new BackupDocument
        {
            Vlans = vlans,
            Clients = clients,
            NetworkAccessServers = nas,
            SsidRules = ssidRules,
            Sessions = sessions,
            Settings = new BackupSettings
            {
                DefaultVlanId = settings.DefaultVlanId,
                AutoCreateUnknownClients = settings.AutoCreateUnknownClients,
                AutoRegisterUnknownNas = settings.AutoRegisterUnknownNas,
                DefaultSharedSecret = settings.DefaultSharedSecret,
                RequireMessageAuthenticator = settings.RequireMessageAuthenticator,
                MacAddressFormat = settings.MacAddressFormat,
                SendTunnelAttributes = settings.SendTunnelAttributes,
                TunnelTag = settings.TunnelTag,
                SendEgressVlanId = settings.SendEgressVlanId,
                SendServiceType = settings.SendServiceType,
                AcctInterimIntervalSeconds = settings.AcctInterimIntervalSeconds
            }
        };
    }

    public string Serialize(BackupDocument document, BackupFormat format) => format switch
    {
        BackupFormat.Yaml => YamlSerializer.Serialize(document),
        _ => JsonSerializer.Serialize(document, JsonOptions)
    };

    /// <summary>
    /// Parses a document, auto-detecting the format. Throws <see cref="BackupFormatException"/> with a
    /// readable message on malformed input, so the page can show it rather than a 500.
    /// </summary>
    public static BackupDocument Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new BackupFormatException("The file is empty.");
        }

        var looksLikeJson = content.TrimStart().StartsWith('{');

        try
        {
            var document = looksLikeJson
                ? JsonSerializer.Deserialize<BackupDocument>(content, JsonOptions)
                : YamlDeserializer.Deserialize<BackupDocument>(content);

            return document ?? throw new BackupFormatException("The file did not contain a configuration document.");
        }
        catch (Exception ex) when (ex is JsonException or YamlDotNet.Core.YamlException)
        {
            throw new BackupFormatException($"The file is not valid {(looksLikeJson ? "JSON" : "YAML")}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Applies a document as an upsert inside a transaction, so a failure leaves the database untouched.
    /// </summary>
    public async Task<ImportResult> ImportAsync(
        BackupDocument document,
        ImportOptions options,
        CancellationToken cancellationToken = default)
    {
        var result = new ImportResult();

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        // VLANs first: clients reference them, and the default-VLAN fallback needs at least one to exist.
        var vlansByNumber = await _db.VlanDefinitions.ToDictionaryAsync(v => v.VlanId, cancellationToken);
        foreach (var incoming in document.Vlans)
        {
            if (incoming.VlanId is < 1 or > 4094 || string.IsNullOrWhiteSpace(incoming.Name))
            {
                result.Warnings.Add($"Skipped VLAN '{incoming.Name}' ({incoming.VlanId}): invalid name or VLAN ID.");
                continue;
            }

            if (vlansByNumber.TryGetValue(incoming.VlanId, out var existing))
            {
                existing.Name = incoming.Name;
                existing.Group = incoming.Group;
                existing.Description = incoming.Description;
                existing.Notes = incoming.Notes;
                existing.UpdatedUtc = DateTime.UtcNow;
                result.VlansUpdated++;
            }
            else
            {
                var vlan = new VlanDefinition
                {
                    Name = incoming.Name,
                    VlanId = incoming.VlanId,
                    Group = incoming.Group,
                    Description = incoming.Description,
                    Notes = incoming.Notes
                };
                _db.VlanDefinitions.Add(vlan);
                vlansByNumber[incoming.VlanId] = vlan;
                result.VlansAdded++;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        // Network access servers, keyed on their source address.
        var nasByAddress = await _db.NetworkAccessServers.ToDictionaryAsync(n => n.IpAddress, cancellationToken);
        foreach (var incoming in document.NetworkAccessServers)
        {
            if (string.IsNullOrWhiteSpace(incoming.IpAddress) || string.IsNullOrWhiteSpace(incoming.Name))
            {
                result.Warnings.Add($"Skipped NAS '{incoming.Name}': missing name or IP address.");
                continue;
            }

            if (nasByAddress.TryGetValue(incoming.IpAddress, out var existing))
            {
                existing.Name = incoming.Name;
                existing.SharedSecret = incoming.SharedSecret;
                existing.AccountingEnabled = incoming.AccountingEnabled;
                existing.IsEnabled = incoming.Enabled;
                existing.Notes = incoming.Notes;
                existing.IsAutoRegistered = false;
                existing.UpdatedUtc = DateTime.UtcNow;
                result.NasUpdated++;
            }
            else
            {
                _db.NetworkAccessServers.Add(new NetworkAccessServer
                {
                    Name = incoming.Name,
                    IpAddress = incoming.IpAddress,
                    SharedSecret = incoming.SharedSecret,
                    AccountingEnabled = incoming.AccountingEnabled,
                    IsEnabled = incoming.Enabled,
                    Notes = incoming.Notes
                });
                result.NasAdded++;
            }
        }

        // Clients, keyed on the normalised identity. VLAN references resolve by number.
        var fallbackVlan = vlansByNumber.Values.FirstOrDefault();
        var clientsByName = await _db.ClientDevices.ToDictionaryAsync(c => c.Name, cancellationToken);
        foreach (var incoming in document.Clients)
        {
            var identity = MacAddress.Normalize(incoming.Identity ?? string.Empty);
            if (string.IsNullOrWhiteSpace(identity))
            {
                result.Warnings.Add("Skipped a client with no identity.");
                continue;
            }

            if (!vlansByNumber.TryGetValue(incoming.VlanId, out var vlan))
            {
                if (fallbackVlan is null)
                {
                    result.Warnings.Add($"Skipped client '{identity}': VLAN {incoming.VlanId} is not defined and there is no VLAN to fall back to.");
                    continue;
                }

                vlan = fallbackVlan;
                result.Warnings.Add($"Client '{identity}' referenced VLAN {incoming.VlanId}, which is not defined; assigned to VLAN {vlan.VlanId} instead.");
            }

            if (clientsByName.TryGetValue(identity, out var existing))
            {
                existing.Description = incoming.Name;
                existing.Notes = incoming.Notes;
                existing.IsEnabled = incoming.Enabled;
                existing.VlanDefinition = vlan;
                existing.UpdatedUtc = DateTime.UtcNow;
                result.ClientsUpdated++;
            }
            else
            {
                _db.ClientDevices.Add(new ClientDevice
                {
                    Name = identity,
                    Description = incoming.Name,
                    Notes = incoming.Notes,
                    IsEnabled = incoming.Enabled,
                    IsAutoCreated = false,
                    VlanDefinition = vlan
                });
                result.ClientsAdded++;
            }
        }

        // SSID rules, keyed on the SSID. The referenced VLAN must exist — an SSID mapped to a missing
        // VLAN is meaningless, so it is skipped rather than pointed at a fallback.
        await _db.SaveChangesAsync(cancellationToken);
        var rulesBySsid = await _db.SsidVlanRules.ToDictionaryAsync(r => r.Ssid, cancellationToken);
        foreach (var incoming in document.SsidRules)
        {
            var ssid = incoming.Ssid?.Trim();
            if (string.IsNullOrWhiteSpace(ssid))
            {
                result.Warnings.Add("Skipped an SSID rule with no SSID.");
                continue;
            }

            if (!vlansByNumber.TryGetValue(incoming.VlanId, out var vlan))
            {
                result.Warnings.Add($"Skipped SSID rule '{ssid}': VLAN {incoming.VlanId} is not defined.");
                continue;
            }

            if (rulesBySsid.TryGetValue(ssid, out var existing))
            {
                existing.VlanDefinition = vlan;
                existing.Notes = incoming.Notes;
                existing.UpdatedUtc = DateTime.UtcNow;
                result.SsidRulesUpdated++;
            }
            else
            {
                _db.SsidVlanRules.Add(new SsidVlanRule
                {
                    Ssid = ssid,
                    VlanDefinition = vlan,
                    Notes = incoming.Notes
                });
                result.SsidRulesAdded++;
            }
        }

        // Accounting sessions, opt-in and keyed on (NAS address, session id). Restoring is upsert too, so
        // re-importing the same file does not duplicate history.
        if (options.ImportAccounting && document.Sessions is { Count: > 0 } incomingSessions)
        {
            await _db.SaveChangesAsync(cancellationToken);
            var clientsForSessions = await _db.ClientDevices.ToDictionaryAsync(c => c.Name, c => c.Id, cancellationToken);
            var existingSessions = await _db.AccountingSessions
                .ToDictionaryAsync(s => (s.NasIpAddress, s.SessionId), cancellationToken);

            foreach (var incoming in incomingSessions)
            {
                if (string.IsNullOrWhiteSpace(incoming.SessionId) || string.IsNullOrWhiteSpace(incoming.NasIpAddress))
                {
                    result.Warnings.Add("Skipped an accounting session with no session id or NAS address.");
                    continue;
                }

                var session = existingSessions.GetValueOrDefault((incoming.NasIpAddress, incoming.SessionId));
                if (session is null)
                {
                    session = new AccountingSession { SessionId = incoming.SessionId, NasIpAddress = incoming.NasIpAddress };
                    _db.AccountingSessions.Add(session);
                }

                session.ClientName = incoming.ClientName;
                session.ClientDeviceId = clientsForSessions.TryGetValue(incoming.ClientName, out var cid) ? cid : null;
                session.CallingStationId = incoming.CallingStationId;
                session.CalledStationId = incoming.CalledStationId;
                session.Ssid = incoming.Ssid;
                session.NasIdentifier = incoming.NasIdentifier;
                session.NasPortType = incoming.NasPortType;
                session.NasName = incoming.NasName;
                session.VlanId = incoming.VlanId;
                session.AcctStatusType = incoming.AcctStatusType;
                session.IsActive = incoming.IsActive;
                session.BytesIn = incoming.BytesIn;
                session.BytesOut = incoming.BytesOut;
                session.PacketsIn = incoming.PacketsIn;
                session.PacketsOut = incoming.PacketsOut;
                session.SessionSeconds = incoming.SessionSeconds;
                session.TerminateCause = incoming.TerminateCause;
                session.StartTime = incoming.StartTime;
                session.LastUpdateTime = incoming.LastUpdateTime;
                session.StopTime = incoming.StopTime;
                result.SessionsImported++;
            }
        }

        if (options.ImportSettings && document.Settings is { } incomingSettings)
        {
            var settings = await _settings.GetAsync(cancellationToken: cancellationToken);
            settings.DefaultVlanId = incomingSettings.DefaultVlanId;
            settings.AutoCreateUnknownClients = incomingSettings.AutoCreateUnknownClients;
            settings.AutoRegisterUnknownNas = incomingSettings.AutoRegisterUnknownNas;
            settings.DefaultSharedSecret = incomingSettings.DefaultSharedSecret;
            settings.RequireMessageAuthenticator = incomingSettings.RequireMessageAuthenticator;
            settings.MacAddressFormat = incomingSettings.MacAddressFormat;
            settings.SendTunnelAttributes = incomingSettings.SendTunnelAttributes;
            settings.TunnelTag = incomingSettings.TunnelTag;
            settings.SendEgressVlanId = incomingSettings.SendEgressVlanId;
            settings.SendServiceType = incomingSettings.SendServiceType;
            settings.AcctInterimIntervalSeconds = incomingSettings.AcctInterimIntervalSeconds;
            settings.UpdatedUtc = DateTime.UtcNow;
            result.SettingsApplied = true;
        }

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger?.LogInformation(
            "Imported configuration: {Changed} record(s) changed, {Warnings} warning(s)",
            result.TotalChanged,
            result.Warnings.Count);

        return result;
    }

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();
}

/// <summary>Thrown when an uploaded document cannot be parsed, so the page shows a message not a 500.</summary>
public sealed class BackupFormatException : Exception
{
    public BackupFormatException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}
