using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using SimpleRadius.Data;
using SimpleRadius.Models;

namespace SimpleRadius.Services;

/// <summary>
/// Hosts the two UDP listeners (authentication and accounting) for the lifetime of the web application.
/// Each datagram is handled on its own task with its own DbContext so a slow write cannot stall the socket.
/// </summary>
public sealed class RadiusServerProcess : BackgroundService
{
    private readonly IDbContextFactory<RadiusDbContext> _dbFactory;
    private readonly RadiusServerSettings _settings;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RadiusServerProcess> _logger;

    public RadiusServerProcess(
        IDbContextFactory<RadiusDbContext> dbFactory,
        RadiusServerSettings settings,
        ILoggerFactory loggerFactory)
    {
        _dbFactory = dbFactory;
        _settings = settings;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RadiusServerProcess>();
    }

    /// <summary>Ports actually bound, which differ from the configured ports when port 0 is requested.</summary>
    public int AuthPort { get; private set; }

    public int AcctPort { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var address = IPAddress.TryParse(_settings.ListenAddress, out var parsed) ? parsed : IPAddress.Any;

        using var authSocket = Bind(address, _settings.AuthPort, "authentication");
        using var acctSocket = Bind(address, _settings.AcctPort, "accounting");

        AuthPort = ((IPEndPoint)authSocket.Client.LocalEndPoint!).Port;
        AcctPort = ((IPEndPoint)acctSocket.Client.LocalEndPoint!).Port;

        _logger.LogInformation(
            "RADIUS listening on {Address}: authentication UDP {AuthPort}, accounting UDP {AcctPort}",
            address,
            AuthPort,
            AcctPort);

        await Task.WhenAll(
            ReceiveLoopAsync(authSocket, RadiusListenerRole.Authentication, stoppingToken),
            ReceiveLoopAsync(acctSocket, RadiusListenerRole.Accounting, stoppingToken));
    }

    private UdpClient Bind(IPAddress address, int port, string role)
    {
        try
        {
            return new UdpClient(new IPEndPoint(address, port));
        }
        catch (SocketException ex)
        {
            _logger.LogCritical(
                ex,
                "Cannot bind the {Role} listener to {Address}:{Port}. Another RADIUS server may already be running, " +
                "or the port needs elevated privileges. Change RadiusServerSettings in appsettings.json to use a free port.",
                role,
                address,
                port);
            throw;
        }
    }

    private async Task ReceiveLoopAsync(UdpClient socket, RadiusListenerRole role, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await socket.ReceiveAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                // An ICMP port-unreachable from a previous reply surfaces here; keep listening.
                _logger.LogDebug(ex, "Socket error on the {Role} listener", role);
                continue;
            }

            _ = ProcessAsync(socket, received, role, stoppingToken);
        }
    }

    private async Task ProcessAsync(
        UdpClient socket,
        UdpReceiveResult received,
        RadiusListenerRole role,
        CancellationToken stoppingToken)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(stoppingToken);
            var handler = new RadiusRequestHandler(db, _settings, _loggerFactory);

            var response = await handler.HandleAsync(
                received.Buffer,
                received.RemoteEndPoint.Address,
                role,
                stoppingToken);

            if (response is not null)
            {
                await socket.SendAsync(response, received.RemoteEndPoint, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process a {Role} packet from {Source}", role, received.RemoteEndPoint);
        }
    }
}
