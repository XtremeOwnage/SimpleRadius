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

        fixture.Db.SsidVlanRules.Add(new SsidVlanRule { Ssid = "IoT", VlanDefinitionId = iot.Id, Notes = "iot wifi" });
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
        Assert.Equal(1, result.SsidRulesAdded);

        var vlan = await target.Db.VlanDefinitions.SingleAsync(v => v.VlanId == 306);
        Assert.Equal("V_IOT_Generic", vlan.Name);
        Assert.Equal("IOT", vlan.Group);

        var client = await target.Db.ClientDevices.Include(c => c.VlanDefinition).SingleAsync(c => c.Name == "44:4f:8e:c8:9a:ac");
        Assert.Equal("Office Lamp", client.Description);
        Assert.Equal("desk", client.Notes);
        Assert.Equal(306, client.VlanDefinition!.VlanId);

        var nas = await target.Db.NetworkAccessServers.SingleAsync();
        Assert.Equal("unifi-secret", nas.SharedSecret);

        var rule = await target.Db.SsidVlanRules.Include(r => r.VlanDefinition).SingleAsync();
        Assert.Equal("IoT", rule.Ssid);
        Assert.Equal(306, rule.VlanDefinition!.VlanId);
    }

    [Fact]
    public async Task AccountingIsExcludedByDefaultAndIncludedOnlyWhenRequested()
    {
        await using var fixture = await SeededAsync();
        await SeedSessionAsync(fixture, "session-1", "44:4f:8e:c8:9a:ac");

        var withoutAccounting = await Service(fixture).ExportAsync();
        Assert.Null(withoutAccounting.Sessions);

        var withAccounting = await Service(fixture).ExportAsync(includeAccounting: true);
        var session = Assert.Single(withAccounting.Sessions!);
        Assert.Equal("session-1", session.SessionId);
    }

    [Fact]
    public async Task AccountingIsRestoredOnlyWhenTheImportOptsIn()
    {
        BackupDocument document;
        await using (var source = await SeededAsync())
        {
            await SeedSessionAsync(source, "session-1", "44:4f:8e:c8:9a:ac", bytesIn: 4096);
            // Round-trip through YAML so the session timestamps survive serialisation.
            document = BackupService.Parse(Service(source).Serialize(
                await Service(source).ExportAsync(includeAccounting: true), BackupFormat.Yaml));
        }

        // Default import: config only, no sessions.
        await using (var target = await TestDatabase.CreateAsync())
        {
            var result = await Service(target).ImportAsync(document, new ImportOptions());
            Assert.Equal(0, result.SessionsImported);
            Assert.Empty(await target.Db.AccountingSessions.ToListAsync());
        }

        // Opt in: sessions restored and relinked to the imported client.
        await using (var target = await TestDatabase.CreateAsync())
        {
            var result = await Service(target).ImportAsync(document, new ImportOptions { ImportAccounting = true });
            Assert.Equal(1, result.SessionsImported);

            var session = await target.Db.AccountingSessions.SingleAsync();
            Assert.Equal("session-1", session.SessionId);
            Assert.Equal(4096, session.BytesIn);
            Assert.NotNull(session.ClientDeviceId);   // relinked to the client imported from the same file
        }
    }

    [Fact]
    public async Task ReimportingAccountingDoesNotDuplicateSessions()
    {
        await using var source = await SeededAsync();
        await SeedSessionAsync(source, "session-1", "44:4f:8e:c8:9a:ac");
        var document = await Service(source).ExportAsync(includeAccounting: true);

        await Service(source).ImportAsync(document, new ImportOptions { ImportAccounting = true });
        await Service(source).ImportAsync(document, new ImportOptions { ImportAccounting = true });

        // Keyed on (NAS address, session id), so the second import updates rather than duplicates.
        Assert.Equal(1, await source.Db.AccountingSessions.CountAsync(s => s.SessionId == "session-1"));
    }

    [Fact]
    public async Task AnSsidRuleForAMissingVlanIsSkippedWithAWarning()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var document = new BackupDocument
        {
            Vlans = [new BackupVlan { Name = "Default", VlanId = 10 }],
            SsidRules = [new BackupSsidRule { Ssid = "Guest", VlanId = 999 }]
        };

        var result = await Service(fixture).ImportAsync(document, new ImportOptions());

        Assert.Equal(0, result.SsidRulesAdded);
        Assert.Contains(result.Warnings, w => w.Contains("Guest") && w.Contains("999"));
        Assert.Empty(await fixture.Db.SsidVlanRules.ToListAsync());
    }

    private static async Task SeedSessionAsync(TestDatabase fixture, string sessionId, string client, long bytesIn = 0)
    {
        fixture.Db.AccountingSessions.Add(new AccountingSession
        {
            SessionId = sessionId,
            ClientName = client,
            NasName = "unifi",
            NasIpAddress = "192.168.1.1",
            Ssid = "IoT",
            NasIdentifier = "AP-Bedroom",
            NasPortType = "Wireless 802.11",
            IsActive = true,
            BytesIn = bytesIn
        });
        await fixture.Db.SaveChangesAsync();
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
