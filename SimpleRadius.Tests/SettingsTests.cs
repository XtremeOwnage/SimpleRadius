using System.Buffers.Binary;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SimpleRadius.Models;
using SimpleRadius.Radius;
using SimpleRadius.Services;

namespace SimpleRadius.Tests;

public class SettingsTests
{
    [Fact]
    public async Task SettingsRowIsCreatedFromTheSeedOnFirstUse()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var service = new SettingsService(fixture.Db);

        var settings = await service.GetAsync(new ServerSettings { DefaultVlanId = 42, SendServiceType = true });

        Assert.Equal(42, settings.DefaultVlanId);
        Assert.True(settings.SendServiceType);
        Assert.Equal(ServerSettings.SingletonId, settings.Id);
        Assert.Equal(1, await fixture.Db.ServerSettings.CountAsync());
    }

    [Fact]
    public async Task SeedIsIgnoredOnceTheRowExists()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var service = new SettingsService(fixture.Db);

        await service.GetAsync(new ServerSettings { DefaultVlanId = 42 });
        var second = await service.GetAsync(new ServerSettings { DefaultVlanId = 99 });

        Assert.Equal(42, second.DefaultVlanId);
        Assert.Equal(1, await fixture.Db.ServerSettings.CountAsync());
    }

    [Fact]
    public async Task SavedSettingsApplyToTheNextRequestWithoutRestarting()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);
        await fixture.AddNasAsync("192.168.1.1", "secret123");

        var handler = new RadiusRequestHandler(fixture.Db);
        var request = RadiusRequestBuilder.BuildAccessRequest(1, "secret123",
            [RadiusAttribute.FromString(RadiusAttributeType.UserName, "aa:bb:cc:dd:ee:ff")]);

        var before = await handler.HandleAsync(request, IPAddress.Parse("192.168.1.1"), RadiusListenerRole.Authentication);
        Assert.True(RadiusPacket.TryParse(before!, out var accepted, out _));
        Assert.False(accepted!.TryGetAttribute(RadiusAttributeType.ServiceType, out _));

        var settings = await new SettingsService(fixture.Db).GetAsync();
        settings.SendServiceType = true;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var after = await handler.HandleAsync(request, IPAddress.Parse("192.168.1.1"), RadiusListenerRole.Authentication);
        Assert.True(RadiusPacket.TryParse(after!, out var updated, out _));
        Assert.True(updated!.TryGetAttribute(RadiusAttributeType.ServiceType, out var serviceType));
        Assert.Equal(2u, serviceType.AsUInt32());
    }

    [Fact]
    public void TunnelTagZeroSendsTheAttributesUntagged()
    {
        var attributes = AccessAcceptAttributes.Build(306, new ServerSettings { TunnelTag = 0 });

        var groupId = Assert.Single(attributes, a => a.Type == RadiusAttributeType.TunnelPrivateGroupId);
        Assert.Equal("306", groupId.AsString());

        // Untagged integers use all four bytes rather than a tag plus 24 bits.
        var tunnelType = Assert.Single(attributes, a => a.Type == RadiusAttributeType.TunnelType);
        Assert.Equal(TunnelValues.TypeVlan, tunnelType.AsUInt32());
    }

    [Fact]
    public void MikrotikStyleAttributeSetIsProducedByDefault()
    {
        // Matches what a MikroTik user group expects: medium type 6, tunnel type 13, group id = the VLAN.
        var attributes = AccessAcceptAttributes.Build(306, new ServerSettings());

        Assert.Equal(6u, Assert.Single(attributes, a => a.Type == RadiusAttributeType.TunnelMediumType).AsUInt32() & 0x00FFFFFF);
        Assert.Equal(13u, Assert.Single(attributes, a => a.Type == RadiusAttributeType.TunnelType).AsUInt32() & 0x00FFFFFF);

        var groupId = Assert.Single(attributes, a => a.Type == RadiusAttributeType.TunnelPrivateGroupId);
        Assert.Equal("306", Encoding.UTF8.GetString(groupId.Value[1..]));
    }

    [Fact]
    public void EgressVlanIdCarriesTheTaggedIndicatorAndVlan()
    {
        var attributes = AccessAcceptAttributes.Build(306, new ServerSettings { SendEgressVlanId = true });

        var egress = Assert.Single(attributes, a => a.Type == RadiusAttributeType.EgressVlanId);
        var value = BinaryPrimitives.ReadUInt32BigEndian(egress.Value);

        Assert.Equal(0x31u, value >> 24);
        Assert.Equal(306u, value & 0x00FFFFFF);
    }

    [Fact]
    public void InterimIntervalIsOnlySentWhenConfigured()
    {
        Assert.DoesNotContain(
            AccessAcceptAttributes.Build(10, new ServerSettings { AcctInterimIntervalSeconds = 0 }),
            a => a.Type == RadiusAttributeType.AcctInterimInterval);

        var attributes = AccessAcceptAttributes.Build(10, new ServerSettings { AcctInterimIntervalSeconds = 300 });
        Assert.Equal(300u, Assert.Single(attributes, a => a.Type == RadiusAttributeType.AcctInterimInterval).AsUInt32());
    }

    [Fact]
    public void TurningOffTunnelAttributesLeavesOnlyWhatWasAskedFor()
    {
        var attributes = AccessAcceptAttributes.Build(10, new ServerSettings
        {
            SendTunnelAttributes = false,
            SendEgressVlanId = true,
            AcctInterimIntervalSeconds = 0
        });

        Assert.Single(attributes);
        Assert.Equal(RadiusAttributeType.EgressVlanId, attributes[0].Type);
    }

    [Theory]
    [InlineData(MacAddressFormat.ColonLower, "aa:bb:cc:dd:ee:ff")]
    [InlineData(MacAddressFormat.ColonUpper, "AA:BB:CC:DD:EE:FF")]
    [InlineData(MacAddressFormat.HyphenLower, "aa-bb-cc-dd-ee-ff")]
    [InlineData(MacAddressFormat.HyphenUpper, "AA-BB-CC-DD-EE-FF")]
    [InlineData(MacAddressFormat.PlainLower, "aabbccddeeff")]
    [InlineData(MacAddressFormat.PlainUpper, "AABBCCDDEEFF")]
    public void MacAddressesAreRenderedInTheSelectedFormat(MacAddressFormat format, string expected)
    {
        Assert.Equal(expected, MacAddress.Format("aa:bb:cc:dd:ee:ff", format));
        Assert.Equal(expected, MacAddress.Sample(format));
    }

    [Fact]
    public void DisplayFormatLeavesOrdinaryUserNamesAlone()
    {
        Assert.Equal("alice", MacAddress.Format("alice", MacAddressFormat.PlainUpper));
    }

    [Fact]
    public async Task DisplayFormatDoesNotAffectWhetherADeviceAuthenticates()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);
        await fixture.AddNasAsync("192.168.1.1", "secret123");

        var settings = await new SettingsService(fixture.Db).GetAsync();
        settings.MacAddressFormat = MacAddressFormat.PlainUpper;
        await fixture.Db.SaveChangesAsync();

        var handler = new RadiusRequestHandler(fixture.Db);

        // The router still sends colon-separated lower case; it must match regardless of the display setting.
        var response = await handler.HandleAsync(
            RadiusRequestBuilder.BuildAccessRequest(1, "secret123",
                [RadiusAttribute.FromString(RadiusAttributeType.UserName, "aa:bb:cc:dd:ee:ff")]),
            IPAddress.Parse("192.168.1.1"),
            RadiusListenerRole.Authentication);

        Assert.True(RadiusPacket.TryParse(response!, out var packet, out _));
        Assert.Equal(RadiusCode.AccessAccept, packet!.Code);
    }
}
