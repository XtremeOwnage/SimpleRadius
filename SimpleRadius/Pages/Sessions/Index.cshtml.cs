using System.Linq.Expressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using SimpleRadius.Data;
using SimpleRadius.Models;
using SimpleRadius.Radius;
using SimpleRadius.Services;

namespace SimpleRadius.Pages.Sessions;

public class IndexModel : PageModel
{
    private const int PageSize = 200;

    private static readonly Dictionary<string, Expression<Func<AccountingSession, object?>>> SortColumns = new()
    {
        ["updated"] = s => s.LastUpdateTime,
        ["client"] = s => s.ClientName,
        ["nas"] = s => s.NasName,
        ["vlan"] = s => s.VlanId,
        ["started"] = s => s.StartTime,
        ["stopped"] = s => s.StopTime,
        ["duration"] = s => s.SessionSeconds,
        ["in"] = s => s.BytesIn,
        ["out"] = s => s.BytesOut,
        ["session"] = s => s.SessionId
    };

    private readonly RadiusDbContext _db;
    private readonly SettingsService _settings;

    public IndexModel(RadiusDbContext db, SettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    public List<AccountingSession> Active { get; private set; } = [];

    public List<AccountingSession> Closed { get; private set; } = [];

    public SessionTotals ActiveTotals { get; private set; } = SessionTotals.Empty;

    public SessionTotals ClosedTotals { get; private set; } = SessionTotals.Empty;

    public List<ClientUsage> TopClients { get; private set; } = [];

    public MacAddressFormat MacFormat { get; private set; } = MacAddressFormat.ColonLower;

    /// <summary>Identity (MAC) to friendly name, for the clients that have one.</summary>
    public Dictionary<string, string> ClientNames { get; private set; } = [];

    public TableSort Sort { get; private set; } = new(null, true, "updated");

    public bool Truncated { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true, Name = "sort")]
    public string? SortColumn { get; set; }

    [BindProperty(SupportsGet = true, Name = "desc")]
    public bool Descending { get; set; } = true;

    [TempData]
    public string? StatusMessage { get; set; }

    public async Task OnGetAsync()
    {
        MacFormat = (await _settings.GetReadOnlyAsync()).MacAddressFormat;
        Sort = new TableSort(SortColumn, Descending, "updated");

        var active = Sort.Apply(Filter(_db.AccountingSessions.AsNoTracking().Where(s => s.IsActive)), SortColumns);
        var closed = Sort.Apply(Filter(_db.AccountingSessions.AsNoTracking().Where(s => !s.IsActive)), SortColumns);

        Active = await active.Take(PageSize).ToListAsync();
        Closed = await closed.Take(PageSize).ToListAsync();

        // Totals come from the database rather than the page, so a capped list still reports real usage.
        ActiveTotals = await TotalsAsync(Filter(_db.AccountingSessions.AsNoTracking().Where(s => s.IsActive)));
        ClosedTotals = await TotalsAsync(Filter(_db.AccountingSessions.AsNoTracking().Where(s => !s.IsActive)));

        Truncated = ActiveTotals.Sessions > Active.Count || ClosedTotals.Sessions > Closed.Count;

        // Grouped into an anonymous type first: SQLite cannot translate a projection straight into a
        // record constructor, and the rows are mapped once they are already materialised.
        var usage = await Filter(_db.AccountingSessions.AsNoTracking())
            .GroupBy(s => s.ClientName)
            .Select(g => new
            {
                ClientName = g.Key,
                Sessions = g.Count(),
                Active = g.Count(s => s.IsActive),
                BytesIn = g.Sum(s => s.BytesIn),
                BytesOut = g.Sum(s => s.BytesOut),
                Seconds = g.Sum(s => s.SessionSeconds)
            })
            .OrderByDescending(u => u.BytesIn + u.BytesOut)
            .Take(10)
            .ToListAsync();

        TopClients = usage
            .Select(u => new ClientUsage(u.ClientName, u.Sessions, u.Active, u.BytesIn, u.BytesOut, u.Seconds))
            .ToList();

        // Resolve the friendly name for everything on screen in one query. Accounting rows only carry the
        // identity (MAC); the readable name lives on the client record.
        var names = Active.Select(s => s.ClientName)
            .Concat(Closed.Select(s => s.ClientName))
            .Concat(TopClients.Select(t => t.ClientName))
            .Distinct()
            .ToList();

        ClientNames = await _db.ClientDevices
            .AsNoTracking()
            .Where(c => names.Contains(c.Name) && c.Description != null && c.Description != "")
            .ToDictionaryAsync(c => c.Name, c => c.Description!);
    }

