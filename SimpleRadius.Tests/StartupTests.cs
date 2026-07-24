using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SimpleRadius.Data;
using SimpleRadius.Services;

namespace SimpleRadius.Tests;

/// <summary>
/// Boots the real application. Service lifetimes are only validated when the environment is Development,
/// so a container that resolves fine in production can still stop the app dead on a developer machine —
/// this is the test that catches that, along with any page that throws on a plain GET.
/// </summary>
public class StartupTests : IClassFixture<SimpleRadiusApplication>
{
    private readonly SimpleRadiusApplication _application;

    public StartupTests(SimpleRadiusApplication application)
    {
        _application = application;
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/ClientDevices")]
    [InlineData("/VlanDefinitions")]
    [InlineData("/NetworkAccessServers")]
    [InlineData("/Sessions")]
    [InlineData("/Settings")]
    [InlineData("/Backup")]
    [InlineData("/VlanDefinitions?grouped=true")]
    public async Task EveryAdminPageIsServed(string path)
    {
        using var client = _application.CreateClient();

        var response = await client.GetAsync(path);

        Assert.True(response.IsSuccessStatusCode, $"GET {path} returned {(int)response.StatusCode}");
    }

    [Fact]
    public void ContainerResolvesTheServicesTheApplicationDependsOn()
    {
        using var scope = _application.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<RadiusDbContext>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<SettingsService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AccountingService>());
        Assert.NotNull(_application.Services.GetRequiredService<RadiusServerProcess>());
    }

    [Fact]
    public async Task StartupSeedsTheDefaultVlan()
    {
        using var client = _application.CreateClient();
        await client.GetAsync("/");

        using var scope = _application.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RadiusDbContext>();

        Assert.Contains(db.VlanDefinitions, v => v.VlanId == 10);
    }
}

/// <summary>Runs the application on a scratch database and ephemeral UDP ports.</summary>
public sealed class SimpleRadiusApplication : WebApplicationFactory<Program>
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"simple-radius-startup-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development is the point of the exercise: it turns on the scope validation this test guards.
        builder.UseEnvironment("Development");

        // Port 0 keeps the listener off 1812/1813, which a real server may already hold.
        builder.UseSetting("RadiusServerSettings:AuthPort", "0");
        builder.UseSetting("RadiusServerSettings:AcctPort", "0");
        builder.UseSetting("RadiusServerSettings:ListenAddress", "127.0.0.1");
        builder.UseSetting("RadiusServerSettings:DatabasePath", _databasePath);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_databasePath) + "*"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A WAL sidecar may still be held briefly; the temp directory takes care of it.
            }
        }
    }
}
