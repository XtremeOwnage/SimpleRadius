using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SimpleRadius.Models;
using SimpleRadius.Radius;
using SimpleRadius.Services;

namespace SimpleRadius.Tests;

public class RadiusRequestHandlerTests
{
    private const string Secret = "secret123";
    private const string NasIp = "192.168.1.1";
    private static readonly IPAddress NasAddress = IPAddress.Parse(NasIp);

    /// <summary>Applies runtime settings to the fixture, the way the settings page would.</summary>
    private static async Task<RadiusRequestHandler> HandlerAsync(
        TestDatabase fixture,
        Action<ServerSettings>? configure = null)
    {
        var settings = await new SettingsService(fixture.Db).GetAsync();
        settings.DefaultVlanId = 10;
        configure?.Invoke(settings);
        await fixture.Db.SaveChangesAsync();

        return new RadiusRequestHandler(fixture.Db);
    }

    private static byte[] AccessRequest(string userName, byte identifier = 1, string secret = Secret, bool withMessageAuthenticator = true) =>
        RadiusRequestBuilder.BuildAccessRequest(
            identifier,
            secret,
            [
                RadiusAttribute.FromString(RadiusAttributeType.UserName, userName),
                RadiusAttribute.FromString(RadiusAttributeType.CallingStationId, userName)
            ],
            withMessageAuthenticator);

    private static byte[] AccountingRequest(
        string userName,
        AcctStatusType status,
        string sessionId = "session-1",
        string secret = Secret,
        params RadiusAttribute[] extra)
    {
        RadiusAttribute[] attributes =
        [
            RadiusAttribute.FromString(RadiusAttributeType.UserName, userName),
            RadiusAttribute.FromString(RadiusAttributeType.AcctSessionId, sessionId),
            RadiusAttribute.FromUInt32(RadiusAttributeType.AcctStatusType, (uint)status),
            .. extra
        ];

        return RadiusRequestBuilder.BuildAccountingRequest(2, secret, attributes);
    }

