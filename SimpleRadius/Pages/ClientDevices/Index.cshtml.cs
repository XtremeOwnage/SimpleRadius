using System.Linq.Expressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using SimpleRadius.Data;
using SimpleRadius.Models;
using SimpleRadius.Radius;
using SimpleRadius.Services;

namespace SimpleRadius.Pages.ClientDevices;

public class IndexModel : PageModel
{
    private static readonly Dictionary<string, Expression<Func<ClientDevice, object?>>> SortColumns = new()
    {
        ["name"] = c => c.Name,
        ["description"] = c => c.Description,
        ["vlan"] = c => c.VlanDefinition!.VlanId,
        ["seen"] = c => c.LastSeenUtc,
        ["auths"] = c => c.AuthenticationCount,
        ["state"] = c => c.IsEnabled
    };

    private readonly RadiusDbContext _db;
    private readonly SettingsService _settings;

    public IndexModel(RadiusDbContext db, SettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    public List<ClientDevice> Clients { get; private set; } = [];

    public List<SelectListItem> VlanOptions { get; private set; } = [];

    public MacAddressFormat MacFormat { get; private set; } = MacAddressFormat.ColonLower;

    public TableSort Sort { get; private set; } = new(null, false, "name");

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true, Name = "sort")]
    public string? SortColumn { get; set; }

    [BindProperty(SupportsGet = true, Name = "desc")]
    public bool Descending { get; set; }

    [BindProperty]
    public ClientDevice Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Input.Name = MacAddress.Normalize(Input.Name ?? string.Empty);

        if (string.IsNullOrWhiteSpace(Input.Name))
        {
            ModelState.AddModelError("Input.Name", "Enter a MAC address or user name.");
        }
        else if (await _db.ClientDevices.AnyAsync(c => c.Name == Input.Name))
        {
            ModelState.AddModelError("Input.Name", "This client already exists.");
        }

        if (!await _db.VlanDefinitions.AnyAsync(v => v.Id == Input.VlanDefinitionId))
        {
            ModelState.AddModelError("Input.VlanDefinitionId", "Choose a VLAN.");
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        Input.IsAutoCreated = false;
        Input.CreatedUtc = DateTime.UtcNow;
        Input.UpdatedUtc = Input.CreatedUtc;
        _db.ClientDevices.Add(Input);
        await _db.SaveChangesAsync();

        StatusMessage = $"Added client {FormatMac(Input.Name)}.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostToggleAsync(int id)
    {
        var client = await _db.ClientDevices.FindAsync(id);
        if (client is null)
        {
            return RedirectToPage();
        }

        client.IsEnabled = !client.IsEnabled;
        client.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        MacFormat = (await _settings.GetReadOnlyAsync()).MacAddressFormat;
        StatusMessage = $"{FormatMac(client.Name)} is now {(client.IsEnabled ? "enabled" : "disabled")}.";
        return RedirectToPage(new { Search, sort = SortColumn, desc = Descending });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var client = await _db.ClientDevices.FindAsync(id);
        if (client is null)
        {
            return RedirectToPage();
        }

        _db.ClientDevices.Remove(client);
        await _db.SaveChangesAsync();

        MacFormat = (await _settings.GetReadOnlyAsync()).MacAddressFormat;
        StatusMessage = $"Deleted client {FormatMac(client.Name)}. It will be recreated on the default VLAN if it authenticates again.";
        return RedirectToPage(new { Search, sort = SortColumn, desc = Descending });
    }

    public string FormatMac(string name) => MacAddress.Format(name, MacFormat);

    private async Task LoadAsync()
    {
        var settings = await _settings.GetReadOnlyAsync();
        MacFormat = settings.MacAddressFormat;
        Sort = new TableSort(SortColumn, Descending, "name");

        var query = _db.ClientDevices
            .AsNoTracking()
            .Include(c => c.VlanDefinition)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(Search))
        {
            // LIKE is case insensitive in SQLite, unlike the instr() that Contains compiles to. The second
            // pattern compares digits only, so a search works whatever separator was typed.
            var pattern = SearchPattern.For(Search);
            var barePattern = SearchPattern.ForDigitsOnly(Search);
            query = query.Where(c =>
                EF.Functions.Like(c.Name, pattern)
                || EF.Functions.Like(c.Name.Replace(":", ""), barePattern)
                || (c.Description != null && EF.Functions.Like(c.Description, pattern)));
        }

        Clients = await Sort.Apply(query, SortColumns).ToListAsync();

        var vlans = await _db.VlanDefinitions
            .AsNoTracking()
            .OrderBy(v => v.VlanId)
            .ToListAsync();

        VlanOptions = vlans
            .Select(v => new SelectListItem($"{v.Name} ({v.VlanId})", v.Id.ToString()))
            .ToList();

        if (Input.VlanDefinitionId == 0)
        {
            // Preselect the default VLAN so the add form matches what the fallback would do.
            var preferred = vlans.FirstOrDefault(v => v.VlanId == settings.DefaultVlanId) ?? vlans.FirstOrDefault();
            Input.VlanDefinitionId = preferred?.Id ?? 0;
        }
    }
}
