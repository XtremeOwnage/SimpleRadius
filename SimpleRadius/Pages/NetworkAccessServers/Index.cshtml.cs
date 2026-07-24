using System.Linq.Expressions;
using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SimpleRadius.Data;
using SimpleRadius.Models;
using SimpleRadius.Services;

namespace SimpleRadius.Pages.NetworkAccessServers;

public class IndexModel : PageModel
{
    private static readonly Dictionary<string, Expression<Func<NetworkAccessServer, object?>>> SortColumns = new()
    {
        ["name"] = n => n.Name,
        ["ip"] = n => n.IpAddress,
        ["accounting"] = n => n.AccountingEnabled,
        ["seen"] = n => n.LastSeenUtc,
        ["state"] = n => n.IsEnabled
    };

    private readonly RadiusDbContext _db;
    private readonly RadiusServerSettings _startup;
    private readonly SettingsService _settings;

    public IndexModel(RadiusDbContext db, RadiusServerSettings startup, SettingsService settings)
    {
        _db = db;
        _startup = startup;
        _settings = settings;
    }

    public List<NetworkAccessServer> Servers { get; private set; } = [];

    public int AuthPort => _startup.AuthPort;

    public int AcctPort => _startup.AcctPort;

    public bool AutoRegisterEnabled { get; private set; }

    public TableSort Sort { get; private set; } = new(null, false, "name");

    [BindProperty(SupportsGet = true, Name = "sort")]
    public string? SortColumn { get; set; }

    [BindProperty(SupportsGet = true, Name = "desc")]
    public bool Descending { get; set; }

    [BindProperty]
    public NetworkAccessServer Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!IPAddress.TryParse(Input.IpAddress.Trim(), out var address))
        {
            ModelState.AddModelError("Input.IpAddress", "Enter the IP address the router sends RADIUS requests from.");
        }
        else
        {
            Input.IpAddress = address.ToString();
            if (await _db.NetworkAccessServers.AnyAsync(n => n.IpAddress == Input.IpAddress))
            {
                ModelState.AddModelError("Input.IpAddress", "A NAS with this address is already configured.");
            }
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        Input.IsAutoRegistered = false;
        Input.CreatedUtc = DateTime.UtcNow;
        Input.UpdatedUtc = Input.CreatedUtc;
        _db.NetworkAccessServers.Add(Input);
        await _db.SaveChangesAsync();

        StatusMessage = $"Added NAS {Input.Name}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var nas = await _db.NetworkAccessServers.FindAsync(id);
        if (nas is null)
        {
            return RedirectToPage();
        }

        nas.IsEnabled = !nas.IsEnabled;
        nas.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        StatusMessage = $"{nas.Name} is now {(nas.IsEnabled ? "enabled" : "disabled")}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var nas = await _db.NetworkAccessServers.FindAsync(id);
        if (nas is null)
        {
            return RedirectToPage();
        }

        _db.NetworkAccessServers.Remove(nas);
        await _db.SaveChangesAsync();

        StatusMessage = $"Deleted NAS {nas.Name}. Requests from {nas.IpAddress} are now ignored.";
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        AutoRegisterEnabled = (await _settings.GetReadOnlyAsync()).AutoRegisterUnknownNas;
        Sort = new TableSort(SortColumn, Descending, "name");

        Servers = await Sort.Apply(_db.NetworkAccessServers.AsNoTracking(), SortColumns).ToListAsync();
    }
}