    [Fact]
    public async Task UnknownClientIsAcceptedOntoTheDefaultVlan()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);
        await fixture.AddNasAsync(NasIp, Secret);

        var handler = await HandlerAsync(fixture);
        var response = await handler.HandleAsync(AccessRequest("aa:bb:cc:dd:ee:ff"), NasAddress, RadiusListenerRole.Authentication);

        Assert.NotNull(response);
        Assert.True(RadiusPacket.TryParse(response, out var packet, out _));
        Assert.Equal(RadiusCode.AccessAccept, packet!.Code);

        Assert.True(packet.TryGetAttribute(RadiusAttributeType.TunnelPrivateGroupId, out var groupId));
        Assert.Equal("10", Encoding.UTF8.GetString(groupId.Value[1..]));

        var client = await fixture.Db.ClientDevices.SingleAsync();
        Assert.Equal("aa:bb:cc:dd:ee:ff", client.Name);
    }

    [Fact]
    public async Task ConfiguredClientReceivesItsAssignedVlan()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);
        var trusted = await fixture.AddVlanAsync("Trusted", 20);
        await fixture.AddClientAsync("aa:bb:cc:dd:ee:ff", trusted);
        await fixture.AddNasAsync(NasIp, Secret);

        var handler = await HandlerAsync(fixture);
        var response = await handler.HandleAsync(AccessRequest("AA-BB-CC-DD-EE-FF"), NasAddress, RadiusListenerRole.Authentication);

        Assert.True(RadiusPacket.TryParse(response!, out var packet, out _));
        Assert.Equal(RadiusCode.AccessAccept, packet!.Code);
        Assert.True(packet.TryGetAttribute(RadiusAttributeType.TunnelPrivateGroupId, out var groupId));
        Assert.Equal("20", Encoding.UTF8.GetString(groupId.Value[1..]));
    }

    [Fact]
    public async Task DisabledClientReceivesAnAccessReject()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var vlan = await fixture.AddVlanAsync("Default", 10);
        await fixture.AddClientAsync("aa:bb:cc:dd:ee:ff", vlan, isEnabled: false);
        await fixture.AddNasAsync(NasIp, Secret);

        var handler = await HandlerAsync(fixture);
        var response = await handler.HandleAsync(AccessRequest("aa:bb:cc:dd:ee:ff"), NasAddress, RadiusListenerRole.Authentication);

        Assert.True(RadiusPacket.TryParse(response!, out var packet, out _));
        Assert.Equal(RadiusCode.AccessReject, packet!.Code);
        Assert.Contains("disabled", packet.GetString(RadiusAttributeType.ReplyMessage));
    }

    [Fact]
    public async Task RequestFromAnUnconfiguredNasIsDiscarded()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);

        var handler = await HandlerAsync(fixture);
        var response = await handler.HandleAsync(AccessRequest("aa:bb:cc:dd:ee:ff"), NasAddress, RadiusListenerRole.Authentication);

        Assert.Null(response);
        Assert.Empty(await fixture.Db.ClientDevices.ToListAsync());
    }

    [Fact]
    public async Task RequestFromADisabledNasIsDiscarded()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);
        await fixture.AddNasAsync(NasIp, Secret, isEnabled: false);

        var handler = await HandlerAsync(fixture);
        var response = await handler.HandleAsync(AccessRequest("aa:bb:cc:dd:ee:ff"), NasAddress, RadiusListenerRole.Authentication);

        Assert.Null(response);
    }

    [Fact]
    public async Task RequestSignedWithTheWrongSecretIsDiscarded()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);
        await fixture.AddNasAsync(NasIp, Secret);

        var handler = await HandlerAsync(fixture);
        var response = await handler.HandleAsync(
            AccessRequest("aa:bb:cc:dd:ee:ff", secret: "wrong-secret"),
            NasAddress,
            RadiusListenerRole.Authentication);

        Assert.Null(response);
        Assert.Empty(await fixture.Db.ClientDevices.ToListAsync());
    }

    [Fact]
    public async Task RequestWithoutAMessageAuthenticatorIsDiscardedWhenItIsRequired()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);
        await fixture.AddNasAsync(NasIp, Secret);

        var handler = await HandlerAsync(fixture, s => s.RequireMessageAuthenticator = true);
        var response = await handler.HandleAsync(
            AccessRequest("aa:bb:cc:dd:ee:ff", withMessageAuthenticator: false),
            NasAddress,
            RadiusListenerRole.Authentication);

        Assert.Null(response);
    }

    [Fact]
    public async Task AccountingPacketArrivingOnTheAuthenticationPortIsDiscarded()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);
        await fixture.AddNasAsync(NasIp, Secret);

        var handler = await HandlerAsync(fixture);
        var response = await handler.HandleAsync(
            AccountingRequest("aa:bb:cc:dd:ee:ff", AcctStatusType.Start),
            NasAddress,
            RadiusListenerRole.Authentication);

        Assert.Null(response);
    }

    [Fact]
    public async Task AccountingStartOpensASessionAndStopClosesIt()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);
        await fixture.AddNasAsync(NasIp, Secret);

        var handler = await HandlerAsync(fixture);

        // Authenticate first so the session can be tied to a client and its VLAN.
        await handler.HandleAsync(AccessRequest("aa:bb:cc:dd:ee:ff"), NasAddress, RadiusListenerRole.Authentication);

        var startResponse = await handler.HandleAsync(
            AccountingRequest("aa:bb:cc:dd:ee:ff", AcctStatusType.Start),
            NasAddress,
            RadiusListenerRole.Accounting);

        Assert.True(RadiusPacket.TryParse(startResponse!, out var ack, out _));
        Assert.Equal(RadiusCode.AccountingResponse, ack!.Code);

        var session = await fixture.Db.AccountingSessions.SingleAsync();
        Assert.True(session.IsActive);
        Assert.Equal(10, session.VlanId);
        Assert.NotNull(session.ClientDeviceId);

        await handler.HandleAsync(
            AccountingRequest("aa:bb:cc:dd:ee:ff", AcctStatusType.Stop,
                extra:
                [
                    RadiusAttribute.FromUInt32(RadiusAttributeType.AcctInputOctets, 1500),
                    RadiusAttribute.FromUInt32(RadiusAttributeType.AcctOutputOctets, 2500),
                    RadiusAttribute.FromUInt32(RadiusAttributeType.AcctSessionTime, 3600),
                    RadiusAttribute.FromUInt32(RadiusAttributeType.AcctTerminateCause, 1)
                ]),
            NasAddress,
            RadiusListenerRole.Accounting);

        fixture.Db.ChangeTracker.Clear();
        session = await fixture.Db.AccountingSessions.SingleAsync();
        Assert.False(session.IsActive);
        Assert.NotNull(session.StopTime);
        Assert.Equal(1500, session.BytesIn);
        Assert.Equal(2500, session.BytesOut);
        Assert.Equal(3600, session.SessionSeconds);
        Assert.Equal(1u, session.TerminateCause);
    }

    [Fact]
    public async Task InterimUpdateRefreshesCountersWithoutOpeningASecondSession()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);
        await fixture.AddNasAsync(NasIp, Secret);

        var handler = await HandlerAsync(fixture);
        await handler.HandleAsync(AccountingRequest("alice", AcctStatusType.Start), NasAddress, RadiusListenerRole.Accounting);
        await handler.HandleAsync(
            AccountingRequest("alice", AcctStatusType.InterimUpdate,
                extra:
                [
                    RadiusAttribute.FromUInt32(RadiusAttributeType.AcctInputOctets, 4096),
                    // Gigawords carry the high order bits once the 32 bit counter wraps.
                    RadiusAttribute.FromUInt32(RadiusAttributeType.AcctInputGigawords, 2)
                ]),
            NasAddress,
            RadiusListenerRole.Accounting);

        fixture.Db.ChangeTracker.Clear();
        var session = await fixture.Db.AccountingSessions.SingleAsync();
        Assert.True(session.IsActive);
        Assert.Equal((2L << 32) + 4096, session.BytesIn);
    }

    [Fact]
    public async Task SameSessionIdFromTwoRoutersIsTrackedSeparately()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);
        await fixture.AddNasAsync(NasIp, Secret);
        await fixture.AddNasAsync("192.168.1.2", Secret);

        var handler = await HandlerAsync(fixture);
        await handler.HandleAsync(AccountingRequest("alice", AcctStatusType.Start), NasAddress, RadiusListenerRole.Accounting);
        await handler.HandleAsync(
            AccountingRequest("bob", AcctStatusType.Start),
            IPAddress.Parse("192.168.1.2"),
            RadiusListenerRole.Accounting);

        Assert.Equal(2, await fixture.Db.AccountingSessions.CountAsync());
    }

    [Fact]
    public async Task AccountingOnClosesTheSessionsLeftOpenByARestartedRouter()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);
        await fixture.AddNasAsync(NasIp, Secret);

        var handler = await HandlerAsync(fixture);
        await handler.HandleAsync(AccountingRequest("alice", AcctStatusType.Start, "s1"), NasAddress, RadiusListenerRole.Accounting);
        await handler.HandleAsync(AccountingRequest("bob", AcctStatusType.Start, "s2"), NasAddress, RadiusListenerRole.Accounting);

        var response = await handler.HandleAsync(
            RadiusRequestBuilder.BuildAccountingRequest(9, Secret,
                [RadiusAttribute.FromUInt32(RadiusAttributeType.AcctStatusType, (uint)AcctStatusType.AccountingOn)]),
            NasAddress,
            RadiusListenerRole.Accounting);

        Assert.NotNull(response);
        fixture.Db.ChangeTracker.Clear();
        Assert.Empty(await fixture.Db.AccountingSessions.Where(s => s.IsActive).ToListAsync());
        Assert.Equal(2, await fixture.Db.AccountingSessions.CountAsync());
    }

    [Fact]
    public async Task AccountingSignedWithTheWrongSecretIsDiscarded()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);
        await fixture.AddNasAsync(NasIp, Secret);

        var handler = await HandlerAsync(fixture);
        var response = await handler.HandleAsync(
            AccountingRequest("alice", AcctStatusType.Start, secret: "wrong-secret"),
            NasAddress,
            RadiusListenerRole.Accounting);

        Assert.Null(response);
        Assert.Empty(await fixture.Db.AccountingSessions.ToListAsync());
    }

    [Fact]
    public async Task AccountingIsAcknowledgedButNotStoredWhenTheNasHasItDisabled()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);
        await fixture.AddNasAsync(NasIp, Secret, accountingEnabled: false);

        var handler = await HandlerAsync(fixture);
        var response = await handler.HandleAsync(
            AccountingRequest("alice", AcctStatusType.Start),
            NasAddress,
            RadiusListenerRole.Accounting);

        Assert.True(RadiusPacket.TryParse(response!, out var packet, out _));
        Assert.Equal(RadiusCode.AccountingResponse, packet!.Code);
        Assert.Empty(await fixture.Db.AccountingSessions.ToListAsync());
    }

    [Fact]
    public async Task MalformedDatagramIsDiscarded()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddNasAsync(NasIp, Secret);

        var handler = await HandlerAsync(fixture);
        Assert.Null(await handler.HandleAsync([1, 2, 3], NasAddress, RadiusListenerRole.Authentication));
    }

    [Fact]
    public async Task NasLastSeenIsRecordedOnEachRequest()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);
        await fixture.AddNasAsync(NasIp, Secret);

        var handler = await HandlerAsync(fixture);
        await handler.HandleAsync(AccessRequest("aa:bb:cc:dd:ee:ff"), NasAddress, RadiusListenerRole.Authentication);

        fixture.Db.ChangeTracker.Clear();
        var nas = await fixture.Db.NetworkAccessServers.SingleAsync();
        Assert.NotNull(nas.LastSeenUtc);
    }
}
