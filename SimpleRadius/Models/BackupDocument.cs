namespace SimpleRadius.Models;

/// <summary>
/// The portable shape of a configuration export. Deliberately not the EF entities: it omits internal ids
/// and timestamps and references a client's VLAN by its VLAN number, so a file exported from one instance
/// imports cleanly into another.
/// </summary>
public class BackupDocument
{
    /// <summary>Schema version of this document, so a future importer can adapt.</summary>
    public string Version { get; set; } = "1";

    public DateTime ExportedUtc { get; set; } = DateTime.UtcNow;

    public List<BackupVlan> Vlans { get; set; } = [];

    public List<BackupClient> Clients { get; set; } = [];

    public List<BackupNas> NetworkAccessServers { get; set; } = [];

    /// <summary>Server settings. Present in an export; only applied on import when explicitly requested.</summary>
    public BackupSettings? Settings { get; set; }
}

public class BackupVlan
{
    public string Name { get; set; } = string.Empty;
    public int VlanId { get; set; }
    public string? Group { get; set; }
    public string? Description { get; set; }
    public string? Notes { get; set; }
}

public class BackupClient
{
    /// <summary>The RADIUS identity — the MAC or user name the device authenticates with.</summary>
    public string Identity { get; set; } = string.Empty;

    /// <summary>Friendly name shown in the UI.</summary>
    public string? Name { get; set; }

    /// <summary>References a VLAN by its VLAN number, not an internal id.</summary>
    public int VlanId { get; set; }

    public bool Enabled { get; set; } = true;

    public string? Notes { get; set; }
}

public class BackupNas
{
    public string Name { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>Plaintext, because RADIUS needs the original value. An export therefore contains secrets.</summary>
    public string SharedSecret { get; set; } = string.Empty;

    public bool AccountingEnabled { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public string? Notes { get; set; }
}

public class BackupSettings
{
    public int DefaultVlanId { get; set; }
    public bool AutoCreateUnknownClients { get; set; }
    public bool AutoRegisterUnknownNas { get; set; }
    public string DefaultSharedSecret { get; set; } = string.Empty;
    public bool RequireMessageAuthenticator { get; set; }
    public MacAddressFormat MacAddressFormat { get; set; }
    public bool SendTunnelAttributes { get; set; }
    public int TunnelTag { get; set; }
    public bool SendEgressVlanId { get; set; }
    public bool SendServiceType { get; set; }
    public int AcctInterimIntervalSeconds { get; set; }
}

/// <summary>The two serialisation formats the backup page offers.</summary>
public enum BackupFormat
{
    Json,
    Yaml
}

/// <summary>Options controlling how an import is applied.</summary>
public class ImportOptions
{
    /// <summary>When true, the server settings in the document overwrite the current ones.</summary>
    public bool ImportSettings { get; set; }
}

/// <summary>Summary of what an import changed, surfaced to the operator.</summary>
public class ImportResult
{
    public int VlansAdded { get; set; }
    public int VlansUpdated { get; set; }
    public int ClientsAdded { get; set; }
    public int ClientsUpdated { get; set; }
    public int NasAdded { get; set; }
    public int NasUpdated { get; set; }
    public bool SettingsApplied { get; set; }

    /// <summary>Non-fatal issues, e.g. a client referencing a VLAN that was not in the file.</summary>
    public List<string> Warnings { get; } = [];

    public int TotalChanged =>
        VlansAdded + VlansUpdated + ClientsAdded + ClientsUpdated + NasAdded + NasUpdated;
}
