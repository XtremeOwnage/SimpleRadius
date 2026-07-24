using SimpleRadius.Services;

namespace SimpleRadius.Tests;

public class VlanGroupingTests
{
    private static SimpleRadius.Pages.VlanDefinitions.IndexModel Page(TestDatabase fixture, bool grouped) =>
        new(fixture.Db, new SettingsService(fixture.Db)) { Grouped = grouped };

    private static async Task<TestDatabase> SeededAsync()
    {
        var fixture = await TestDatabase.CreateAsync();
        (await fixture.AddVlanAsync("V_SECURE", 2)).Group = "LAN";
        (await fixture.AddVlanAsync("V_IOT_Generic", 306)).Group = "IOT";
        (await fixture.AddVlanAsync("V_IOT_DMZ", 307)).Group = "IOT";
        await fixture.AddVlanAsync("Default", 10); // no group
        await fixture.Db.SaveChangesAsync();
        return fixture;
    }

    [Fact]
    public async Task GroupsBucketVlansAndPlaceUngroupedLast()
    {
        await using var fixture = await SeededAsync();
        var page = Page(fixture, grouped: true);
        await page.OnGetAsync();

        Assert.True(page.HasGroups);

        // Named groups alphabetically first, ungrouped last.
        Assert.Equal(["IOT", "LAN", SimpleRadius.Pages.VlanDefinitions.IndexModel.Ungrouped],
            page.Groups.Select(g => g.Group).ToList());

        var iot = page.Groups.Single(g => g.Group == "IOT").Vlans;
        Assert.Equal([306, 307], iot.Select(v => v.VlanId).ToList());
    }

    [Fact]
    public async Task WithNoGroupsEverythingLandsUnderUngrouped()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);

        var page = Page(fixture, grouped: true);
        await page.OnGetAsync();

        Assert.False(page.HasGroups);
        var only = Assert.Single(page.Groups);
        Assert.Equal(SimpleRadius.Pages.VlanDefinitions.IndexModel.Ungrouped, only.Group);
    }

    [Fact]
    public async Task GroupIsSortableInTheFlatView()
    {
        await using var fixture = await SeededAsync();
        var page = new SimpleRadius.Pages.VlanDefinitions.IndexModel(fixture.Db, new SettingsService(fixture.Db))
        {
            SortColumn = "group"
        };
        await page.OnGetAsync();

        // Ordered by group; the two IOT VLANs come before LAN, with ungrouped (null) sorting first in SQLite.
        var groups = page.Vlans.Select(v => v.Group).ToList();
        Assert.Equal(groups.OrderBy(g => g, StringComparer.Ordinal).ToList(), groups);
    }
}
