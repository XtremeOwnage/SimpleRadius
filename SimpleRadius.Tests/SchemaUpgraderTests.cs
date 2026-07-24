using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleRadius.Data;
using SimpleRadius.Models;

namespace SimpleRadius.Tests;

public class SchemaUpgraderTests
{
    /// <summary>
    /// Builds a database with the current schema, then drops the Notes columns to mimic one created by an
    /// older build — which is exactly the situation a running deployment upgrades from.
    /// </summary>
    private static async Task<(SqliteConnection Connection, DbContextOptions<RadiusDbContext> Options)> LegacyDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<RadiusDbContext>().UseSqlite(connection).Options;
        await using (var db = new RadiusDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
        }

        foreach (var table in new[] { "ClientDevices", "NetworkAccessServers", "VlanDefinitions" })
        {
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"ALTER TABLE \"{table}\" DROP COLUMN \"Notes\";";
            await drop.ExecuteNonQueryAsync();
        }

        // Group was added after Notes; drop it too so the test covers a database older than both.
        await using (var dropGroup = connection.CreateCommand())
        {
            dropGroup.CommandText = "ALTER TABLE \"VlanDefinitions\" DROP COLUMN \"Group\";";
            await dropGroup.ExecuteNonQueryAsync();
        }

        return (connection, options);
    }

    private static async Task<bool> HasColumnAsync(SqliteConnection connection, string table, string column)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}';";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }

    [Fact]
    public async Task AddsMissingNotesColumnsToAnOlderDatabase()
    {
        var (connection, options) = await LegacyDatabaseAsync();
        try
        {
            Assert.False(await HasColumnAsync(connection, "ClientDevices", "Notes"));

            await using var db = new RadiusDbContext(options);
            await SchemaUpgrader.ApplyAsync(db, NullLogger.Instance);

            Assert.True(await HasColumnAsync(connection, "ClientDevices", "Notes"));
            Assert.True(await HasColumnAsync(connection, "NetworkAccessServers", "Notes"));
            Assert.True(await HasColumnAsync(connection, "VlanDefinitions", "Notes"));
            Assert.True(await HasColumnAsync(connection, "VlanDefinitions", "Group"));
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task QueryingNotesFailsBeforeUpgradeAndWorksAfter()
    {
        var (connection, options) = await LegacyDatabaseAsync();
        try
        {
            await using (var stale = new RadiusDbContext(options))
            {
                // The model expects Notes, but the legacy table lacks it — the exact production symptom.
                await Assert.ThrowsAsync<SqliteException>(() => stale.ClientDevices.ToListAsync());
            }

            await using (var db = new RadiusDbContext(options))
            {
                await SchemaUpgrader.ApplyAsync(db, NullLogger.Instance);
            }

            await using (var upgraded = new RadiusDbContext(options))
            {
                upgraded.ClientDevices.Add(new ClientDevice
                {
                    Name = "aa:bb:cc:dd:ee:ff",
                    Description = "Office Lamp",
                    Notes = "on the desk by the window",
                    VlanDefinitionId = (await SeedVlanAsync(upgraded)).Id
                });
                await upgraded.SaveChangesAsync();

                var saved = await upgraded.ClientDevices.SingleAsync();
                Assert.Equal("on the desk by the window", saved.Notes);
            }
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task IsIdempotentAndANoOpOnAFreshDatabase()
    {
        await using var fixture = await TestDatabase.CreateAsync();

        // A fresh database already has the columns; applying (twice) must not throw or duplicate them.
        await SchemaUpgrader.ApplyAsync(fixture.Db, NullLogger.Instance);
        await SchemaUpgrader.ApplyAsync(fixture.Db, NullLogger.Instance);

        var vlan = await fixture.AddVlanAsync("Default", 10);
        fixture.Db.ClientDevices.Add(new ClientDevice { Name = "a", VlanDefinitionId = vlan.Id, Notes = "ok" });
        await fixture.Db.SaveChangesAsync();

        Assert.Equal("ok", (await fixture.Db.ClientDevices.SingleAsync()).Notes);
    }

    private static async Task<VlanDefinition> SeedVlanAsync(RadiusDbContext db)
    {
        var vlan = new VlanDefinition { Name = "Default", VlanId = 10 };
        db.VlanDefinitions.Add(vlan);
        await db.SaveChangesAsync();
        return vlan;
    }
}
