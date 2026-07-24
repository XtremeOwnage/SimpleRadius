# Changelog

All notable changes are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[semantic versioning](https://semver.org/).

## [Unreleased]

## [1.0.0] - 2026-07-23

First public release. Published as `v1.0.0-rc` for testing ahead of the final tag.

### Added

- **RADIUS authentication** with dynamic VLAN assignment. Access-Accept carries the RFC 2868 tunnel
  group (Tunnel-Type, Tunnel-Medium-Type, Tunnel-Private-Group-Id), which is what UniFi and MikroTik
  expect for dynamic VLANs.
- **Default-VLAN fallback.** An unknown device is created on first request and placed on a configurable
  default VLAN, so new hardware gets on the network and can then be reassigned.
- **MAC normalisation.** `AABBCCDDEEFF`, `aa-bb-cc-dd-ee-ff` and `AA:BB:CC:DD:EE:FF` all resolve to one
  client, so firmware differences never cause a mismatch.
- **Accounting.** Sessions open on Start, refresh on interim updates (including gigawords counters) and
  close on Stop; Accounting-On/Off from a restarted router closes what it left open.
- **Admin web UI** — dashboard, clients, VLANs, network access servers and sessions, with sortable
  tables and search.
- **Session search and aggregation**: totals for bytes and connected time, and a per-client rollup,
  computed in the database so they stay accurate when the listed rows are capped.
- **Settings page** for client policy and reply attributes, stored in the database and applied to the
  next packet without a restart. Includes a preview of the exact Access-Accept your settings produce.
- **Hardware compatibility settings**: tunnel tag (including 0 for untagged), Egress-VLANID (RFC 4675),
  Service-Type, Acct-Interim-Interval, and a MAC display format.
- **Optional OpenID Connect authentication** for the admin UI, off by default, with an optional required
  role. Selecting it while leaving it half-configured is a startup error rather than a silently open UI.
- **Configurable logging**, including an optional rolling file sink and journald integration under
  systemd.
- **Deployment**: multi-arch container image on GHCR, Kubernetes manifests, a systemd unit with an
  install script, deb and rpm packages, and self-contained binaries for Linux, Windows and macOS.
- **Health endpoint** at `/healthz` that opens the database, plus a `--healthcheck` mode so the
  shell-less container image can still run a Docker HEALTHCHECK.

### Security

- Message-Authenticator (RFC 3579) is verified whenever a router sends one, with constant-time
  comparison; accounting request authenticators are verified on every packet.
- Packets from an unconfigured or disabled NAS are discarded silently, as RFC 2865 requires.
- The container runs as a non-root user on a chiselled image with no shell or package manager; the
  Kubernetes manifests add `readOnlyRootFilesystem` and drop all capabilities.
- Data Protection keys are persisted next to the database, so antiforgery survives a restart and works
  with a read-only root filesystem.

### Known limitations

- The admin UI has no authentication unless you configure OIDC. See [SECURITY.md](SECURITY.md).
- MAC-based authentication only — no EAP, 802.1X certificates or passwords.
- Shared secrets are stored in plaintext, because RADIUS needs the original value for its digests.
- One instance only; SQLite permits a single writer.
- No schema migrations. `EnsureCreated` builds the database, so a schema change needs a fresh one.

[Unreleased]: https://github.com/XtremeOwnage/SimpleRadius/compare/v1.0.0-rc...HEAD
[1.0.0]: https://github.com/XtremeOwnage/SimpleRadius/releases/tag/v1.0.0
