using Microsoft.EntityFrameworkCore;
using SimpleRadius.Models;
using SimpleRadius.Services;

namespace SimpleRadius.Data;

/// <summary>Creates the SQLite file on first run, seeds the settings row and the default VLAN.</summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var startup = services.GetRequiredService<RadiusServerSettings>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DatabaseInitializer));

        var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(startup.DatabasePath));
        if (!string.IsNullOrEmpty(databaseDirectory))
        {
            Directory.CreateDirectory(databaseDirectory);
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RadiusDbContext>();

        await db.Database.EnsureCreatedAsync(cancellationToken);

        // Write-ahead logging keeps the admin UI readable while the listener is writing accounting rows.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);

        // Bring a database created by an older build up to date before anything queries the new columns.
        await SchemaUpgrader.ApplyAsync(db, logger, cancellationToken);

        // appsettings only seeds these on first run; after that the settings page owns them.
        var settingsService = scope.ServiceProvider.GetRequiredService<SettingsService>();
        var settings = await settingsService.GetAsync(startup.Seed, cancellationToken);

        var policy = new RadiusPolicyService(db, settings, startup.DefaultVlanName);
        var defaultVlan = await policy.GetOrCreateDefaultVlanAsync(cancellationToken);

        logger.LogInformation(
            "Database ready at {Path}; default VLAN is {VlanName} ({VlanId})",
            Path.GetFullPath(startup.DatabasePath),
            defaultVlan.Name,
            defaultVlan.VlanId);
    }
}
