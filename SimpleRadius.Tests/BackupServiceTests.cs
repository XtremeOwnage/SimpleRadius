using Microsoft.EntityFrameworkCore;
using SimpleRadius.Models;
using SimpleRadius.Services;

namespace SimpleRadius.Tests;

public class BackupServiceTests
{
    private static BackupService Service(TestDatabase fixture) =>
        new(fixture.Db, new SettingsService(fixture.Db));

    private static async Task<TestDatabase> SeededAsync()
    {
        var fixture = await TestDatabase.CreateAsync();
        var iot = await fixture.AddVlanAsync("V_IOT_Generic", 306);
        iot.Group = "IOT";
        iot.Description = "Generic IOT Devices";
        var secure = await fixture.AddVlanAsync("V_SECURE", 2);
        secure.Group = "LAN";
        await fixture.AddNasAsync("192.168.1.1", "unifi-secret");

        var lamp = await fixture.AddClientAsync("44:4f:8e:c8:9a:ac", iot);
        lamp.Description = "Office Lamp";
        lamp.Notes = "desk";
        await fixture.Db.SaveChangesAsync();
        return fixture;
    }

    [Theory]
    [InlineData(BackupFormat.Json)]
    [InlineData(BackupFormat.Yaml)]
    public async Task ExportThenImportIntoAnEmptyInstanceReproducesTheConfiguration(BackupFormat format)
    {
        string serialized;
        await using (var source = await SeededAsync())
        {
            var document = await Service(source).ExportAsync();
            serialized = Service(source).Serialize(document, format);
        }

        // A completely separate, empty database.
        await using var target = await TestDatabase.CreateAsync();
        var parsed = BackupService.Parse(serialized);
        var result = await Service(target).ImportAsync(parsed, new ImportOptions());

        Assert.Equal(2, result.VlansAdded);
        Assert.Equal(1, result.ClientsAdded);
        Assert.Equal(1, result.NasAdded);

        var vlan = await target.Db.VlanDefinitions.SingleAsync(v => v.VlanId == 306);
        Assert.Equal("V_IOT_Generic", vlan.Name);
        Assert.Equal("IOT", vlan.Group);

        var client = await target.Db.ClientDevices.Include(c => c.VlanDefinition).SingleAsync(c => c.Name == "44:4f:8e:c8:9a:ac");
        Assert.Equal("Office Lamp", client.Description);
        Assert.Equal("desk", client.Notes);
        Assert.Equal(306, client.VlanDefinition!.VlanId);

        var nas = await target.Db.NetworkAccessServers.SingleAsync();
        Assert.Equal("unifi-secret", nas.SharedSecret);
    }

    [Fact]
    public async Task ImportUpdatesExistingRecordsByKeyRatherThanDuplicating()
    {
        await using var fixture = await SeededAsync();

        var document = new BackupDocument
        {
            Vlans = [new BackupVlan { Name = "V_IOT_Generic renamed", VlanId = 306, Group = "IoT" }],
            Clients = [new BackupClient { Identity = "44-4F-8E-C8-9A-AC", Name = "Desk Lamp", VlanId = 306 }],
            NetworkAccessServers = [new BackupNas { Name = "UniFi", IpAddress = "192.168.1.1", SharedSecret = "rotated" }]
        };

        var result = await Service(fixture).ImportAsync(document, new ImportOptions());

        Assert.Equal(1, result.VlansUpdated);
        Assert.Equal(1, result.ClientsUpdated);
        Assert.Equal(1, result.NasUpdated);
        Assert.Equal(0, result.VlansAdded + result.ClientsAdded + result.NasAdded);

        // Keyed on VLAN number / normalised MAC / NAS address, so counts are unchanged.
        Assert.Equal(2, await fixture.Db.VlanDefinitions.CountAsync());
        Assert.Equal(1, await fixture.Db.ClientDevices.CountAsync());
        Assert.Equal(1, await fixture.Db.NetworkAccessServers.CountAsync());

        var client = await fixture.Db.ClientDevices.SingleAsync();
        Assert.Equal("Desk Lamp", client.Description);   // updated
        Assert.Equal("rotated", (await fixture.Db.NetworkAccessServers.SingleAsync()).SharedSecret);
    }

