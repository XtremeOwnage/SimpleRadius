using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using SimpleRadius.Data;
using SimpleRadius.Models;
using SimpleRadius.Radius;

namespace SimpleRadius.Pages.ClientDevices;

public class EditModel : PageModel
{
    private readonly RadiusDbContext _db;

    public EditModel(RadiusDbContext db)
    {
        _db = db;
    }

    [BindProperty]
    public ClientDevice Input { get; set; } = new();

    public List<SelectListItem> VlanOptions { get; private set; } = [];

    public ClientDevice? Existing { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var client = await _db.ClientDevices.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (client is null)
        {
            return NotFound();
        }

        Input = client;
        Existing = client;
        await LoadVlansAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var client = await _db.ClientDevices.FirstOrDefaultAsync(c => c.Id == Input.Id);
        if (client is null)
        {
            return NotFound();
        }

        Existing = client;
        Input.Name = MacAddress.Normalize(Input.Name ?? string.Empty);

        if (string.IsNullOrWhiteSpace(Input.Name))
        {
            ModelState.AddModelError("Input.Name", "Enter a MAC address or user name.");
        }
        else if (await _db.ClientDevices.AnyAsync(c => c.Id != Input.Id && c.Name == Input.Name))
        {
            ModelState.AddModelError("Input.Name", "Another client already uses this identity.");
        }

        if (!await _db.VlanDefinitions.AnyAsync(v => v.Id == Input.VlanDefinitionId))
        {
            ModelState.AddModelError("Input.VlanDefinitionId", "Choose a VLAN.");
        }

        if (!ModelState.IsValid)
        {
            await LoadVlansAsync();
            return Page();
        }

        client.Name = Input.Name;
        client.Description = Input.Description;
        client.VlanDefinitionId = Input.VlanDefinitionId;
        client.IsEnabled = Input.IsEnabled;
        client.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        StatusMessage = $"Updated {client.Name}. The new VLAN applies at its next authentication.";
        return RedirectToPage("Index");
    }

    private async Task LoadVlansAsync()
    {
        VlanOptions = await _db.VlanDefinitions
            .AsNoTracking()
            .OrderBy(v => v.VlanId)
            .Select(v => new SelectListItem($"{v.Name} ({v.VlanId})", v.Id.ToString()))
            .ToListAsync();
    }
}
