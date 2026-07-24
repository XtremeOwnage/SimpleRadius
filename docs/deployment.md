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

## Upgrading

1. Read the [changelog](../CHANGELOG.md).
2. Back up the data directory.
3. Replace the image, package or binary.
4. Restart.

Settings live in the database, so they survive. **There are no schema migrations yet** — the database is
created with `EnsureCreated`, so a release that changes the schema needs a fresh database. Releases that
do this will say so prominently in the changelog.
