# Logging

Console logging is always on. A rolling file sink is available if you want logs on disk without an
external collector.

## Where logs go

| Deployment | Where to look |
| --- | --- |
| Container | `docker logs simpleradius` / `kubectl logs -n simpleradius statefulset/simpleradius` |
| systemd | `journalctl -u simpleradius -f` |
| Standalone | The console it was started from |
| File sink | The directory set in `FileLogging:Directory` |

Under systemd the app detects it is being supervised and switches console output to the priority prefixes
journald understands, so `journalctl -p warning -u simpleradius` filters correctly.

## Levels

Standard ASP.NET Core configuration:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore.Database.Command": "Warning",
      "SimpleRadius": "Information"
    }
  }
}
```

Or as environment variables:

```bash
Logging__LogLevel__Default=Information
Logging__LogLevel__SimpleRadius=Debug
```

Levels, quietest first: `None`, `Critical`, `Error`, `Warning`, `Information`, `Debug`, `Trace`.

### Useful categories

| Category | What it covers |
| --- | --- |
| `SimpleRadius.Services.RadiusRequestHandler` | Every accept, reject and discard, with the reason |
| `SimpleRadius.Services.AccountingService` | Session start, interim and stop |
| `SimpleRadius.Services.RadiusPolicyService` | Client creation and NAS auto-registration |
| `SimpleRadius.Services.RadiusServerProcess` | Socket binding and listener errors |
| `Microsoft.EntityFrameworkCore.Database.Command` | Every SQL statement — noisy, `Warning` by default |
| `Microsoft.AspNetCore.Authentication` | The OIDC handshake |

## What normal operation looks like

```
info: SimpleRadius.Services.RadiusServerProcess[0]
      RADIUS listening on 0.0.0.0: authentication UDP 1812, accounting UDP 1813
info: SimpleRadius.Services.RadiusPolicyService[0]
      Created client aa:bb:cc:dd:ee:ff on default VLAN 10
info: SimpleRadius.Services.RadiusRequestHandler[0]
      Accepted aa:bb:cc:dd:ee:ff from UniFi gateway on VLAN 10
info: SimpleRadius.Services.AccountingService[0]
      Accounting Start for session 8A2F… (aa:bb:cc:dd:ee:ff) on UniFi gateway
```

## Messages worth reacting to

| Message | Meaning |
| --- | --- |
| `The admin UI has no authentication` | Expected until you configure OIDC. If you see it in production, act on it. |
| `Discarded … from unconfigured NAS x.x.x.x` | A router is sending requests but is not in the NAS list. Add it. |
| `Message-Authenticator failed, the shared secret does not match` | Mismatched secret between router and server. |
| `request authenticator failed` | Same, on the accounting port. |
| `Auto-registered NAS … with the default shared secret` | Auto-registration is on. Set the real secret, then turn it off. |
| `Cannot bind the … listener` | Another process holds the port. The message names it. |

## File logging

Off by default:

```json
{
  "FileLogging": {
    "Enabled": true,
    "Directory": "logs",
    "MaxFileSizeMegabytes": 10,
    "RetainedFileCount": 7
  }
}
```

| Setting | Default | What it does |
| --- | --- | --- |
| `Enabled` | `false` | Turns the file sink on. |
| `Directory` | `logs` | Relative paths resolve against the content root. |
| `MaxFileSizeMegabytes` | `10` | A new file starts once the current one passes this. |
| `RetainedFileCount` | `7` | Older files are deleted. |

Files are named `simpleradius-YYYYMMDD.log`. Lines look like:

```
2026-07-23 14:31:02.184Z [info] SimpleRadius.Services.RadiusRequestHandler: Accepted aa:bb:cc:dd:ee:ff from UniFi gateway on VLAN 10
```

Write it somewhere persistent. In a container that means under `/data`:

```yaml
environment:
  FileLogging__Enabled: "true"
  FileLogging__Directory: /data/logs
```

Logging never takes the server down: if a write fails, the message still reaches the console and the
request carries on.

## Debugging a device that will not connect

Turn the handler up and watch:

```bash
Logging__LogLevel__SimpleRadius=Debug
```

Then work down this list:

1. **No log line at all when the device connects.** The packet never arrived. Check the router's RADIUS
   configuration, and any firewall between it and UDP 1812.
2. **`Discarded … from unconfigured NAS`.** Add the router under **NAS** using the source address in the
   message — not necessarily the address you configured it with, if it has several interfaces.
3. **`Message-Authenticator failed`.** The shared secret differs. Retype it at both ends.
4. **`Accepted … on VLAN n`, but the device is on the wrong VLAN.** The server did its part; the router
   is not applying the reply. Compare the **Access-Accept preview** on the Settings page with what your
   hardware expects, and try the reply-attribute toggles — see
   [configuration.md](configuration.md#reply-attributes).
5. **Sessions never appear.** Accounting is not configured on the router, or it is disabled for that NAS.

## Shipping logs elsewhere

There is no built-in syslog or OTLP exporter. Use the platform:

- **Container**: any Docker or Kubernetes log driver collects stdout.
- **systemd**: journald already has it; forward with `systemd-journal-upload` or a Vector/Promtail agent.
- **File**: point Promtail, Filebeat or Vector at the log directory.
