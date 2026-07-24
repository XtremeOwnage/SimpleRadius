using SimpleRadius.Models;

namespace SimpleRadius;

/// <summary>
/// Lets the process check its own health: "SimpleRadius --healthcheck" calls /healthz and exits 0 or 1.
/// The container image has no shell and no curl, so this is what makes a Docker HEALTHCHECK possible
/// without adding a package manager and its attack surface back to the image.
/// </summary>
public static class HealthCheckCommand
{
    public const string Argument = "--healthcheck";

    public static bool IsRequested(string[] args) =>
        args.Any(a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunAsync(RadiusServerSettings settings)
    {
        var url = BuildProbeUrl(settings.WebUiUrl);

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                return 0;
            }

            await Console.Error.WriteLineAsync($"{url} returned {(int)response.StatusCode}");
            return 1;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"{url} could not be reached: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Turns the configured listen address into something connectable. A wildcard bind such as
    /// http://0.0.0.0:8080 cannot be used as a destination, so it becomes loopback on the same port.
    /// </summary>
    internal static string BuildProbeUrl(string webUiUrl)
    {
        var candidate = webUiUrl.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();

        if (string.IsNullOrEmpty(candidate) || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            return "http://127.0.0.1:8080/healthz";
        }

        var host = uri.Host is "0.0.0.0" or "+" or "*" or "[::]" or "::" ? "127.0.0.1" : uri.Host;
        return $"{uri.Scheme}://{host}:{uri.Port}/healthz";
    }
}
