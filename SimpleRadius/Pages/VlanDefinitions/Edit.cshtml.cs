using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SimpleRadius.Data;
using SimpleRadius.Models;

namespace SimpleRadius.Pages.VlanDefinitions;

public class EditModel : PageModel
{
    private readonly RadiusDbContext _db;

    public EditModel(RadiusDbContext db)
    {
        _db = db;
    }

    [BindProperty]
    public VlanDefinition Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var vlan = await _db.VlanDefinitions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id);
        if (vlan is null)
        {
            return NotFound();
        }

        Input = vlan;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var vlan = await _db.VlanDefinitions.FirstOrDefaultAsync(v => v.Id == Input.Id);
        if (vlan is null)
        {
            return NotFound();
        }

        if (await _db.VlanDefinitions.AnyAsync(v => v.Id != Input.Id && v.Name == Input.Name))
        {
            ModelState.AddModelError("Input.Name", "A VLAN with this name already exists.");
        }

        if (await _db.VlanDefinitions.AnyAsync(v => v.Id != Input.Id && v.VlanId == Input.VlanId))
        {
            ModelState.AddModelError("Input.VlanId", "This VLAN ID is already defined.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        vlan.Name = Input.Name;
        vlan.VlanId = Input.VlanId;
        vlan.Description = Input.Description;
        vlan.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        StatusMessage = $"Updated VLAN {vlan.Name}. Clients assigned to it use the new ID on their next authentication.";
        return RedirectToPage("Index");
    }
}
