using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimpleRadius.Data;
using SimpleRadius.Models;

namespace SimpleRadius.Tests;

/// <summary>
/// An isolated SQLite database backed by a private in-memory connection, so each test gets the real
/// schema — including the unique indexes the policy code relies on — without touching disk.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private TestDatabase(SqliteConnection connection, RadiusDbContext db)
    {
        _connection = connection;
        Db = db;
    }

    public RadiusDbContext Db { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<RadiusDbContext>()
            .UseSqlite(connection)
            .Options;

        var db = new RadiusDbContext(options);
        await db.Database.EnsureCreatedAsync();

        return new TestDatabase(connection, db);
    }

    public async Task<VlanDefinition> AddVlanAsync(string name, int vlanId)
    {
        var vlan = new VlanDefinition { Name = name, VlanId = vlanId };
        Db.VlanDefinitions.Add(vlan);
        await Db.SaveChangesAsync();
        return vlan;
    }

    public async Task<NetworkAccessServer> AddNasAsync(
        string ipAddress = "192.168.1.1",
        string sharedSecret = "secret123",
        bool accountingEnabled = true,
        bool isEnabled = true)
    {
        var nas = new NetworkAccessServer
        {
            Name = $"NAS {ipAddress}",
            IpAddress = ipAddress,
            SharedSecret = sharedSecret,
            AccountingEnabled = accountingEnabled,
            IsEnabled = isEnabled
        };

        Db.NetworkAccessServers.Add(nas);
        await Db.SaveChangesAsync();
        return nas;
    }

    public async Task<ClientDevice> AddClientAsync(string name, VlanDefinition vlan, bool isEnabled = true)
    {
        var client = new ClientDevice
        {
            Name = name,
            VlanDefinitionId = vlan.Id,
            IsEnabled = isEnabled
        };

        Db.ClientDevices.Add(client);
        await Db.SaveChangesAsync();
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
