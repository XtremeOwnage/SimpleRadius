using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SimpleRadius.Models;
using SimpleRadius.Radius;
using SimpleRadius.Services;

namespace SimpleRadius.Tests;

public class SsidVlanTests
{
    private const string Secret = "secret123";
    private static readonly IPAddress Nas = IPAddress.Parse("192.168.1.1");

    private static async Task<TestDatabase> FixtureAsync()
    {
        var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);
        await fixture.AddNasAsync("192.168.1.1", Secret);
        return fixture;
    }

    private static async Task<SsidVlanRule> AddRuleAsync(TestDatabase fixture, string ssid, VlanDefinition vlan)
    {
        var rule = new SsidVlanRule { Ssid = ssid, VlanDefinitionId = vlan.Id };
        fixture.Db.SsidVlanRules.Add(rule);
        await fixture.Db.SaveChangesAsync();
        return rule;
    }

    private static byte[] AccessRequest(string user, string? calledStation)
    {
        var attrs = new List<RadiusAttribute>
        {
            RadiusAttribute.FromString(RadiusAttributeType.UserName, user)
        };
        if (calledStation is not null)
        {
            attrs.Add(RadiusAttribute.FromString(RadiusAttributeType.CalledStationId, calledStation));
        }

        return RadiusRequestBuilder.BuildAccessRequest(1, Secret, attrs);
    }

    [Fact]
    public async Task NewDeviceOnAMappedSsidGetsThatVlan()
    {
        await using var fixture = await FixtureAsync();
        var iot = await fixture.AddVlanAsync("V_IOT_Generic", 306);
        await AddRuleAsync(fixture, "IoT", iot);

        var handler = new RadiusRequestHandler(fixture.Db);
        var response = await handler.HandleAsync(
            AccessRequest("aa:bb:cc:dd:ee:ff", "24-A4-3C-00-11-22:IoT"),
            Nas,
            RadiusListenerRole.Authentication);

        Assert.True(RadiusPacket.TryParse(response!, out var packet, out _));
        Assert.True(packet!.TryGetAttribute(RadiusAttributeType.TunnelPrivateGroupId, out var groupId));
        Assert.Equal("306", Encoding.UTF8.GetString(groupId.Value[1..]));

        var client = await fixture.Db.ClientDevices.Include(c => c.VlanDefinition).SingleAsync();
        Assert.Equal(306, client.VlanDefinition!.VlanId);
    }

    [Fact]
    public async Task NewDeviceOnAnUnmappedSsidGetsTheGlobalDefault()
    {
        await using var fixture = await FixtureAsync();
        await AddRuleAsync(fixture, "IoT", await fixture.AddVlanAsync("V_IOT_Generic", 306));

        var handler = new RadiusRequestHandler(fixture.Db);
        var response = await handler.HandleAsync(
            AccessRequest("aa:bb:cc:dd:ee:ff", "24-A4-3C-00-11-22:Trusted"),
            Nas,
            RadiusListenerRole.Authentication);

        Assert.True(RadiusPacket.TryParse(response!, out var packet, out _));
        Assert.True(packet!.TryGetAttribute(RadiusAttributeType.TunnelPrivateGroupId, out var groupId));
        Assert.Equal("10", Encoding.UTF8.GetString(groupId.Value[1..]));
    }

    [Fact]
    public async Task ARequestWithNoSsidGetsTheGlobalDefault()
    {
        await using var fixture = await FixtureAsync();
        await AddRuleAsync(fixture, "IoT", await fixture.AddVlanAsync("V_IOT_Generic", 306));

        var handler = new RadiusRequestHandler(fixture.Db);
        var response = await handler.HandleAsync(
            AccessRequest("aa:bb:cc:dd:ee:ff", calledStation: null),
            Nas,
            RadiusListenerRole.Authentication);

        Assert.True(RadiusPacket.TryParse(response!, out var packet, out _));
        Assert.True(packet!.TryGetAttribute(RadiusAttributeType.TunnelPrivateGroupId, out var groupId));
        Assert.Equal("10", Encoding.UTF8.GetString(groupId.Value[1..]));
    }

    [Fact]
    public async Task AKnownDeviceKeepsItsVlanRegardlessOfTheSsidRule()
    {
        await using var fixture = await FixtureAsync();
        var trusted = await fixture.AddVlanAsync("V_SECURE", 2);
        var iot = await fixture.AddVlanAsync("V_IOT_Generic", 306);
        await AddRuleAsync(fixture, "IoT", iot);
        // The device is already known and explicitly on the secure VLAN.
        await fixture.AddClientAsync("aa:bb:cc:dd:ee:ff", trusted);

        var handler = new RadiusRequestHandler(fixture.Db);
        var response = await handler.HandleAsync(
            AccessRequest("aa:bb:cc:dd:ee:ff", "24-A4-3C-00-11-22:IoT"),
            Nas,
            RadiusListenerRole.Authentication);

        Assert.True(RadiusPacket.TryParse(response!, out var packet, out _));
        Assert.True(packet!.TryGetAttribute(RadiusAttributeType.TunnelPrivateGroupId, out var groupId));
        Assert.Equal("2", Encoding.UTF8.GetString(groupId.Value[1..]));
    }

    [Fact]
    public async Task SsidMatchingIsCaseSensitive()
    {
        await using var fixture = await FixtureAsync();
        await AddRuleAsync(fixture, "IoT", await fixture.AddVlanAsync("V_IOT_Generic", 306));

        var handler = new RadiusRequestHandler(fixture.Db);
        var response = await handler.HandleAsync(
            AccessRequest("aa:bb:cc:dd:ee:ff", "24-A4-3C-00-11-22:iot"),   // lower case, no match
            Nas,
            RadiusListenerRole.Authentication);

        Assert.True(RadiusPacket.TryParse(response!, out var packet, out _));
        Assert.True(packet!.TryGetAttribute(RadiusAttributeType.TunnelPrivateGroupId, out var groupId));
        Assert.Equal("10", Encoding.UTF8.GetString(groupId.Value[1..]));
    }

    [Fact]
    public async Task AccountingCapturesSsidNasIdentifierAndPortType()
    {
        await using var fixture = await FixtureAsync();

        var handler = new RadiusRequestHandler(fixture.Db);
        await handler.HandleAsync(
            RadiusRequestBuilder.BuildAccountingRequest(2, Secret,
            [
                RadiusAttribute.FromString(RadiusAttributeType.UserName, "aa:bb:cc:dd:ee:ff"),
                RadiusAttribute.FromString(RadiusAttributeType.AcctSessionId, "s1"),
                RadiusAttribute.FromUInt32(RadiusAttributeType.AcctStatusType, (uint)AcctStatusType.Start),
                RadiusAttribute.FromString(RadiusAttributeType.CalledStationId, "24-A4-3C-00-11-22:IoT"),
                RadiusAttribute.FromString(RadiusAttributeType.NasIdentifier, "AP-Bedroom"),
                RadiusAttribute.FromUInt32(RadiusAttributeType.NasPortType, 19)
            ]),
            Nas,
            RadiusListenerRole.Accounting);

        var session = await fixture.Db.AccountingSessions.SingleAsync();
        Assert.Equal("IoT", session.Ssid);
        Assert.Equal("AP-Bedroom", session.NasIdentifier);
        Assert.Equal("Wireless 802.11", session.NasPortType);
        Assert.Equal("24-A4-3C-00-11-22:IoT", session.CalledStationId);
    }

    [Fact]
    public async Task SessionSearchMatchesTheSsid()
    {
        await using var fixture = await FixtureAsync();
        var handler = new RadiusRequestHandler(fixture.Db);
        await handler.HandleAsync(
            RadiusRequestBuilder.BuildAccountingRequest(2, Secret,
            [
                RadiusAttribute.FromString(RadiusAttributeType.UserName, "aa:bb:cc:dd:ee:ff"),
                RadiusAttribute.FromString(RadiusAttributeType.AcctSessionId, "s1"),
                RadiusAttribute.FromUInt32(RadiusAttributeType.AcctStatusType, (uint)AcctStatusType.Start),
                RadiusAttribute.FromString(RadiusAttributeType.CalledStationId, "24-A4-3C-00-11-22:IoT")
            ]),
            Nas,
            RadiusListenerRole.Accounting);
        fixture.Db.ChangeTracker.Clear();

        var page = new SimpleRadius.Pages.Sessions.IndexModel(fixture.Db, new SettingsService(fixture.Db))
        {
            Search = "iot"   // LIKE is case insensitive
        };
        await page.OnGetAsync();

        Assert.Single(page.Active);
    }

    [Fact]
    public async Task SsidRulesPageAddsAndListsRules()
    {
        await using var fixture = await FixtureAsync();
        var iot = await fixture.AddVlanAsync("V_IOT_Generic", 306);

        var add = new SimpleRadius.Pages.SsidRules.IndexModel(fixture.Db, new SettingsService(fixture.Db))
        {
            Input = new SsidVlanRule { Ssid = "  IoT  ", VlanDefinitionId = iot.Id }
        };
        await add.OnPostAsync();

        var rule = await fixture.Db.SsidVlanRules.SingleAsync();
        Assert.Equal("IoT", rule.Ssid);   // trimmed

        var list = new SimpleRadius.Pages.SsidRules.IndexModel(fixture.Db, new SettingsService(fixture.Db));
        await list.OnGetAsync();
        Assert.Single(list.Rules);
        Assert.Equal(306, list.Rules[0].VlanDefinition!.VlanId);
    }
}
