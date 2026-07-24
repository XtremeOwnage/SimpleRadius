using Microsoft.EntityFrameworkCore;
using SimpleRadius.Models;
using SimpleRadius.Services;

namespace SimpleRadius.Tests;

public class RadiusPolicyServiceTests
{
    private static ServerSettings Settings(Action<ServerSettings>? configure = null)
    {
        var settings = new ServerSettings { DefaultVlanId = 10 };
        configure?.Invoke(settings);
        return settings;
    }

    [Fact]
    public async Task UnknownClientGetsTheDefaultVlanAndIsPersisted()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);
        var service = new RadiusPolicyService(fixture.Db, Settings());

        var result = await service.AuthorizeAsync("aa:bb:cc:dd:ee:ff", "192.168.1.1");

        Assert.True(result.IsAccepted);
        Assert.Equal(10, result.VlanId);

        var persisted = await fixture.Db.ClientDevices.SingleAsync();
        Assert.Equal("aa:bb:cc:dd:ee:ff", persisted.Name);
        Assert.True(persisted.IsAutoCreated);
        Assert.True(persisted.IsEnabled);
        Assert.Equal("192.168.1.1", persisted.LastNasIpAddress);
        Assert.NotNull(persisted.LastSeenUtc);
        Assert.Equal(1, persisted.AuthenticationCount);
    }

    [Fact]
    public async Task DefaultVlanIsCreatedWhenNoVlanExistsYet()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var service = new RadiusPolicyService(fixture.Db, Settings(s => s.DefaultVlanId = 99));

        var result = await service.AuthorizeAsync("aa:bb:cc:dd:ee:ff", "192.168.1.1");

        Assert.True(result.IsAccepted);
        Assert.Equal(99, result.VlanId);

        var vlan = await fixture.Db.VlanDefinitions.SingleAsync();
        Assert.Equal(99, vlan.VlanId);
        Assert.Equal("Default", vlan.Name);
    }

    [Fact]
    public async Task ConfiguredClientKeepsItsOwnVlanRatherThanTheDefault()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);
        var trusted = await fixture.AddVlanAsync("Trusted", 20);
        await fixture.AddClientAsync("aa:bb:cc:dd:ee:ff", trusted);

        var service = new RadiusPolicyService(fixture.Db, Settings());
        var result = await service.AuthorizeAsync("aa:bb:cc:dd:ee:ff", "192.168.1.1");

        Assert.True(result.IsAccepted);
        Assert.Equal(20, result.VlanId);
        Assert.Equal(1, await fixture.Db.ClientDevices.CountAsync());
    }

    [Fact]
    public async Task DifferentMacSpellingsResolveToOneClient()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);
        var service = new RadiusPolicyService(fixture.Db, Settings());

        await service.AuthorizeAsync("AA-BB-CC-DD-EE-FF", "192.168.1.1");
        await service.AuthorizeAsync("aabbccddeeff", "192.168.1.1");

        var client = await fixture.Db.ClientDevices.SingleAsync();
        Assert.Equal("aa:bb:cc:dd:ee:ff", client.Name);
        Assert.Equal(2, client.AuthenticationCount);
    }

    [Fact]
    public async Task DisabledClientIsRejected()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var vlan = await fixture.AddVlanAsync("Default", 10);
        await fixture.AddClientAsync("aa:bb:cc:dd:ee:ff", vlan, isEnabled: false);

        var service = new RadiusPolicyService(fixture.Db, Settings());
        var result = await service.AuthorizeAsync("aa:bb:cc:dd:ee:ff", "192.168.1.1");

        Assert.False(result.IsAccepted);
        Assert.Contains("disabled", result.Reason);
    }

    [Fact]
    public async Task UnknownClientIsRejectedWhenAutoCreationIsOff()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await fixture.AddVlanAsync("Default", 10);

        var service = new RadiusPolicyService(fixture.Db, Settings(s => s.AutoCreateUnknownClients = false));
        var result = await service.AuthorizeAsync("aa:bb:cc:dd:ee:ff", "192.168.1.1");

        Assert.False(result.IsAccepted);
        Assert.Empty(await fixture.Db.ClientDevices.ToListAsync());
    }

    [Fact]
    public async Task UnknownNasIsNotRegisteredByDefault()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var service = new RadiusPolicyService(fixture.Db, Settings());

        Assert.Null(await service.ResolveNasAsync("10.0.0.1"));
        Assert.Empty(await fixture.Db.NetworkAccessServers.ToListAsync());
    }

    [Fact]
    public async Task UnknownNasIsRegisteredWhenAutoRegistrationIsOn()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var service = new RadiusPolicyService(fixture.Db, Settings(s =>
        {
            s.AutoRegisterUnknownNas = true;
            s.DefaultSharedSecret = "seeded-secret";
        }));

        var nas = await service.ResolveNasAsync("10.0.0.1");

        Assert.NotNull(nas);
        Assert.Equal("seeded-secret", nas!.SharedSecret);
        Assert.True(nas.IsAutoRegistered);
    }

    [Fact]
    public async Task EditingAVlansIdChangesWhatAssignedClientsReceive()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var vlan = await fixture.AddVlanAsync("Guest", 30);
        await fixture.AddClientAsync("aa:bb:cc:dd:ee:ff", vlan);

        var service = new RadiusPolicyService(fixture.Db, Settings());
        Assert.Equal(30, (await service.AuthorizeAsync("aa:bb:cc:dd:ee:ff", "192.168.1.1")).VlanId);

        vlan.VlanId = 31;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        Assert.Equal(31, (await service.AuthorizeAsync("aa:bb:cc:dd:ee:ff", "192.168.1.1")).VlanId);
    }
}
