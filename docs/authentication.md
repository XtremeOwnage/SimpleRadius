# Admin UI authentication

By default there is **no sign-in**. Anyone who can reach the web UI can change VLAN assignments, add or
remove clients, and read every shared secret in plaintext.

That default exists to make first-run setup trivial. It is not safe on an untrusted network. The server
logs a warning on every start while it is unauthenticated:

```
warn: The admin UI has no authentication. Anyone who can reach it can change VLANs, clients and
      shared secrets. Restrict access at the network, or configure Authentication:Oidc.
```

You have three options, and any of them is better than none:

1. **OpenID Connect** — built in, covered below.
2. **A reverse proxy that authenticates** — Authelia, oauth2-proxy, Cloudflare Access, or plain basic
   auth. Nothing to configure here; just do not expose the app's port directly.
3. **Network restriction** — bind the UI to a management interface (`RadiusServerSettings:WebUiUrl`) or
   firewall it.

---

## OpenID Connect

Works with any compliant provider: Keycloak, Authentik, Auth0, Entra ID, Google, Okta, Zitadel.

### Minimum configuration

```json
{
  "Authentication": {
    "Mode": "Oidc",
    "Oidc": {
      "Authority": "https://idp.example.com/realms/home",
      "ClientId": "simpleradius",
      "ClientSecret": ""
    }
  }
}
```

Or as environment variables:

```bash
Authentication__Mode=Oidc
Authentication__Oidc__Authority=https://idp.example.com/realms/home
Authentication__Oidc__ClientId=simpleradius
```

Restart, and every page requires a sign-in. `/healthz` stays anonymous so orchestrator probes are not
redirected to a login page.

> If you set `Mode` to `Oidc` and leave `Authority` or `ClientId` empty, **the server refuses to start**.
> Falling back to an open admin UI when you explicitly asked for authentication would be the dangerous
> outcome, so it fails loudly instead.

### At your provider

Register a client with:

- **Redirect URI**: `https://your-address/signin-oidc`
- **Post-logout redirect URI**: `https://your-address/signout-callback-oidc`
- **Grant type**: authorization code (PKCE is used automatically)
- **Client type**: public is fine — leave `ClientSecret` empty. For a confidential client, set it.

### All settings

| Setting | Default | What it does |
| --- | --- | --- |
| `Mode` | `None` | `None` or `Oidc`. |
| `Oidc:Authority` | — | Issuer URL. Discovery reads `{Authority}/.well-known/openid-configuration`. |
| `Oidc:ClientId` | — | Client identifier from your provider. |
| `Oidc:ClientSecret` | empty | Leave empty for a public client using PKCE. |
| `Oidc:Scopes` | `openid`, `profile`, `email` | Scopes requested. |
| `Oidc:RequireHttpsMetadata` | `true` | Only turn off for a provider on a private network with a self-signed certificate. It disables TLS verification of discovery. |
| `Oidc:CallbackPath` | `/signin-oidc` | Must match the provider's redirect URI. |
| `Oidc:SignedOutCallbackPath` | `/signout-callback-oidc` | Must match the post-logout redirect URI. |
| `Oidc:NameClaim` | `name` | Claim shown as the signed-in operator's name. |
| `Oidc:RoleClaim` | `roles` | Claim inspected for `RequiredRole`. |
| `Oidc:RequiredRole` | empty | When set, a token must carry this role. Empty accepts anyone the provider authenticates. |
| `Oidc:PublicUrl` | empty | Set when behind a reverse proxy — see below. |

### Restricting to a role

Authenticating is not the same as authorising. Without `RequiredRole`, **anyone your provider can
authenticate gets full control**. On a family Keycloak realm, that is everyone.

```bash
Authentication__Oidc__RequiredRole=radius-admin
Authentication__Oidc__RoleClaim=roles
```

Someone who signs in without that role gets an access-denied page rather than a redirect loop.

The claim name varies: Keycloak uses `roles` (with a mapper) or `realm_access.roles`, Entra ID uses
`roles`, Auth0 typically a namespaced claim such as `https://example.com/roles`. Check a decoded token
from your provider if the role is not matching.

### Behind a reverse proxy

The app sees the proxy's request, not the browser's, so redirect URIs get built with the internal
address. Point it at the public one:

```bash
Authentication__Oidc__PublicUrl=https://radius.example.com
```

`X-Forwarded-For` and `X-Forwarded-Proto` are honoured, so your proxy should send both.

---

## Worked example: Keycloak

1. In your realm, **Clients → Create client**
   - Client ID: `simpleradius`
   - Client authentication: **off** (public client with PKCE)
   - Valid redirect URIs: `https://radius.example.com/signin-oidc`
   - Valid post-logout redirect URIs: `https://radius.example.com/signout-callback-oidc`
2. **Realm roles → Create role** → `radius-admin`, and assign it to the users who should have access.
3. **Client scopes → simpleradius-dedicated → Add mapper → By configuration → User Realm Role**
   - Token Claim Name: `roles`
   - Add to ID token and access token: on
4. Configure SimpleRadius:

```yaml
environment:
  Authentication__Mode: Oidc
  Authentication__Oidc__Authority: https://keycloak.example.com/realms/home
  Authentication__Oidc__ClientId: simpleradius
  Authentication__Oidc__RequiredRole: radius-admin
  Authentication__Oidc__PublicUrl: https://radius.example.com
```

## Worked example: Authentik

1. **Applications → Providers → Create → OAuth2/OpenID Provider**
   - Client type: Public
   - Redirect URI: `https://radius.example.com/signin-oidc`
2. Create an Application bound to that provider, and restrict it with a policy or group binding.
3. Use the provider's issuer URL as `Authority`.

---

## Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| Server will not start, complains about Authority | `Mode` is `Oidc` but `Authority` or `ClientId` is empty. |
| Redirected to the provider, then an error about redirect_uri | The registered URI does not match. Behind a proxy, set `PublicUrl`. |
| Redirect loop | Cookies not surviving the round trip. Check the proxy forwards `X-Forwarded-Proto`. |
| "Access denied" after a successful sign-in | `RequiredRole` is set and the token lacks it, or `RoleClaim` names the wrong claim. |
| Discovery fails over TLS | Self-signed certificate on the provider. Fix the trust chain, or set `RequireHttpsMetadata: false` on a private network only. |
| Health probes redirect to the provider | Should not happen — `/healthz` is explicitly anonymous. Please open an issue. |

Raise the log level to see the handshake:

```bash
Logging__LogLevel__Microsoft.AspNetCore.Authentication=Debug
```
