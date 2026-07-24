using System.Net;
using SimpleRadius.Models;
using SimpleRadius.Radius;
using SimpleRadius.Services;

namespace SimpleRadius.Tests;

public class SessionDisplayTests
{
    /// <summary>Seeds a named client and one active session for it.</summary>
    private static async Task<TestDatabase> FixtureAsync()
    {
        var fixture = await TestDatabase.CreateAsync();
        var vlan = await fixture.AddVlanAsync("Default", 10);
        await fixture.AddNasAsync("192.168.1.1", "secret123");

        var client = await fixture.AddClientAsync("aa:bb:cc:dd:ee:ff", vlan);
        client.Description = "Office Lamp";
        await fixture.Db.SaveChangesAsync();

        var handler = new RadiusRequestHandler(fixture.Db);
        await handler.HandleAsync(
            RadiusRequestBuilder.BuildAccountingRequest(1, "secret123",
            [
                RadiusAttribute.FromString(RadiusAttributeType.UserName, "aa:bb:cc:dd:ee:ff"),
                RadiusAttribute.FromString(RadiusAttributeType.AcctSessionId, "s1"),
                RadiusAttribute.FromUInt32(RadiusAttributeType.AcctStatusType, (uint)AcctStatusType.Start)
            ]),
            IPAddress.Parse("192.168.1.1"),
            RadiusListenerRole.Accounting);

        fixture.Db.ChangeTracker.Clear();
        return fixture;
    }

    [Fact]
    public async Task ActiveSessionResolvesTheClientFriendlyName()
    {
        await using var fixture = await FixtureAsync();

        var page = new SimpleRadius.Pages.Sessions.IndexModel(fixture.Db, new SettingsService(fixture.Db));
        await page.OnGetAsync();

        var session = Assert.Single(page.Active);
        Assert.True(page.HasFriendlyName(session.ClientName));
        Assert.Equal("Office Lamp", page.DisplayName(session.ClientName));
    }

    [Fact]
    public async Task UsageByClientAlsoResolvesTheFriendlyName()
    {
        await using var fixture = await FixtureAsync();

        var page = new SimpleRadius.Pages.Sessions.IndexModel(fixture.Db, new SettingsService(fixture.Db));
        await page.OnGetAsync();

        var usage = Assert.Single(page.TopClients);
        Assert.Equal("Office Lamp", page.DisplayName(usage.ClientName));
    }

    [Fact]
    public async Task AnUnnamedClientFallsBackToTheFormattedMac()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);
        await fixture.AddNasAsync("192.168.1.1", "secret123");

        var handler = new RadiusRequestHandler(fixture.Db);
        await handler.HandleAsync(
            RadiusRequestBuilder.BuildAccountingRequest(1, "secret123",
            [
                RadiusAttribute.FromString(RadiusAttributeType.UserName, "11:22:33:44:55:66"),
                RadiusAttribute.FromString(RadiusAttributeType.AcctSessionId, "s2"),
                RadiusAttribute.FromUInt32(RadiusAttributeType.AcctStatusType, (uint)AcctStatusType.Start)
            ]),
            IPAddress.Parse("192.168.1.1"),
            RadiusListenerRole.Accounting);
        fixture.Db.ChangeTracker.Clear();

        var page = new SimpleRadius.Pages.Sessions.IndexModel(fixture.Db, new SettingsService(fixture.Db));
        await page.OnGetAsync();

        var session = Assert.Single(page.Active);
        Assert.False(page.HasFriendlyName(session.ClientName));
        Assert.Equal("11:22:33:44:55:66", page.DisplayName(session.ClientName));
    }

    [Fact]
    public async Task SessionSearchMatchesTheClientFriendlyName()
    {
        await using var fixture = await FixtureAsync();

        var page = new SimpleRadius.Pages.Sessions.IndexModel(fixture.Db, new SettingsService(fixture.Db))
        {
            Search = "office"
        };
        await page.OnGetAsync();

        Assert.Single(page.Active);
    }
}
