using System.ComponentModel.DataAnnotations;

namespace SimpleRadius.Models;

/// <summary>
/// Startup configuration bound from the "RadiusServerSettings" section of appsettings.json. These are read
/// once when the process starts and need a restart to take effect. Everything that can be changed while
/// running lives in <see cref="ServerSettings"/> and is edited on the settings page.
/// </summary>
public class RadiusServerSettings
{
    /// <summary>
    /// Address the admin web UI listens on. Applied only when nothing else has already chosen one, so
    /// ASPNETCORE_URLS, --urls and a launch profile's applicationUrl all still win over it.
    /// </summary>
    public string WebUiUrl { get; set; } = "http://0.0.0.0:5285";

    [Range(1, 65535)]
    public int AuthPort { get; set; } = 1812;

    [Range(1, 65535)]
    public int AcctPort { get; set; } = 1813;

    /// <summary>Address the UDP listeners bind to. "0.0.0.0" listens on every IPv4 interface.</summary>
    public string ListenAddress { get; set; } = "0.0.0.0";

    public string DatabasePath { get; set; } = "data/simple-radius.db";

    /// <summary>Name given to the default VLAN row when it has to be created.</summary>
    public string DefaultVlanName { get; set; } = "Default";

    /// <summary>
    /// Values written into the settings row the first time the database is created. Editing them later has
    /// no effect: once the row exists the settings page is the source of truth.
    /// </summary>
    public ServerSettings Seed { get; set; } = new();
}
