using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimpleRadius;
using SimpleRadius.Data;
using SimpleRadius.Models;
using SimpleRadius.Services;

var builder = WebApplication.CreateBuilder(args);

var settings = builder.Configuration
    .GetSection("RadiusServerSettings")
    .Get<RadiusServerSettings>() ?? new RadiusServerSettings();

// Probing mode exits before any listener is started, so it never competes for the RADIUS ports.
if (HealthCheckCommand.IsRequested(args))
{
    return await HealthCheckCommand.RunAsync(settings);
}

var authentication = builder.Configuration
    .GetSection("Authentication")
    .Get<AuthenticationSettings>() ?? new AuthenticationSettings();

var fileLogging = builder.Configuration
    .GetSection("FileLogging")
    .Get<FileLoggingSettings>() ?? new FileLoggingSettings();

builder.Logging.AddSimpleRadiusFileLogging(fileLogging, builder.Environment.ContentRootPath);

// No-ops unless the process is actually started by systemd, where it enables Type=notify readiness
// signalling and switches console output to the priority prefixes journald understands.
builder.Host.UseSystemd();

// Keeps the admin UI's address in appsettings.json beside the RADIUS ports. A root "Urls" key cannot be
// used for this: host configuration is layered before appsettings.json, so it would override
// ASPNETCORE_URLS and silently ignore the port a launch profile asked for.
if (string.IsNullOrWhiteSpace(builder.Configuration["Urls"]) && !string.IsNullOrWhiteSpace(settings.WebUiUrl))
{
    builder.WebHost.UseUrls(settings.WebUiUrl);
}

var connectionString = new SqliteConnectionStringBuilder
{
    DataSource = settings.DatabasePath,
    ForeignKeys = true,
    // Retries on SQLITE_BUSY so listener writes and admin page writes do not collide.
    DefaultTimeout = 30
}.ToString();

builder.Services.AddSingleton(settings);
builder.Services.AddRazorPages();

using (var startupLoggerFactory = LoggerFactory.Create(logging => logging.AddConsole()))
{
    builder.Services.AddSimpleRadiusAuthentication(
        authentication,
        startupLoggerFactory.CreateLogger("SimpleRadius.Authentication"));
}

// Antiforgery tokens are protected with these keys. Keeping them beside the database means they survive
// a restart, and that a container with a read-only root filesystem still has somewhere to write them.
var keyDirectory = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(settings.DatabasePath)) ?? ".",
    "keys");
Directory.CreateDirectory(keyDirectory);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory))
    .SetApplicationName("SimpleRadius");

// The listener is a singleton and creates a context per datagram, so the factory owns the options.
// Pages get a per-request context from that same factory, which keeps one options registration and
// keeps the container valid under the scope validation that runs in Development.
builder.Services.AddDbContextFactory<RadiusDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<RadiusDbContext>>().CreateDbContext());
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<AccountingService>();
builder.Services.AddSingleton<RadiusServerProcess>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RadiusServerProcess>());

var app = builder.Build();

await DatabaseInitializer.InitializeAsync(app.Services);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

// Trust the proxy headers so redirect URIs and logged client addresses are the real ones.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseRouting();

if (authentication.IsOidcEnabled)
{
    app.UseAuthentication();
}

app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

// Liveness/readiness for containers: confirms the database is reachable, not merely that Kestrel is up.
// Anonymous even when sign-in is on, so an orchestrator's probe is never redirected to a login page.
app.MapGet("/healthz", async (RadiusDbContext db, CancellationToken cancellationToken) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? Results.Ok("healthy")
            : Results.Problem("database unavailable", statusCode: StatusCodes.Status503ServiceUnavailable))
    .AllowAnonymous();

app.Run();

return 0;
