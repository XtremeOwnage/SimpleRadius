# Contributing

Thanks for taking a look. Issues, bug reports and pull requests are all welcome.

## Getting set up

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Nothing else.

```bash
git clone https://github.com/XtremeOwnage/SimpleRadius.git
cd SimpleRadius
dotnet test          # 100+ tests, about a second
dotnet run --project SimpleRadius
```

The admin UI comes up on <http://localhost:5285> and the RADIUS listeners on UDP 1812/1813.

In VS Code, press F5. Two launch profiles are defined in
[`SimpleRadius/Properties/launchSettings.json`](SimpleRadius/Properties/launchSettings.json):

| Profile | Web UI | RADIUS | Database |
| --- | --- | --- | --- |
| `http` | 5285 | 1812 / 1813 | `data/simple-radius.db` |
| `alternate-ports` | 5286 | 11812 / 11813 (loopback) | `data/simple-radius-debug.db` |

They share no port, so both can run at once. Use `alternate-ports` when a real RADIUS server already holds
1812/1813 — binding a taken port fails outright and stops the host.

## Layout

| Path | What lives there |
| --- | --- |
| [`SimpleRadius/Radius/`](SimpleRadius/Radius/) | Packet parsing, authenticators, attribute encoding, MAC normalisation |
| [`SimpleRadius/Services/`](SimpleRadius/Services/) | Policy, accounting, the request handler, the UDP host, auth and logging setup |
| [`SimpleRadius/Models/`](SimpleRadius/Models/) | Entities plus the startup and runtime settings classes |
| [`SimpleRadius/Pages/`](SimpleRadius/Pages/) | Razor Pages admin UI |
| [`SimpleRadius.Tests/`](SimpleRadius.Tests/) | Everything below |
| [`deploy/`](deploy/) | Kubernetes manifests and the systemd unit |

Two pieces are worth knowing before you change anything:

- [`RadiusRequestHandler`](SimpleRadius/Services/RadiusRequestHandler.cs) holds the entire request path
  and touches no sockets, so the whole protocol flow can be driven from a test.
- [`RadiusServerProcess`](SimpleRadius/Services/RadiusServerProcess.cs) owns only the two UDP sockets.

Keep that split. It is why the tests can cover the interesting behaviour without any network setup.

## Tests

```bash
dotnet test
```

Covers packet encoding and authenticator digests against the RFC definitions, malformed input, the
default-VLAN fallback, disabled clients and routers, wrong shared secrets, the accounting lifecycle, the
attribute set each compatibility toggle produces, MAC formatting, sorting, session search and aggregation,
the admin page queries, authentication wiring, a test that boots the whole app and requests every page,
and two that drive the real listener over loopback UDP.

Please add a test with a behaviour change. A few things worth knowing:

- Tests run against **real SQLite**, not an in-memory provider, because several bugs found in this project
  were LINQ-to-SQLite translation failures that an in-memory provider happily accepts.
- `StartupTests` boots the application in the **Development** environment on purpose: DI scope validation
  only runs there, and it has already caught a container misconfiguration that worked fine in Production.
- `TestDatabase` gives you an isolated database with the real schema, including unique indexes.

## Style

Match the surrounding code. A few conventions that are consistent throughout:

- Comments explain **why**, not what. If a line looks odd, say what forced it — a protocol requirement, a
  provider limitation, a hardware quirk.
- Cite the RFC and section when implementing protocol behaviour.
- Prefer clear names over short ones.
- Nullable reference types are on. Do not silence warnings with `!` unless you can justify it.

## Pull requests

1. Branch from `main`.
2. Make sure `dotnet test` passes.
3. Describe what changed and why. Mention hardware you tested against — that is genuinely useful here.
4. One logical change per PR where you can manage it.

CI runs build, tests, a vulnerable-package scan, a publish-size guard, and a container build with a smoke
test. It has to be green.

### The publish-size guard

CI fails if the publish output exceeds 40 MB. This is not arbitrary: the SQLite bundle ships native
binaries for every platform it supports — riscv64, mips64, s390x, iOS simulator — which is about 84 MB of
a 103 MB publish. A RID-specific publish keeps only the one that matters and lands around 10 MB. If that
check fires, something dropped the `RuntimeIdentifier`.

## Versioning

[Semantic versioning](https://semver.org/), with the version in exactly one place:
[`Directory.Build.props`](Directory.Build.props).

| Change | Bump |
| --- | --- |
| Bug fix, no behaviour change | patch — `1.2.3` → `1.2.4` |
| New setting or feature, existing configs keep working | minor — `1.2.3` → `1.3.0` |
| A schema change, a renamed setting, a changed default | major — `1.2.3` → `2.0.0` |

Until 1.0.0, minor versions may still break things; the changelog will say so.

### Releasing

```bash
# Update CHANGELOG.md first, then:
git tag -a v1.2.3 -m "v1.2.3"
git push origin v1.2.3
```

The tag drives everything. [`release.yml`](.github/workflows/release.yml) derives the version from it and
stamps it into the assemblies, the container labels and tags, the archive names and the deb/rpm metadata,
so a release cannot ship mismatched numbers. A tag containing a hyphen (`v1.2.3-rc.1`) is published as a
pre-release and does not move the `latest` container tag.

Artifacts produced per release: multi-arch container image on GHCR, self-contained binaries for linux
x64/arm64/arm, win x64/arm64 and osx-arm64, plus deb and rpm packages — each with a SHA-256 checksum.

## Things that would genuinely help

- **Hardware reports.** Which switches and access points work, and which need which reply-attribute
  settings. Compatibility is the hardest thing to test alone.
- **EF Core migrations.** The schema is currently created with `EnsureCreated`, so a schema change means
  a fresh database. This is the biggest known gap.
- **CoA / Disconnect-Message** support (RFC 5176), to move a device's VLAN without making it reconnect.
- Test coverage for anything you find under-covered.

## Code of conduct

Be decent to each other. Harassment or personal attacks are not welcome, and maintainers will remove
comments, commits and issues that cross that line.
