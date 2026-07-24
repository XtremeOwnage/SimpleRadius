using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SimpleRadius.Data;
using SimpleRadius.Models;

namespace SimpleRadius.Pages.NetworkAccessServers;

public class EditModel : PageModel
{
    private readonly RadiusDbContext _db;

    public EditModel(RadiusDbContext db)
    {
        _db = db;
    }

    [BindProperty]
    public NetworkAccessServer Input { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var nas = await _db.NetworkAccessServers.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id);
        if (nas is null)
        {
            return NotFound();
        }

        Input = nas;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var nas = await _db.NetworkAccessServers.FirstOrDefaultAsync(n => n.Id == Input.Id);
        if (nas is null)
        {
            return NotFound();
        }

        if (!IPAddress.TryParse(Input.IpAddress.Trim(), out var address))
        {
            ModelState.AddModelError("Input.IpAddress", "Enter a valid IP address.");
        }
        else
        {
            Input.IpAddress = address.ToString();
            if (await _db.NetworkAccessServers.AnyAsync(n => n.Id != Input.Id && n.IpAddress == Input.IpAddress))
            {
                ModelState.AddModelError("Input.IpAddress", "Another NAS already uses this address.");
            }
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        nas.Name = Input.Name;
        nas.IpAddress = Input.IpAddress;
        nas.SharedSecret = Input.SharedSecret;
        nas.AccountingEnabled = Input.AccountingEnabled;
        nas.IsEnabled = Input.IsEnabled;
        nas.IsAutoRegistered = false;
        nas.UpdatedUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        StatusMessage = $"Updated NAS {nas.Name}.";
        return RedirectToPage("Index");
    }
}
