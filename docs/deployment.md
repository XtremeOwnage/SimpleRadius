# Deployment

Every method below results in the same thing: one process listening on three ports, and one SQLite file
holding all state.

| Port | Protocol | Purpose |
| --- | --- | --- |
| 8080 (container) / 5285 (elsewhere) | TCP | Admin web UI |
| 1812 | UDP | RADIUS authentication |
| 1813 | UDP | RADIUS accounting |

Whichever you choose, **back up the data directory**. It holds the database, the Data Protection keys and
every setting you configure.

---

## Docker

```bash
docker run -d --name simpleradius \
  -p 8080:8080 -p 1812:1812/udp -p 1813:1813/udp \
  -v simpleradius-data:/data \
  --restart unless-stopped \
  ghcr.io/xtremeownage/simpleradius:latest
```

Images are published for `linux/amd64` and `linux/arm64`. Tags: `latest`, `1`, `1.2`, `1.2.3`.

The image is built on the chiselled .NET runtime — no shell, no package manager, non-root by default.
That is also why the health check runs the app in probe mode (`dotnet SimpleRadius.dll --healthcheck`)
rather than calling curl.

## Docker Compose

The repository ships a [`docker-compose.yml`](../docker-compose.yml):

```bash
docker compose up -d
```

To use the published image instead of building locally, replace `build: .` with
`image: ghcr.io/xtremeownage/simpleradius:latest`.

---

## Kubernetes

```bash
kubectl apply -k deploy/k8s
kubectl -n simpleradius port-forward svc/simpleradius-web 8080:80
```

Manifests are in [`deploy/k8s/`](../deploy/k8s/): a namespace, a ConfigMap, a StatefulSet with a volume
claim, and separate services for the web UI and RADIUS.

### The one thing that will bite you

A NAS is identified by the **source address** of its packets. Under the default `Cluster` external traffic
policy, the load balancer source-NATs traffic to the node address — every router then looks like the same
unconfigured client, and the server drops all of it.

[`service-radius.yaml`](../deploy/k8s/service-radius.yaml) therefore sets:

```yaml
externalTrafficPolicy: Local
```

The trade-off is that traffic only reaches nodes running the pod. Do not remove it without understanding
what breaks.

### Other notes

- **A StatefulSet with one replica**, not a Deployment. SQLite permits one writer and the RADIUS state is
  shared, so do not scale up.
- The web service is a **ClusterIP** because the admin UI has no authentication of its own. Port-forward
  to it, or put an ingress in front that enforces access control.
- The pod runs as UID 1654 with a read-only root filesystem and all capabilities dropped.
- Web and RADIUS are separate services because several load balancer implementations refuse a single
  service that mixes TCP and UDP.

---

## Linux with systemd

### From a deb or rpm package

```bash
# Debian / Ubuntu
curl -fsSLO https://github.com/XtremeOwnage/SimpleRadius/releases/latest/download/simpleradius_amd64.deb
sudo dpkg -i simpleradius_*.deb

# RHEL / Fedora
curl -fsSLO https://github.com/XtremeOwnage/SimpleRadius/releases/latest/download/simpleradius_x86_64.rpm
sudo rpm -i simpleradius_*.rpm

sudo systemctl enable --now simpleradius
```

The package creates the `simpleradius` service account, the state directory and a starter configuration
at `/etc/simpleradius/simpleradius.env`. Your edits to that file are preserved across upgrades.

### From a release tarball

```bash
curl -fsSL -o simpleradius.tar.gz \
  https://github.com/XtremeOwnage/SimpleRadius/releases/latest/download/simpleradius-linux-x64.tar.gz
tar xzf simpleradius.tar.gz
cd simpleradius-*/
sudo ./deploy/systemd/install.sh
```

Re-run the script to upgrade in place; it leaves the database and configuration alone.

### Afterwards

```bash
systemctl status simpleradius
journalctl -u simpleradius -f
sudo nano /etc/simpleradius/simpleradius.env   # then: systemctl restart simpleradius
```

| Path | Contents |
| --- | --- |
| `/opt/simpleradius` | The application |
| `/etc/simpleradius/simpleradius.env` | Configuration |
| `/var/lib/simpleradius` | Database, Data Protection keys, optional logs |

The unit runs as a dedicated account with `ProtectSystem=strict`, `NoNewPrivileges` and a writable path
limited to the state directory. Ports 1812/1813 are above 1024, so nothing privileged is needed; if you
move the admin UI to port 80 or 443, add `AmbientCapabilities=CAP_NET_BIND_SERVICE`.

---

## Standalone binary

Self-contained downloads need no .NET runtime. Available for `linux-x64`, `linux-arm64`, `linux-arm`,
`win-x64`, `win-arm64` and `osx-arm64`.

```bash
tar xzf simpleradius-*-linux-x64.tar.gz
cd simpleradius-*/
./SimpleRadius
```

On Windows, unzip and run `SimpleRadius.exe`.

They are around 100 MB, which deserves an explanation: trimming and Native AOT would cut that
dramatically, but both Razor Pages and EF Core declare themselves incompatible with them, and shipping an
unsupported configuration in an authentication server is not a good trade. The container image and
build-from-source paths use a framework-dependent publish instead, at about 10 MB.