    [Fact]
    public async Task AClientReferencingAnUndefinedVlanFallsBackWithAWarning()
    {
        await using var fixture = await TestDatabase.CreateAsync();

        var document = new BackupDocument
        {
            Vlans = [new BackupVlan { Name = "Default", VlanId = 10 }],
            Clients = [new BackupClient { Identity = "aa:bb:cc:dd:ee:ff", VlanId = 999 }]
        };

        var result = await Service(fixture).ImportAsync(document, new ImportOptions());

        Assert.Equal(1, result.ClientsAdded);
        Assert.Contains(result.Warnings, w => w.Contains("999"));

        var client = await fixture.Db.ClientDevices.Include(c => c.VlanDefinition).SingleAsync();
        Assert.Equal(10, client.VlanDefinition!.VlanId);
    }

    [Fact]
    public async Task ImportingSettingsIsOptIn()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        // Establish current settings.
        var current = await new SettingsService(fixture.Db).GetAsync();
        current.DefaultVlanId = 10;
        await fixture.Db.SaveChangesAsync();

        var document = new BackupDocument
        {
            Vlans = [new BackupVlan { Name = "Guest", VlanId = 30 }],
            Settings = new BackupSettings { DefaultVlanId = 30, TunnelTag = 0, SendServiceType = true }
        };

        // Default: settings untouched.
        await Service(fixture).ImportAsync(document, new ImportOptions { ImportSettings = false });
        Assert.Equal(10, (await new SettingsService(fixture.Db).GetReadOnlyAsync()).DefaultVlanId);

        // Opt in: settings applied.
        var result = await Service(fixture).ImportAsync(document, new ImportOptions { ImportSettings = true });
        Assert.True(result.SettingsApplied);
        var applied = await new SettingsService(fixture.Db).GetReadOnlyAsync();
        Assert.Equal(30, applied.DefaultVlanId);
        Assert.Equal(0, applied.TunnelTag);
        Assert.True(applied.SendServiceType);
    }

    [Fact]
    public async Task MacIdentitiesAreNormalisedOnImport()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var document = new BackupDocument
        {
            Vlans = [new BackupVlan { Name = "Default", VlanId = 10 }],
            Clients = [new BackupClient { Identity = "AABBCCDDEEFF", VlanId = 10 }]
        };

        await Service(fixture).ImportAsync(document, new ImportOptions());

        Assert.True(await fixture.Db.ClientDevices.AnyAsync(c => c.Name == "aa:bb:cc:dd:ee:ff"));
    }

    [Fact]
    public void ParsingEmptyOrMalformedContentThrowsAReadableError()
    {
        Assert.Throws<BackupFormatException>(() => BackupService.Parse(""));
        Assert.Throws<BackupFormatException>(() => BackupService.Parse("{ not valid json"));
        Assert.Throws<BackupFormatException>(() => BackupService.Parse(": : not : valid : yaml :\n\t- ["));
    }

    [Fact]
    public async Task JsonAndYamlAreBothAccepted()
    {
        await using var fixture = await SeededAsync();
        var document = await Service(fixture).ExportAsync();

        var json = Service(fixture).Serialize(document, BackupFormat.Json);
        var yaml = Service(fixture).Serialize(document, BackupFormat.Yaml);

        Assert.StartsWith("{", json.TrimStart());
        Assert.Equal(2, BackupService.Parse(json).Vlans.Count);
        Assert.Equal(2, BackupService.Parse(yaml).Vlans.Count);
        Assert.Equal("V_IOT_Generic", BackupService.Parse(yaml).Vlans.Single(v => v.VlanId == 306).Name);
    }

    [Fact]
    public async Task ExportOmitsInternalIdsAndAccountingHistory()
    {
        await using var fixture = await SeededAsync();
        var yaml = Service(fixture).Serialize(await Service(fixture).ExportAsync(), BackupFormat.Yaml);

        // References VLANs by number, not internal id, and carries no session data.
        Assert.Contains("vlanId: 306", yaml);
        Assert.DoesNotContain("accountingSession", yaml, StringComparison.OrdinalIgnoreCase);
    }
}
