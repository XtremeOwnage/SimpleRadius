using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SimpleRadius.Models;
using SimpleRadius.Services;

namespace SimpleRadius.Tests;

public class AuthenticationSetupTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    [Fact]
    public void AuthenticationIsOffByDefault()
    {
        var settings = new AuthenticationSettings();

        Assert.Equal(AuthenticationMode.None, settings.Mode);
        Assert.False(settings.IsOidcEnabled);
    }

    [Fact]
    public void NoAuthenticationServicesAreAddedWhenModeIsNone()
    {
        var services = new ServiceCollection();
        services.AddSimpleRadiusAuthentication(new AuthenticationSettings(), Logger);

        // No authentication scheme is registered, so every page stays anonymous.
        Assert.DoesNotContain(services, d => d.ServiceType.FullName?.Contains("IAuthenticationService") == true);
    }

    [Theory]
    [InlineData("", "client")]
    [InlineData("https://idp.example.com", "")]
    public void SelectingOidcWithoutAuthorityOrClientIdFailsLoudly(string authority, string clientId)
    {
        var settings = new AuthenticationSettings
        {
            Mode = AuthenticationMode.Oidc,
            Oidc = { Authority = authority, ClientId = clientId }
        };

        // Silently falling back to an unauthenticated admin UI would be the dangerous outcome here.
        var error = Assert.Throws<InvalidOperationException>(
            () => new ServiceCollection().AddSimpleRadiusAuthentication(settings, Logger));

        Assert.Contains("Authority", error.Message);
    }

    [Fact]
    public void FullyConfiguredOidcRegistersAuthenticationAndAFallbackPolicy()
    {
        var settings = new AuthenticationSettings
        {
            Mode = AuthenticationMode.Oidc,
            Oidc =
            {
                Authority = "https://idp.example.com",
                ClientId = "simpleradius",
                RequiredRole = "radius-admin"
            }
        };

        Assert.True(settings.IsOidcEnabled);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSimpleRadiusAuthentication(settings, Logger);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Authorization.AuthorizationOptions>>();

        Assert.NotNull(options.Value.FallbackPolicy);
    }
}

public class FileLoggingTests
{
    [Fact]
    public void FileLoggingIsOffByDefault()
    {
        Assert.False(new FileLoggingSettings().Enabled);
    }

    [Fact]
    public void EnabledFileLoggingWritesAndPrunes()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"simpleradius-logs-{Guid.NewGuid():N}");

        try
        {
            var settings = new FileLoggingSettings { Enabled = true, RetainedFileCount = 2 };
            using (var provider = new FileLoggerProviderAccessor(directory, settings))
            {
                provider.Log("hello from the test");
            }

            var files = Directory.GetFiles(directory, "simpleradius-*.log");
            Assert.Single(files);
            Assert.Contains("hello from the test", File.ReadAllText(files[0]));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>The provider is internal to the app; this drives it through the public logging surface.</summary>
    private sealed class FileLoggerProviderAccessor : IDisposable
    {
        private readonly ILoggerFactory _factory;

        public FileLoggerProviderAccessor(string directory, FileLoggingSettings settings)
        {
            _factory = LoggerFactory.Create(builder =>
                builder.AddSimpleRadiusFileLogging(
                    new FileLoggingSettings
                    {
                        Enabled = settings.Enabled,
                        Directory = directory,
                        MaxFileSizeMegabytes = settings.MaxFileSizeMegabytes,
                        RetainedFileCount = settings.RetainedFileCount
                    },
                    contentRootPath: Path.GetTempPath()));
        }

        public void Log(string message) => _factory.CreateLogger("Test").LogInformation("{Message}", message);

        public void Dispose() => _factory.Dispose();
    }
}
