using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleRadius.Data;
using SimpleRadius.Models;
using SimpleRadius.Radius;
using SimpleRadius.Services;

namespace SimpleRadius.Tests;

/// <summary>Exercises the real UDP listener end to end over the loopback interface.</summary>
public class RadiusServerProcessTests : IAsyncLifetime
{
    private const string Secret = "secret123";

    private string _databasePath = string.Empty;
    private ServiceProvider _services = null!;
    private RadiusServerProcess _server = null!;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"simple-radius-test-{Guid.NewGuid():N}.db");

        var settings = new RadiusServerSettings
        {
            // Port 0 lets the OS pick free ports so the test never collides with a real server.
            AuthPort = 0,
            AcctPort = 0,
            ListenAddress = "127.0.0.1",
            DatabasePath = _databasePath
        };

        var services = new ServiceCollection();
        services.AddSingleton(settings);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddDbContextFactory<RadiusDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
        services.AddSingleton<RadiusServerProcess>();
        _services = services.BuildServiceProvider();

        var factory = _services.GetRequiredService<IDbContextFactory<RadiusDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.VlanDefinitions.Add(new VlanDefinition { Name = "Default", VlanId = 10 });
            db.NetworkAccessServers.Add(new NetworkAccessServer
            {
                Name = "Loopback",
                IpAddress = "127.0.0.1",
                SharedSecret = Secret
            });
            await db.SaveChangesAsync();
        }

        _server = _services.GetRequiredService<RadiusServerProcess>();
        await _server.StartAsync(CancellationToken.None);

        // StartAsync returns as soon as ExecuteAsync yields, so wait for the sockets to report their ports.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (_server.AuthPort == 0 || _server.AcctPort == 0)
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync(CancellationToken.None);
        await _services.DisposeAsync();

        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(_databasePath)!, Path.GetFileName(_databasePath) + "*"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A WAL sidecar may still be held briefly; the temp directory takes care of it.
            }
        }
    }

    [Fact]
    public async Task UnknownDeviceIsAcceptedOntoTheDefaultVlanOverTheWire()
    {
        using var client = new UdpClient(0, AddressFamily.InterNetwork);
        var endpoint = new IPEndPoint(IPAddress.Loopback, _server.AuthPort);

        var request = RadiusRequestBuilder.BuildAccessRequest(7, Secret,
            [RadiusAttribute.FromString(RadiusAttributeType.UserName, "aa:bb:cc:dd:ee:ff")]);

        await client.SendAsync(request, endpoint);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reply = await client.ReceiveAsync(timeout.Token);

        Assert.True(RadiusPacket.TryParse(reply.Buffer, out var packet, out _));
        Assert.Equal(RadiusCode.AccessAccept, packet!.Code);
        Assert.Equal(7, packet.Identifier);
        Assert.True(packet.TryGetAttribute(RadiusAttributeType.TunnelPrivateGroupId, out var groupId));
        Assert.Equal("10", Encoding.UTF8.GetString(groupId.Value[1..]));
    }

    [Fact]
    public async Task AccountingStartIsAcknowledgedAndPersistedOverTheWire()
    {
        using var client = new UdpClient(0, AddressFamily.InterNetwork);
        var endpoint = new IPEndPoint(IPAddress.Loopback, _server.AcctPort);

        var request = RadiusRequestBuilder.BuildAccountingRequest(8, Secret,
        [
            RadiusAttribute.FromString(RadiusAttributeType.UserName, "aa:bb:cc:dd:ee:ff"),
            RadiusAttribute.FromString(RadiusAttributeType.AcctSessionId, "wire-session"),
            RadiusAttribute.FromUInt32(RadiusAttributeType.AcctStatusType, (uint)AcctStatusType.Start)
        ]);

        await client.SendAsync(request, endpoint);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reply = await client.ReceiveAsync(timeout.Token);

        Assert.True(RadiusPacket.TryParse(reply.Buffer, out var packet, out _));
        Assert.Equal(RadiusCode.AccountingResponse, packet!.Code);

        var factory = _services.GetRequiredService<IDbContextFactory<RadiusDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var session = await db.AccountingSessions.SingleAsync(s => s.SessionId == "wire-session");
        Assert.True(session.IsActive);
    }
}
