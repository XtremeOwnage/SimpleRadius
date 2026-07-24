namespace SimpleRadius.Models;

/// <summary>How the admin UI authenticates operators.</summary>
public enum AuthenticationMode
{
    /// <summary>No sign-in. Anyone who can reach the site has full control. This is the default.</summary>
    None,

    /// <summary>Sign in through an OpenID Connect provider.</summary>
    Oidc
}

/// <summary>
/// Admin UI authentication, bound from the "Authentication" section of appsettings.json. Startup only:
/// changing it needs a restart, because the whole authentication pipeline is built from it.
/// </summary>
public class AuthenticationSettings
{
    public AuthenticationMode Mode { get; set; } = AuthenticationMode.None;

    public OidcSettings Oidc { get; set; } = new();

    /// <summary>True when the configuration is complete enough to build an OIDC pipeline.</summary>
    public bool IsOidcEnabled =>
        Mode == AuthenticationMode.Oidc
        && !string.IsNullOrWhiteSpace(Oidc.Authority)
        && !string.IsNullOrWhiteSpace(Oidc.ClientId);
}

public class OidcSettings
{
    /// <summary>Issuer URL, e.g. https://keycloak.example.com/realms/home.</summary>
    public string Authority { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    /// <summary>Leave empty for a public client using PKCE.</summary>
    public string ClientSecret { get; set; } = string.Empty;

    public string[] Scopes { get; set; } = ["openid", "profile", "email"];

    /// <summary>
    /// Only turn this off for a provider on a private network with a self-signed certificate. It disables
    /// TLS verification of the discovery document.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    public string CallbackPath { get; set; } = "/signin-oidc";

    public string SignedOutCallbackPath { get; set; } = "/signout-callback-oidc";

    /// <summary>Claim shown as the signed-in operator's name.</summary>
    public string NameClaim { get; set; } = "name";

    /// <summary>Claim inspected for <see cref="RequiredRole"/>.</summary>
    public string RoleClaim { get; set; } = "roles";

    /// <summary>
    /// When set, a token must carry this role or the operator is refused. Leave empty to accept anyone
    /// the provider authenticates.
    /// </summary>
    public string RequiredRole { get; set; } = string.Empty;

    /// <summary>
    /// Set this when running behind a reverse proxy that terminates TLS, so redirect URIs are built with
    /// the public address rather than the container's own. Example: https://radius.example.com.
    /// </summary>
    public string PublicUrl { get; set; } = string.Empty;
}
