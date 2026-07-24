# Configuration

Settings are split by whether changing one needs a restart.

- **Startup settings** — ports, paths, authentication — live in `appsettings.json` or environment
  variables and are read once at boot.
- **Runtime settings** — client policy and reply attributes — live in the database, are edited on the
  **Settings** page, and apply to the very next RADIUS packet.

Any startup setting can be supplied as an environment variable, with `__` for each level of nesting:

```bash
RadiusServerSettings__AuthPort=11812
Authentication__Oidc__ClientId=simpleradius
```

---

## Runtime settings (Settings page)

![Settings](images/settings.png)

Stored in the database, so they survive a container being replaced and need no restart.

### Client policy

| Setting | Default | What it does |
| --- | --- | --- |
| Default VLAN ID | `10` | VLAN for devices with no explicit assignment. Created automatically if no VLAN uses it, and starred on the VLANs page. |
| Auto-create unknown clients | on | Add a device on its first request instead of rejecting it. Turn off to make the client list an allow-list. |
| Auto-register unknown routers | **off** | Accept a NAS that is not configured, using the default secret below. A router whose secret you do not know cannot be authenticated, so leave this off outside testing. |
| Default shared secret | `change-me` | Only used for auto-registered routers. |
| MAC address format | `aa:bb:cc:dd:ee:ff` | Display only — see below. |

**MAC address format is cosmetic.** Inbound requests are normalised before lookup, so a router may send
any spelling and still match. Changing it never affects whether a device authenticates; it only changes
what you read in the UI. Options mirror what a UniFi or MikroTik controller offers:
`aa:bb:cc:dd:ee:ff`, `AA:BB:CC:DD:EE:FF`, `aa-bb-cc-dd-ee-ff`, `AA-BB-CC-DD-EE-FF`, `aabbccddeeff`,
`AABBCCDDEEFF`.

### Reply attributes

These exist because hardware disagrees about what an Access-Accept should contain. The defaults work with
UniFi and MikroTik; change them only if your equipment needs something else. The Settings page previews
the exact attribute set your choices produce.

| Setting | Default | What it does |
| --- | --- | --- |
| Send tunnel attributes | on | Tunnel-Type = 13, Tunnel-Medium-Type = 6, Tunnel-Private-Group-Id = the VLAN. This is the standard way to assign a VLAN. |
| Tunnel tag | `1` | Tag binding those three together (RFC 2868). `0` sends them untagged, for equipment that rejects a tag. |
| Send Egress-VLANID | off | Adds the RFC 4675 attribute. Some switches want this instead of the tunnel group. |
| Send Service-Type | off | Adds Service-Type = Framed, which a few NAS models require before applying a VLAN. |
| Require Message-Authenticator | off | Reject an Access-Request that omits the attribute. It is always verified when a router does send it; requiring it locks out older hardware that never does. |
| Interim accounting interval | `600` | Asks the router for periodic accounting updates, which is what keeps live session counters moving. `0` leaves the router's own setting alone. |

---

## Startup settings

### `RadiusServerSettings`

| Setting | Default | What it does |
| --- | --- | --- |
| `WebUiUrl` | `http://0.0.0.0:5285` | Address the admin UI binds to. Applied only if nothing else has chosen one, so `ASPNETCORE_URLS`, `--urls` and launch profiles all still win. |
| `AuthPort` | `1812` | UDP port for Access-Request. |
| `AcctPort` | `1813` | UDP port for Accounting-Request. |
| `ListenAddress` | `0.0.0.0` | Interface the UDP listeners bind to. |
| `DatabasePath` | `data/simple-radius.db` | SQLite file. Relative paths resolve against the content root. |
| `DefaultVlanName` | `Default` | Name given to the default VLAN row when it has to be created. |

Binding a port that something else holds fails outright and stops the process — deliberately, so a
half-running server never looks healthy. The log says exactly which port and why.

### `RadiusServerSettings:Seed`

Written to the database **the first time it is created, and never again**. After that the Settings page
owns these values and editing the file has no effect. They exist so a container or package can ship with
sensible starting values.

```json
"Seed": {
  "DefaultVlanId": 10,
  "AutoCreateUnknownClients": true,
  "AutoRegisterUnknownNas": false,
  "DefaultSharedSecret": "change-me",
  "RequireMessageAuthenticator": false,
  "MacAddressFormat": "ColonLower",
  "SendTunnelAttributes": true,
  "TunnelTag": 1,
  "SendEgressVlanId": false,
  "SendServiceType": false,
  "AcctInterimIntervalSeconds": 600
}
```

### `Authentication`

Off by default. See [authentication.md](authentication.md) for the full guide.

### `FileLogging` and `Logging`

See [logging.md](logging.md).

---

## Worked examples

### Container with a non-default VLAN and no auto-creation

```yaml
environment:
  RadiusServerSettings__DatabasePath: /data/simple-radius.db
  RadiusServerSettings__Seed__DefaultVlanId: "999"
  RadiusServerSettings__Seed__AutoCreateUnknownClients: "false"
```

With auto-creation off, a device must exist in the client list before it is accepted — the client list
becomes an allow-list.

### Running alongside an existing RADIUS server

```bash
RadiusServerSettings__AuthPort=11812
RadiusServerSettings__AcctPort=11813
RadiusServerSettings__WebUiUrl=http://0.0.0.0:5286
```

### Everything on a management interface only

```bash
RadiusServerSettings__ListenAddress=10.0.50.5
RadiusServerSettings__WebUiUrl=http://10.0.50.5:5285
```

---

## Where configuration is read from

Standard ASP.NET Core order, later winning:

1. `appsettings.json`
2. `appsettings.{Environment}.json`
3. Environment variables
4. Command-line arguments

One exception worth knowing: `WebUiUrl` is applied only when nothing else has set a URL, so
`ASPNETCORE_URLS`, `--urls` and a launch profile's `applicationUrl` all override it. A root `Urls` key in
`appsettings.json` is *not* used for this, because host configuration is layered before `appsettings.json`
and would silently override those.
