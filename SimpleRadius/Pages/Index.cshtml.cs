using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SimpleRadius.Data;
using SimpleRadius.Models;
using SimpleRadius.Radius;
using SimpleRadius.Services;

namespace SimpleRadius.Pages;

public class IndexModel : PageModel
{
    private readonly RadiusDbContext _db;
    private readonly RadiusServerSettings _startup;
    private readonly SettingsService _settings;
    private readonly RadiusServerProcess _server;

    public IndexModel(
        RadiusDbContext db,
        RadiusServerSettings startup,
        SettingsService settings,
        RadiusServerProcess server)
    {
        _db = db;
        _startup = startup;
        _settings = settings;
        _server = server;
    }

    public int VlanCount { get; private set; }
    public int ClientCount { get; private set; }
    public int DisabledClientCount { get; private set; }
    public int NasCount { get; private set; }
    public int ActiveSessionCount { get; private set; }
    public long BytesTransferred { get; private set; }

    public List<ClientDevice> RecentClients { get; private set; } = [];
    public List<AccountingSession> RecentSessions { get; private set; } = [];

    public RadiusServerSettings Startup => _startup;

    public ServerSettings Settings { get; private set; } = new();

    /// <summary>Ports actually bound by the listener, which is what a router must be pointed at.</summary>
    public int AuthPort => _server.AuthPort == 0 ? _startup.AuthPort : _server.AuthPort;

    public int AcctPort => _server.AcctPort == 0 ? _startup.AcctPort : _server.AcctPort;

    public async Task OnGetAsync()
    {
        Settings = await _settings.GetReadOnlyAsync();

        VlanCount = await _db.VlanDefinitions.CountAsync();
        ClientCount = await _db.ClientDevices.CountAsync();
        DisabledClientCount = await _db.ClientDevices.CountAsync(c => !c.IsEnabled);
        NasCount = await _db.NetworkAccessServers.CountAsync(n => n.IsEnabled);
        ActiveSessionCount = await _db.AccountingSessions.CountAsync(s => s.IsActive);

        BytesTransferred = await _db.AccountingSessions
            .SumAsync(s => s.BytesIn + s.BytesOut);

        RecentClients = await _db.ClientDevices
            .AsNoTracking()
            .Include(c => c.VlanDefinition)
            .Where(c => c.LastSeenUtc != null)
            .OrderByDescending(c => c.LastSeenUtc)
            .Take(10)
            .ToListAsync();

        RecentSessions = await _db.AccountingSessions
            .AsNoTracking()
            .OrderByDescending(s => s.LastUpdateTime)
            .Take(10)
            .ToListAsync();
    }

    public string FormatMac(string name) => MacAddress.Format(name, Settings.MacAddressFormat);
}
