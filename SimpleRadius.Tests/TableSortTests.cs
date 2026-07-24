using System.Net;
using SimpleRadius.Models;
using SimpleRadius.Pages;
using SimpleRadius.Radius;
using SimpleRadius.Services;

namespace SimpleRadius.Tests;

public class TableSortTests
{
    [Fact]
    public void AnEmptyColumnFallsBackToTheDefault()
    {
        var sort = new TableSort(null, false, "name");

        Assert.Equal("name", sort.Column);
        Assert.True(sort.IsSortedBy("NAME"));
    }

    [Fact]
    public void ClickingTheActiveColumnFlipsDirection()
    {
        var ascending = new TableSort("name", false, "name");
        Assert.True(ascending.NextDirectionFor("name"));
        Assert.Equal("▴", ascending.IndicatorFor("name"));

        var descending = new TableSort("name", true, "name");
        Assert.False(descending.NextDirectionFor("name"));
        Assert.Equal("▾", descending.IndicatorFor("name"));
    }

    [Fact]
    public void ADifferentColumnStartsAscendingAndShowsNoIndicator()
    {
        var sort = new TableSort("name", true, "name");

        Assert.False(sort.NextDirectionFor("vlan"));
        Assert.Equal(string.Empty, sort.IndicatorFor("vlan"));
    }

    [Fact]
    public async Task ClientsPageSortsOnTheRequestedColumn()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var vlan = await fixture.AddVlanAsync("Default", 10);
        await fixture.AddClientAsync("cc:cc:cc:cc:cc:cc", vlan);
        await fixture.AddClientAsync("aa:aa:aa:aa:aa:aa", vlan);
        await fixture.AddClientAsync("bb:bb:bb:bb:bb:bb", vlan);

        var ascending = new SimpleRadius.Pages.ClientDevices.IndexModel(fixture.Db, new SettingsService(fixture.Db));
        await ascending.OnGetAsync();
        Assert.Equal("aa:aa:aa:aa:aa:aa", ascending.Clients[0].Name);

