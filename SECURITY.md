# Security

## Reporting a vulnerability

Report privately through
[GitHub security advisories](https://github.com/XtremeOwnage/SimpleRadius/security/advisories/new).
Please do not open a public issue for anything exploitable.

This is a spare-time project with no paid maintainers and no SLA. Expect a best-effort response.

## Read this before deploying

SimpleRadius is aimed at home and lab networks. Several deliberate trade-offs favour ease of setup over
hardening, and you should know all of them before putting it anywhere that matters.

### The admin UI has no authentication by default

Out of the box, **anyone who can reach the web UI has full control**: change any device's VLAN, add or
remove clients, read every configured shared secret in plaintext, and wipe accounting history.

This is the default because it makes first-run setup trivial. It is not a safe default on an untrusted
network. Your options, best first:

1. **Turn on OpenID Connect.** See [docs/authentication.md](docs/authentication.md). The server refuses to
   start if you select OIDC and leave it half-configured, rather than quietly serving an open UI.
2. **Put it behind a reverse proxy** that enforces authentication (Authelia, oauth2-proxy, Cloudflare
   Access, or basic auth).
3. **Restrict it at the network** — bind the UI to a management interface, or firewall it.

The server logs a warning on every start while it is unauthenticated. If you see that line in production,
it is telling you something real.

### Shared secrets are stored in plaintext

RADIUS computes its digests from the original secret, so the server must be able to read it back. They are
stored as plaintext in the SQLite file and are visible to anyone with the file or admin UI access.

- Keep the database file readable only by the service account (the systemd unit and container both do).
- Do not reuse a secret you use anywhere else.
- Back it up as you would a password store.

### What RADIUS itself does not protect

These are properties of the protocol, not of this implementation:

- **Access-Request authenticators are random nonces**, not keyed digests, so a request cannot be
  authenticated on its own. The shared secret is verified through the Message-Authenticator attribute
  (RFC 3579) when the router sends one. Hardware that omits it is accepted by default; set
  `RequireMessageAuthenticator` once you have confirmed all of your gear sends it.
- **MAC addresses are trivially spoofed.** MAC-based authentication identifies a device, it does not
  prove anything. Someone who can observe a permitted MAC can present it and receive that device's VLAN.
  Treat VLAN assignment as convenience and segmentation, not as an access control boundary.
- **RADIUS is UDP with MD5-based digests.** Keep it on a management network or a VPN. Do not route it
  across the internet.

### What the implementation does do

- Packets from an unconfigured NAS are dropped, not answered, so the server does not become an oracle.
- Message-Authenticator and accounting request authenticators are verified with constant-time comparison.
- Replies are correctly signed, so a router can verify them.
- Malformed packets are rejected before any database work happens.
- The admin UI uses antiforgery tokens on every form, with Data Protection keys persisted alongside the
  database so they survive a restart.
- The container runs as a non-root user on a chiselled base image with no shell or package manager.
  The Kubernetes manifests add `readOnlyRootFilesystem` and drop all capabilities.

### Deployment checklist

- [ ] Admin UI is not reachable from an untrusted network, or OIDC is on
- [ ] Shared secrets are unique to this server
- [ ] The database file is owned by the service account and backed up
- [ ] `AutoRegisterUnknownNas` is off (it is by default) so unknown routers cannot self-register
- [ ] TLS terminates at a proxy if the UI is reachable off-host
- [ ] You have read the [limits](README.md#status-and-limits)

## Supported versions

The latest release only. Fixes go into a new release rather than being backported.
