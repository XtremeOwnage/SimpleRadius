using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleRadius.Data;
using SimpleRadius.Models;
using SimpleRadius.Radius;
using SimpleRadius.Services;

namespace SimpleRadius.Tests;

/// <summary>
/// Drives the page models against real SQLite. The queries behind the admin screens are translated by
/// the provider, so exercising them here catches translation failures the service tests never reach.
/// </summary>
public class AdminPageTests
{
    private static RadiusServerSettings Startup() => new() { AuthPort = 1812, AcctPort = 1813 };

    private static SettingsService SettingsFor(TestDatabase fixture) => new(fixture.Db);

    /// <summary>Seeds a client, a NAS and one open plus one closed session.</summary>
    private static async Task<TestDatabase> SeededAsync()
    {
        var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);
        await fixture.AddNasAsync("192.168.1.1", "secret123");

        var handler = new RadiusRequestHandler(fixture.Db);
        var nas = IPAddress.Parse("192.168.1.1");

        await handler.HandleAsync(
            RadiusRequestBuilder.BuildAccessRequest(1, "secret123",
                [RadiusAttribute.FromString(RadiusAttributeType.UserName, "aa:bb:cc:dd:ee:ff")]),
            nas,
            RadiusListenerRole.Authentication);

        foreach (var (sessionId, status) in new[]
                 {
                     ("open-session", AcctStatusType.Start),
                     ("closed-session", AcctStatusType.Stop)
                 })
        {
            await handler.HandleAsync(
                RadiusRequestBuilder.BuildAccountingRequest(2, "secret123",
                [
                    RadiusAttribute.FromString(RadiusAttributeType.UserName, "aa:bb:cc:dd:ee:ff"),
                    RadiusAttribute.FromString(RadiusAttributeType.AcctSessionId, sessionId),
                    RadiusAttribute.FromUInt32(RadiusAttributeType.AcctStatusType, (uint)status)
                ]),
                nas,
                RadiusListenerRole.Accounting);
        }

        fixture.Db.ChangeTracker.Clear();
        return fixture;
    }

    [Fact]
    public async Task DashboardSummarisesTheCurrentState()
    {
        await using var fixture = await SeededAsync();

        var server = new RadiusServerProcess(
            new TestDbContextFactory(fixture.Db),
            Startup(),
            NullLoggerFactory.Instance);

        var page = new SimpleRadius.Pages.IndexModel(fixture.Db, Startup(), SettingsFor(fixture), server);
        await page.OnGetAsync();

        Assert.Equal(1, page.ClientCount);
        Assert.Equal(1, page.VlanCount);
        Assert.Equal(1, page.NasCount);
        Assert.Equal(1, page.ActiveSessionCount);
        Assert.Single(page.RecentClients);
        Assert.Equal(2, page.RecentSessions.Count);

        // Falls back to the configured ports until the listener has bound.
        Assert.Equal(1812, page.AuthPort);
        Assert.Equal(1813, page.AcctPort);
    }

    [Fact]
    public async Task SessionsPageSplitsLiveSessionsFromHistory()
    {
        await using var fixture = await SeededAsync();

        var page = new SimpleRadius.Pages.Sessions.IndexModel(fixture.Db, SettingsFor(fixture));
        await page.OnGetAsync();

        Assert.Equal("open-session", Assert.Single(page.Active).SessionId);
        Assert.Equal("closed-session", Assert.Single(page.Closed).SessionId);
    }

    [Fact]
    public async Task ClearingHistoryLeavesLiveSessionsAlone()
    {
        await using var fixture = await SeededAsync();

        var page = new SimpleRadius.Pages.Sessions.IndexModel(fixture.Db, SettingsFor(fixture));
        await page.OnPostClearHistoryAsync();

        var remaining = await fixture.Db.AccountingSessions.SingleAsync();
        Assert.Equal("open-session", remaining.SessionId);
    }

    [Fact]
    public async Task ClientsPageListsDevicesAndOffersTheDefaultVlan()
    {
        await using var fixture = await SeededAsync();

        var page = new SimpleRadius.Pages.ClientDevices.IndexModel(fixture.Db, SettingsFor(fixture));
        await page.OnGetAsync();

        Assert.Equal("aa:bb:cc:dd:ee:ff", Assert.Single(page.Clients).Name);
        Assert.Single(page.VlanOptions);
        Assert.NotEqual(0, page.Input.VlanDefinitionId);
    }

    [Fact]
    public async Task ClientsPageFilterMatchesOnIdentity()
    {
        await using var fixture = await SeededAsync();

        var page = new SimpleRadius.Pages.ClientDevices.IndexModel(fixture.Db, SettingsFor(fixture)) { Search = "zz:zz" };
        await page.OnGetAsync();

        Assert.Empty(page.Clients);
    }

    [Fact]
    public async Task AddingAClientNormalisesTheMacBeforeSaving()
    {
        await using var fixture = await SeededAsync();
        var vlan = await fixture.Db.VlanDefinitions.SingleAsync();

        var page = new SimpleRadius.Pages.ClientDevices.IndexModel(fixture.Db, SettingsFor(fixture))
        {
            Input = new ClientDevice { Name = "11-22-33-44-55-66", VlanDefinitionId = vlan.Id }
        };

        var result = await page.OnPostAsync();

        Assert.IsType<RedirectToPageResult>(result);
        Assert.True(await fixture.Db.ClientDevices.AnyAsync(c => c.Name == "11:22:33:44:55:66"));
    }

    [Fact]
    public async Task VlanStillInUseCannotBeDeleted()
    {
        await using var fixture = await SeededAsync();
        var vlan = await fixture.Db.VlanDefinitions.SingleAsync();

        var page = new SimpleRadius.Pages.VlanDefinitions.IndexModel(fixture.Db, SettingsFor(fixture));
        await page.OnPostDeleteAsync(vlan.Id);

        Assert.NotNull(page.ErrorMessage);
        Assert.Equal(1, await fixture.Db.VlanDefinitions.CountAsync());
    }

    [Fact]
    public async Task VlanPageCountsClientsPerVlan()
    {
        await using var fixture = await SeededAsync();

        var page = new SimpleRadius.Pages.VlanDefinitions.IndexModel(fixture.Db, SettingsFor(fixture));
        await page.OnGetAsync();

        var vlan = Assert.Single(page.Vlans);
        Assert.Equal(1, page.ClientCounts[vlan.Id]);
    }

    [Fact]
    public async Task NasPageListsConfiguredRouters()
    {
        await using var fixture = await SeededAsync();

        var page = new SimpleRadius.Pages.NetworkAccessServers.IndexModel(fixture.Db, Startup(), SettingsFor(fixture));
        await page.OnGetAsync();

        Assert.Equal("192.168.1.1", Assert.Single(page.Servers).IpAddress);
    }

    /// <summary>Hands the page models the fixture's context; the listener is never started here.</summary>
    private sealed class TestDbContextFactory(RadiusDbContext context) : IDbContextFactory<RadiusDbContext>
    {
        public RadiusDbContext CreateDbContext() => context;
    }
}
