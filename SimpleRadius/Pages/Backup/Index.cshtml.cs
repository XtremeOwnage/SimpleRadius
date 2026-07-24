using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using SimpleRadius.Models;
using SimpleRadius.Services;

namespace SimpleRadius.Pages.Backup;

public class IndexModel : PageModel
{
    private const long MaxUploadBytes = 5 * 1024 * 1024;

    private readonly BackupService _backup;

    public IndexModel(BackupService backup)
    {
        _backup = backup;
    }

    [BindProperty]
    public IFormFile? Upload { get; set; }

    [BindProperty]
    public bool ImportSettings { get; set; }

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnGetExportAsync(string format, CancellationToken cancellationToken)
    {
        var backupFormat = string.Equals(format, "yaml", StringComparison.OrdinalIgnoreCase)
            ? BackupFormat.Yaml
            : BackupFormat.Json;

        var document = await _backup.ExportAsync(cancellationToken);
        var content = _backup.Serialize(document, backupFormat);

        var extension = backupFormat == BackupFormat.Yaml ? "yaml" : "json";
        var contentType = backupFormat == BackupFormat.Yaml ? "application/x-yaml" : "application/json";
        var fileName = $"simpleradius-config-{DateTime.UtcNow:yyyyMMdd-HHmmss}.{extension}";

        return File(Encoding.UTF8.GetBytes(content), contentType, fileName);
    }

    public async Task<IActionResult> OnPostImportAsync(CancellationToken cancellationToken)
    {
        if (Upload is null || Upload.Length == 0)
        {
            ErrorMessage = "Choose a JSON or YAML file to import.";
            return RedirectToPage();
        }

        if (Upload.Length > MaxUploadBytes)
        {
            ErrorMessage = "That file is larger than the 5 MB limit.";
            return RedirectToPage();
        }

        string content;
        using (var reader = new StreamReader(Upload.OpenReadStream()))
        {
            content = await reader.ReadToEndAsync(cancellationToken);
        }

        BackupDocument document;
        try
        {
            document = BackupService.Parse(content);
        }
        catch (BackupFormatException ex)
        {
            ErrorMessage = ex.Message;
            return RedirectToPage();
        }

        var result = await _backup.ImportAsync(document, new ImportOptions { ImportSettings = ImportSettings }, cancellationToken);

        var summary = new StringBuilder($"Imported: {result.TotalChanged} record(s) changed ");
        summary.Append($"(VLANs +{result.VlansAdded}/~{result.VlansUpdated}, ");
        summary.Append($"clients +{result.ClientsAdded}/~{result.ClientsUpdated}, ");
        summary.Append($"NAS +{result.NasAdded}/~{result.NasUpdated})");
        if (result.SettingsApplied)
        {
            summary.Append(", settings applied");
        }
        summary.Append('.');

        if (result.Warnings.Count > 0)
        {
            summary.Append($" {result.Warnings.Count} warning(s): ");
            summary.Append(string.Join(" ", result.Warnings.Take(5)));
            if (result.Warnings.Count > 5)
            {
                summary.Append(" …");
            }
        }

        StatusMessage = summary.ToString();
        return RedirectToPage();
    }
}