    public async Task<IActionResult> OnPostClearHistoryAsync()
    {
        var removed = await _db.AccountingSessions.Where(s => !s.IsActive).ExecuteDeleteAsync();

        StatusMessage = $"Cleared {removed} closed session(s).";
        return RedirectToPage();
    }

    private IQueryable<AccountingSession> Filter(IQueryable<AccountingSession> query)
    {
        if (string.IsNullOrWhiteSpace(Search))
        {
            return query;
        }

        // LIKE rather than Contains: EF translates Contains to instr(), which is case sensitive.
        // The second pattern matches on the digits alone, so "aabb" finds "aa:bb:..." whatever was typed.
        var pattern = SearchPattern.For(Search);
        var barePattern = SearchPattern.ForDigitsOnly(Search);

        return query.Where(s =>
            EF.Functions.Like(s.ClientName, pattern)
            || EF.Functions.Like(s.ClientName.Replace(":", ""), barePattern)
            // The friendly name lives on the client record; the nav becomes a LEFT JOIN in the query.
            || (s.ClientDevice != null && s.ClientDevice.Description != null && EF.Functions.Like(s.ClientDevice.Description, pattern))
            || EF.Functions.Like(s.NasName, pattern)
            || EF.Functions.Like(s.NasIpAddress, pattern)
            || EF.Functions.Like(s.SessionId, pattern)
            || (s.CallingStationId != null && EF.Functions.Like(s.CallingStationId, pattern))
            || (s.CalledStationId != null && EF.Functions.Like(s.CalledStationId, pattern)));
    }

    private static async Task<SessionTotals> TotalsAsync(IQueryable<AccountingSession> query)
    {
        var totals = await query
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Sessions = g.Count(),
                BytesIn = g.Sum(s => s.BytesIn),
                BytesOut = g.Sum(s => s.BytesOut),
                Seconds = g.Sum(s => s.SessionSeconds)
            })
            .FirstOrDefaultAsync();

        return totals is null
            ? SessionTotals.Empty
            : new SessionTotals(totals.Sessions, totals.BytesIn, totals.BytesOut, totals.Seconds);
    }

    public string FormatClient(string clientName) => MacAddress.Format(clientName, MacFormat);

    /// <summary>The friendly name if the client has one, otherwise the formatted identity.</summary>
    public string DisplayName(string clientName) =>
        ClientNames.TryGetValue(clientName, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : FormatClient(clientName);

    /// <summary>True when a friendly name exists, so the identity can be shown as a secondary line.</summary>
    public bool HasFriendlyName(string clientName) => ClientNames.ContainsKey(clientName);

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    public static string FormatDuration(long seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(seconds >= 86400 ? @"d\d\ hh\:mm\:ss" : @"hh\:mm\:ss");
}

/// <summary>Aggregated usage across a set of sessions.</summary>
public sealed record SessionTotals(int Sessions, long BytesIn, long BytesOut, long Seconds)
{
    public static readonly SessionTotals Empty = new(0, 0, 0, 0);

    public long BytesTotal => BytesIn + BytesOut;
}

/// <summary>Usage rolled up per client, for the top-talkers table.</summary>
public sealed record ClientUsage(
    string ClientName,
    int Sessions,
    int ActiveSessions,
    long BytesIn,
    long BytesOut,
    long Seconds)
{
    public long BytesTotal => BytesIn + BytesOut;
}