        var descending = new SimpleRadius.Pages.ClientDevices.IndexModel(fixture.Db, new SettingsService(fixture.Db))
        {
            SortColumn = "name",
            Descending = true
        };
        await descending.OnGetAsync();
        Assert.Equal("cc:cc:cc:cc:cc:cc", descending.Clients[0].Name);
    }

    [Fact]
    public async Task AnUnknownSortColumnDoesNotBreakThePage()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var vlan = await fixture.AddVlanAsync("Default", 10);
        await fixture.AddClientAsync("aa:aa:aa:aa:aa:aa", vlan);

        var page = new SimpleRadius.Pages.ClientDevices.IndexModel(fixture.Db, new SettingsService(fixture.Db))
        {
            SortColumn = "; drop table ClientDevices"
        };

        await page.OnGetAsync();
        Assert.Single(page.Clients);
    }

    [Fact]
    public async Task VlanPageSortsByName()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Zulu", 30);
        await fixture.AddVlanAsync("Alpha", 20);

        var page = new SimpleRadius.Pages.VlanDefinitions.IndexModel(fixture.Db, new SettingsService(fixture.Db))
        {
            SortColumn = "name"
        };
        await page.OnGetAsync();

        Assert.Equal("Alpha", page.Vlans[0].Name);
    }

    [Fact]
    public async Task NasPageSortsByIpAddress()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddNasAsync("192.168.1.9", "secret");
        await fixture.AddNasAsync("192.168.1.1", "secret");

        var page = new SimpleRadius.Pages.NetworkAccessServers.IndexModel(
            fixture.Db,
            new RadiusServerSettings(),
            new SettingsService(fixture.Db))
        {
            SortColumn = "ip"
        };
        await page.OnGetAsync();

        Assert.Equal("192.168.1.1", page.Servers[0].IpAddress);
    }

    [Fact]
    public async Task SessionsPageSortsByBytesIn()
    {
        await using var fixture = await SessionFixtureAsync();

        var page = new SimpleRadius.Pages.Sessions.IndexModel(fixture.Db, new SettingsService(fixture.Db))
        {
            SortColumn = "in",
            Descending = true
        };
        await page.OnGetAsync();

        Assert.Equal(5000, page.Active[0].BytesIn);
    }

    /// <summary>Two active sessions with different counters, plus one for a second client.</summary>
    private static async Task<TestDatabase> SessionFixtureAsync()
    {
        var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);
        await fixture.AddNasAsync("192.168.1.1", "secret123");

        var handler = new RadiusRequestHandler(fixture.Db);
        var nas = IPAddress.Parse("192.168.1.1");

        foreach (var (user, session, bytesIn) in new[]
                 {
                     ("aa:bb:cc:dd:ee:ff", "s1", 1000u),
                     ("aa:bb:cc:dd:ee:ff", "s2", 5000u),
                     ("11:22:33:44:55:66", "s3", 250u)
                 })
        {
            await handler.HandleAsync(
                RadiusRequestBuilder.BuildAccountingRequest(1, "secret123",
                [
                    RadiusAttribute.FromString(RadiusAttributeType.UserName, user),
                    RadiusAttribute.FromString(RadiusAttributeType.AcctSessionId, session),
                    RadiusAttribute.FromUInt32(RadiusAttributeType.AcctStatusType, (uint)AcctStatusType.Start),
                    RadiusAttribute.FromUInt32(RadiusAttributeType.AcctInputOctets, bytesIn),
                    RadiusAttribute.FromUInt32(RadiusAttributeType.AcctOutputOctets, bytesIn * 2),
                    RadiusAttribute.FromUInt32(RadiusAttributeType.AcctSessionTime, 60)
                ]),
                nas,
                RadiusListenerRole.Accounting);
        }

        fixture.Db.ChangeTracker.Clear();
        return fixture;
    }

    [Fact]
    public async Task SessionSearchMatchesAClientWhateverSeparatorIsTyped()
    {
        await using var fixture = await SessionFixtureAsync();

        foreach (var term in new[] { "aa:bb:cc", "aabbcc", "AA:BB:CC" })
        {
            var page = new SimpleRadius.Pages.Sessions.IndexModel(fixture.Db, new SettingsService(fixture.Db))
            {
                Search = term
            };
            await page.OnGetAsync();

            Assert.Equal(2, page.Active.Count);
            Assert.Equal(2, page.ActiveTotals.Sessions);
        }
    }

    [Fact]
    public async Task SessionSearchMatchesTheSessionIdAndTheNas()
    {
        await using var fixture = await SessionFixtureAsync();

        var bySession = new SimpleRadius.Pages.Sessions.IndexModel(fixture.Db, new SettingsService(fixture.Db)) { Search = "s3" };
        await bySession.OnGetAsync();
        Assert.Single(bySession.Active);

        var byNas = new SimpleRadius.Pages.Sessions.IndexModel(fixture.Db, new SettingsService(fixture.Db)) { Search = "192.168.1.1" };
        await byNas.OnGetAsync();
        Assert.Equal(3, byNas.Active.Count);
    }

    [Fact]
    public async Task SessionTotalsAndPerClientUsageAggregateAcrossSessions()
    {
        await using var fixture = await SessionFixtureAsync();

        var page = new SimpleRadius.Pages.Sessions.IndexModel(fixture.Db, new SettingsService(fixture.Db));
        await page.OnGetAsync();

        Assert.Equal(3, page.ActiveTotals.Sessions);
        Assert.Equal(6250, page.ActiveTotals.BytesIn);
        Assert.Equal(12500, page.ActiveTotals.BytesOut);
        Assert.Equal(180, page.ActiveTotals.Seconds);

        // Ordered by total traffic, so the two-session client comes first.
        var top = page.TopClients[0];
        Assert.Equal("aa:bb:cc:dd:ee:ff", top.ClientName);
        Assert.Equal(2, top.Sessions);
        Assert.Equal(2, top.ActiveSessions);
        Assert.Equal(6000, top.BytesIn);
        Assert.Equal(18000, top.BytesTotal);
    }

    [Fact]
    public async Task SearchNarrowsTheTotalsToTheMatchingSessions()
    {
        await using var fixture = await SessionFixtureAsync();

        var page = new SimpleRadius.Pages.Sessions.IndexModel(fixture.Db, new SettingsService(fixture.Db))
        {
            Search = "11:22:33:44:55:66"
        };
        await page.OnGetAsync();

        Assert.Equal(1, page.ActiveTotals.Sessions);
        Assert.Equal(250, page.ActiveTotals.BytesIn);
        Assert.Single(page.TopClients);
    }

    [Fact]
    public async Task SessionsAreShownInTheConfiguredMacFormat()
    {
        await using var fixture = await SessionFixtureAsync();

        var settings = await new SettingsService(fixture.Db).GetAsync();
        settings.MacAddressFormat = MacAddressFormat.HyphenUpper;
        await fixture.Db.SaveChangesAsync();

        var page = new SimpleRadius.Pages.Sessions.IndexModel(fixture.Db, new SettingsService(fixture.Db));
        await page.OnGetAsync();

        Assert.Equal("AA-BB-CC-DD-EE-FF", page.FormatClient("aa:bb:cc:dd:ee:ff"));
    }
}