Verify a download against its checksum:

```bash
sha256sum -c simpleradius-1.2.3-linux-x64.tar.gz.sha256
```

---

## From source

```bash
git clone https://github.com/XtremeOwnage/SimpleRadius.git
cd SimpleRadius
dotnet run --project SimpleRadius
```

For a deployable build, **always pass a runtime identifier**:

```bash
dotnet publish SimpleRadius/SimpleRadius.csproj -c Release -r linux-x64 --self-contained false -o out
```

Without `-r`, the SQLite bundle ships native binaries for every platform it supports — riscv64, mips64,
s390x, iOS simulator — turning a 10 MB publish into 103 MB. The build warns you if you forget.

---

## Behind a reverse proxy

Only the web UI goes through a proxy; RADIUS is UDP and must reach the server directly.

```nginx
location / {
    proxy_pass http://127.0.0.1:5285;
    proxy_set_header Host              $host;
    proxy_set_header X-Forwarded-For   $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
}
```

The app honours `X-Forwarded-For` and `X-Forwarded-Proto`. When using OIDC behind a proxy, set
`Authentication:Oidc:PublicUrl` to the externally visible address so redirect URIs are built correctly —
see [authentication.md](authentication.md).

---

## Connecting a MikroTik router (RouterOS CLI)

This configures a MikroTik as a RADIUS **client** of SimpleRadius — it does not run SimpleRadius, which is
a .NET service, not a RouterOS package. Commands are RouterOS 7; note the version caveats where they
apply. Replace `192.0.2.10` with the SimpleRadius host and `SR-SECRET` with the shared secret.

First add the router under **NAS** in the SimpleRadius UI, using the source IP its RADIUS packets arrive
from, with the same secret. Requests from any other address are dropped.

### 1. Point RouterOS at SimpleRadius

```routeros
/radius
add service=wireless,dot1x address=192.0.2.10 secret="SR-SECRET" timeout=2s
```

Include only the services you use (`wireless`, `dot1x`, `dhcp`, `hotspot`, `login`, `ppp`, `ipsec`).

SimpleRadius identifies a NAS by the **source address** of its packets. If the router has several
interfaces, pin the one SimpleRadius knows so it is not identified by the wrong address:

```routeros
/radius set [find address=192.0.2.10] src-address=192.0.2.1
```

RADIUS accounting is sent automatically for a service once its RADIUS server is set, which is what
populates the Sessions page. SimpleRadius does not send CoA / Disconnect-Message, so there is no need to
enable `/radius incoming`.

### 2a. Wired MAC authentication (802.1X server)

For switch ports, RouterOS 7's dot1x server can authenticate by MAC:

```routeros
/interface dot1x server
add interface=ether2 auth-types=mac-auth \
    radius-mac-format=XX:XX:XX:XX:XX:XX \
    accounting=yes interim-update=5m
```

The MAC format here is cosmetic: SimpleRadius normalises every spelling before lookup, so any
`radius-mac-format` matches. Add `dot1x` to `auth-types` if you also want certificate-based supplicants.

### 2b. Wireless MAC authentication

On the legacy `wireless` package (RouterOS 6, or 7 without wifiwave2):

```routeros
/interface wireless security-profiles
add name=sr-mac mode=none \
    radius-mac-authentication=yes \
    radius-mac-mode=as-username \
    radius-mac-format=XX:XX:XX:XX:XX:XX
/interface wireless
set wlan1 security-profile=sr-mac vlan-mode=use-tag
```

The newer `wifi` (wifiwave2) package configures RADIUS MAC auth differently and by RouterOS version;
check the MikroTik documentation for your exact build. Either way, the `/radius` entry above is unchanged.

### 3. Let the RADIUS reply set the VLAN

SimpleRadius returns the VLAN in the RFC 2868 tunnel group — `Tunnel-Type = 13`,
`Tunnel-Medium-Type = 6`, `Tunnel-Private-Group-Id = <vlan>` — which is exactly what RouterOS reads. For
the router to act on it, the port or interface must be a member of a bridge with VLAN filtering on:

```routeros
/interface bridge
set bridge1 vlan-filtering=yes
```

With that, an accepted device is placed on the VLAN from the reply; an untagged tunnel tag is available on
the SimpleRadius **Settings** page for the rare firmware that rejects a tag.

### 4. Verify

```routeros
/radius monitor 0            ;# live request/accept/reject counters
/log print where topics~"radius"
```

A device that connects should appear under **Clients** in SimpleRadius, and its session under
**Sessions**. If nothing arrives, work through the checklist in
[logging.md](logging.md#debugging-a-device-that-will-not-connect) — the most common cause is a source
address that does not match the NAS entry.

---

## Upgrading

1. Read the [changelog](../CHANGELOG.md).
2. Back up the data directory.
3. Replace the image, package or binary.
4. Restart.

Settings live in the database, so they survive. **There are no schema migrations yet** — the database is
created with `EnsureCreated`, so a release that changes the schema needs a fresh database. Releases that
do this will say so prominently in the changelog.
