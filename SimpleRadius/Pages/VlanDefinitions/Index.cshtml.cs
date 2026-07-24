using System.Linq.Expressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SimpleRadius.Data;
using SimpleRadius.Models;
using SimpleRadius.Services;

namespace SimpleRadius.Pages.VlanDefinitions;

public class IndexModel : PageModel
{
    private static readonly Dictionary<string, Expression<Func<VlanDefinition, object?>>> SortColumns = new()
    {
        ["vlan"] = v => v.VlanId,
        ["name"] = v => v.Name,
        ["description"] = v => v.Description
    };

    private readonly RadiusDbContext _db;
    private readonly SettingsService _settings;

    public IndexModel(RadiusDbContext db, SettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    public List<VlanDefinition> Vlans { get; private set; } = [];

    public Dictionary<int, int> ClientCounts { get; private set; } = [];

    public int DefaultVlanId { get; private set; }

    public TableSort Sort { get; private set; } = new(null, false, "vlan");

    [BindProperty(SupportsGet = true, Name = "sort")]
    public string? SortColumn { get; set; }

    [BindProperty(SupportsGet = true, Name = "desc")]
    public bool Descending { get; set; }

    [BindProperty]
    public VlanDefinition Input { get; set; } = new();

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
        if (await _db.VlanDefinitions.AnyAsync(v => v.Name == Input.Name))
        {
            ModelState.AddModelError("Input.Name", "A VLAN with this name already exists.");
        }

        if (await _db.VlanDefinitions.AnyAsync(v => v.VlanId == Input.VlanId))
        {
            ModelState.AddModelError("Input.VlanId", "This VLAN ID is already defined.");
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        Input.CreatedUtc = DateTime.UtcNow;
        Input.UpdatedUtc = Input.CreatedUtc;
        _db.VlanDefinitions.Add(Input);
        await _db.SaveChangesAsync();

        StatusMessage = $"Added VLAN {Input.Name} ({Input.VlanId}).";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var vlan = await _db.VlanDefinitions.FindAsync(id);
        if (vlan is null)
        {
            return RedirectToPage();
        }

        if (await _db.ClientDevices.AnyAsync(c => c.VlanDefinitionId == id))
        {
            ErrorMessage = $"{vlan.Name} still has clients assigned to it. Move them to another VLAN first.";
            return RedirectToPage();
        }

        var settings = await _settings.GetReadOnlyAsync();
        if (vlan.VlanId == settings.DefaultVlanId)
        {
            ErrorMessage = $"{vlan.Name} is the default VLAN. Choose a different default on the settings page first.";
            return RedirectToPage();
        }

        _db.VlanDefinitions.Remove(vlan);
        await _db.SaveChangesAsync();

        StatusMessage = $"Deleted VLAN {vlan.Name}.";
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        DefaultVlanId = (await _settings.GetReadOnlyAsync()).DefaultVlanId;
        Sort = new TableSort(SortColumn, Descending, "vlan");

        Vlans = await Sort.Apply(_db.VlanDefinitions.AsNoTracking(), SortColumns).ToListAsync();

        ClientCounts = await _db.ClientDevices
            .GroupBy(c => c.VlanDefinitionId)
            .Select(g => new { VlanDefinitionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.VlanDefinitionId, g => g.Count);
    }
}
