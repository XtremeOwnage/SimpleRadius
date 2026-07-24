using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using SimpleRadius.Data;
using SimpleRadius.Models;
using SimpleRadius.Radius;
using SimpleRadius.Services;

namespace SimpleRadius.Pages.Settings;

public class IndexModel : PageModel
{
    private readonly RadiusDbContext _db;
    private readonly SettingsService _settings;
    private readonly RadiusServerSettings _startup;
    private readonly RadiusServerProcess _server;

    public IndexModel(
        RadiusDbContext db,
        SettingsService settings,
        RadiusServerSettings startup,
        RadiusServerProcess server)
    {
        _db = db;
        _settings = settings;
        _startup = startup;
        _server = server;
    }

    [BindProperty]
    public ServerSettings Input { get; set; } = new();

    public List<SelectListItem> MacFormatOptions { get; private set; } = [];

    public List<VlanDefinition> Vlans { get; private set; } = [];

    /// <summary>Startup values, shown read-only because changing them needs a restart.</summary>
    public RadiusServerSettings Startup => _startup;

    public int AuthPort => _server.AuthPort == 0 ? _startup.AuthPort : _server.AuthPort;

    public int AcctPort => _server.AcctPort == 0 ? _startup.AcctPort : _server.AcctPort;

    /// <summary>A preview of the attributes an Access-Accept would carry under the current settings.</summary>
    public IReadOnlyList<RadiusAttribute> PreviewAttributes { get; private set; } = [];

    public int PreviewVlanId { get; private set; }

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync()
    {
        Input = await _settings.GetReadOnlyAsync();
        await LoadAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (Input.SendEgressVlanId is false && Input.SendTunnelAttributes is false)
        {
            ModelState.AddModelError(
                "Input.SendTunnelAttributes",
                "Turn on tunnel attributes or Egress-VLANID, otherwise an accepted device is never given a VLAN.");
        }

        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        await _settings.SaveAsync(Input);

        StatusMessage = "Settings saved. They apply to the next RADIUS request; no restart needed.";
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        MacFormatOptions = Enum.GetValues<MacAddressFormat>()
            .Select(f => new SelectListItem(MacAddress.Sample(f), f.ToString()))
            .ToList();

        Vlans = await _db.VlanDefinitions
            .AsNoTracking()
            .OrderBy(v => v.VlanId)
            .ToListAsync();

        PreviewVlanId = Vlans.FirstOrDefault(v => v.VlanId == Input.DefaultVlanId)?.VlanId ?? Input.DefaultVlanId;
        PreviewAttributes = AccessAcceptAttributes.Build(PreviewVlanId, Input);
    }

    /// <summary>Renders an attribute value the way a router's log would show it.</summary>
    public static string Describe(RadiusAttribute attribute)
    {
        var name = attribute.Type switch
        {
            RadiusAttributeType.ServiceType => "Service-Type",
            RadiusAttributeType.TunnelType => "Tunnel-Type",
            RadiusAttributeType.TunnelMediumType => "Tunnel-Medium-Type",
            RadiusAttributeType.TunnelPrivateGroupId => "Tunnel-Private-Group-Id",
            RadiusAttributeType.EgressVlanId => "Egress-VLANID",
            RadiusAttributeType.AcctInterimInterval => "Acct-Interim-Interval",
            _ => $"Attribute {attribute.Type}"
        };

        return $"{name} = {DescribeValue(attribute)}";
    }

    private static string DescribeValue(RadiusAttribute attribute)
    {
        // Tagged attributes carry a tag byte the value does not include.
        var tagged = attribute.Type is RadiusAttributeType.TunnelType
            or RadiusAttributeType.TunnelMediumType
            or RadiusAttributeType.TunnelPrivateGroupId;

        if (attribute.Type == RadiusAttributeType.TunnelPrivateGroupId)
        {
            return tagged && attribute.Value.Length > 0 && attribute.Value[0] <= 0x1F
                ? $"\"{System.Text.Encoding.UTF8.GetString(attribute.Value[1..])}\" (tag {attribute.Value[0]})"
                : $"\"{attribute.AsString()}\"";
        }

        if (attribute.Value.Length == 4)
        {
            var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(attribute.Value);
            return tagged && attribute.Value[0] != 0
                ? $"{value & 0x00FFFFFF} (tag {attribute.Value[0]})"
                : value.ToString();
        }

        return attribute.AsString();
    }
}
