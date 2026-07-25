using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using SimpleRadius.Data;
using SimpleRadius.Models;
using SimpleRadius.Services;

namespace SimpleRadius.Pages.SsidRules;

public class IndexModel : PageModel
{
    private readonly RadiusDbContext _db;
    private readonly SettingsService _settings;

    public IndexModel(RadiusDbContext db, SettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    public List<SsidVlanRule> Rules { get; private set; } = [];

    public List<SelectListItem> VlanOptions { get; private set; } = [];

    public int DefaultVlanId { get; private set; }

    [BindProperty]
    public SsidVlanRule Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        Input.Ssid = Input.Ssid?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(Input.Ssid))
        {
            ModelState.AddModelError("Input.Ssid", "Enter an SSID.");
        }
        else if (await _db.SsidVlanRules.AnyAsync(r => r.Ssid == Input.Ssid))
        {
            ModelState.AddModelError("Input.Ssid", "A rule for this SSID already exists.");
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

        Input.CreatedUtc = DateTime.UtcNow;
        Input.UpdatedUtc = Input.CreatedUtc;
        _db.SsidVlanRules.Add(Input);
        await _db.SaveChangesAsync();

        StatusMessage = $"New devices on SSID '{Input.Ssid}' will default to the chosen VLAN.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var rule = await _db.SsidVlanRules.FindAsync(id);
        if (rule is not null)
        {
            _db.SsidVlanRules.Remove(rule);
            await _db.SaveChangesAsync();
            StatusMessage = $"Removed the rule for SSID '{rule.Ssid}'.";
        }

        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        DefaultVlanId = (await _settings.GetReadOnlyAsync()).DefaultVlanId;

        Rules = await _db.SsidVlanRules
            .AsNoTracking()
            .Include(r => r.VlanDefinition)
            .OrderBy(r => r.Ssid)
            .ToListAsync();

        VlanOptions = await _db.VlanDefinitions
            .AsNoTracking()
            .OrderBy(v => v.VlanId)
            .Select(v => new SelectListItem($"{v.Name} ({v.VlanId})", v.Id.ToString()))
            .ToListAsync();
    }
}
