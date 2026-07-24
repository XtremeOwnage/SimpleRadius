using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using SimpleRadius.Models;

namespace SimpleRadius.Services;

/// <summary>
/// Wires up admin UI authentication. The default is no authentication at all, which keeps first-run
/// setup trivial; pointing the configuration at an OpenID Connect provider turns sign-in on.
/// </summary>
public static class AuthenticationSetup
{
    public static IServiceCollection AddSimpleRadiusAuthentication(
        this IServiceCollection services,
        AuthenticationSettings settings,
        ILogger logger)
    {
        services.AddSingleton(settings);

        if (settings.Mode == AuthenticationMode.Oidc && !settings.IsOidcEnabled)
        {
            // Failing loudly beats silently serving an unauthenticated admin UI to someone who asked for OIDC.
            throw new InvalidOperationException(
                "Authentication:Mode is 'Oidc' but Authentication:Oidc:Authority or ClientId is missing. "
                + "Set both, or set Mode to 'None' to run without authentication.");
        }

        if (!settings.IsOidcEnabled)
        {
            logger.LogWarning(
                "The admin UI has no authentication. Anyone who can reach it can change VLANs, clients and "
                + "shared secrets. Restrict access at the network, or configure Authentication:Oidc.");
            return services;
        }

        var oidc = settings.Oidc;

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(options =>
            {
                options.Cookie.Name = "SimpleRadius.Auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.SlidingExpiration = true;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.AccessDeniedPath = "/Account/AccessDenied";
            })
            .AddOpenIdConnect(options =>
            {
                options.Authority = oidc.Authority;
                options.ClientId = oidc.ClientId;
                options.ClientSecret = string.IsNullOrWhiteSpace(oidc.ClientSecret) ? null : oidc.ClientSecret;
                options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
                options.CallbackPath = oidc.CallbackPath;
                options.SignedOutCallbackPath = oidc.SignedOutCallbackPath;

                // The authorization code flow with PKCE, which is what a public client should use.
                options.ResponseType = "code";
                options.UsePkce = true;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = true;
                options.MapInboundClaims = false;

                options.Scope.Clear();
                foreach (var scope in oidc.Scopes)
                {
                    options.Scope.Add(scope);
                }

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    NameClaimType = oidc.NameClaim,
                    RoleClaimType = oidc.RoleClaim
                };

                if (!string.IsNullOrWhiteSpace(oidc.PublicUrl))
                {
                    // Behind a proxy the request host is the container's, so the redirect URI has to be
                    // rewritten to the address the provider was told about.
                    var publicUrl = oidc.PublicUrl.TrimEnd('/');
                    options.Events.OnRedirectToIdentityProvider = context =>
                    {
                        context.ProtocolMessage.RedirectUri = publicUrl + oidc.CallbackPath;
                        return Task.CompletedTask;
                    };
                    options.Events.OnRedirectToIdentityProviderForSignOut = context =>
                    {
                        context.ProtocolMessage.PostLogoutRedirectUri = publicUrl + oidc.SignedOutCallbackPath;
                        return Task.CompletedTask;
                    };
                }
            });

        services.AddAuthorization(options =>
        {
            var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser();

            if (!string.IsNullOrWhiteSpace(oidc.RequiredRole))
            {
                policy = policy.RequireAssertion(context =>
                    context.User.HasClaim(c =>
                        (c.Type == oidc.RoleClaim || c.Type == ClaimTypes.Role)
                        && c.Value == oidc.RequiredRole));
            }

            // Applied to every endpoint that does not opt out, so a new page is protected by default.
            options.FallbackPolicy = policy.Build();
        });

        logger.LogInformation(
            "Admin UI authentication is on: OIDC via {Authority} as client {ClientId}{Role}",
            oidc.Authority,
            oidc.ClientId,
            string.IsNullOrWhiteSpace(oidc.RequiredRole) ? string.Empty : $", requiring role '{oidc.RequiredRole}'");

        return services;
    }
}
