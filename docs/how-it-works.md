# How it works

The whole request path lives in
[`RadiusRequestHandler`](../SimpleRadius/Services/RadiusRequestHandler.cs), which touches no sockets.
[`RadiusServerProcess`](../SimpleRadius/Services/RadiusServerProcess.cs) owns the two UDP sockets and
nothing else. That split is why the protocol behaviour can be tested without any network setup.

## Authentication

```
Device associates
      │
      ▼
Router sends Access-Request (User-Name = the device MAC)
      │
      ▼
Match the source address against the NAS list ──► not found ──► discard silently
      │
      ▼
Verify Message-Authenticator, if present ──► mismatch ──► discard silently
      │
      ▼
Normalise the identity, look up the client
      │
      ├── known and enabled  ──► its VLAN
      ├── known and disabled ──► Access-Reject with a Reply-Message
      └── unknown            ──► create it on the default VLAN
      │
      ▼
Access-Accept carrying the VLAN attributes
```

### Identifying the NAS

By **source address**. This is why the Kubernetes service sets `externalTrafficPolicy: Local` — under the
default policy every router would appear to come from the node address.

Requests from an address that is not configured are **discarded, not rejected**. RFC 2865 requires
silence, and answering would turn the server into an oracle for probing shared secrets.

### Verifying the shared secret

An Access-Request's authenticator is a random nonce, so the request cannot be authenticated on its own.
The secret is checked through **Message-Authenticator** (RFC 3579), an HMAC-MD5 over the whole packet with
the attribute zeroed. Modern firmware sends it; comparison is constant-time.

Hardware that omits it is accepted by default, because rejecting would lock out older gear. Once you have
confirmed all of your equipment sends it, turn on **Require Message-Authenticator**.

Accounting is different: an Accounting-Request authenticator *is* a keyed digest (RFC 2866), so the secret
is verified on every accounting packet regardless.

### Identity normalisation

UniFi presents the calling MAC as the RADIUS user name, but the spelling varies by firmware. All of these
resolve to the same client:

| What the router sends | Stored as |
| --- | --- |
| `AABBCCDDEEFF` | `aa:bb:cc:dd:ee:ff` |
| `aa-bb-cc-dd-ee-ff` | `aa:bb:cc:dd:ee:ff` |
| `AA:BB:CC:DD:EE:FF` | `aa:bb:cc:dd:ee:ff` |
| `aabb.ccdd.eeff` | `aa:bb:cc:dd:ee:ff` |

Anything that is not a MAC — an ordinary user name — is left alone. The **MAC address format** setting
changes only how addresses are displayed; matching always ignores formatting.

## VLAN assignment

The Access-Accept carries the RFC 2868 tunnel group:

```
Tunnel-Type             = 13   (VLAN)          tag 1
Tunnel-Medium-Type      = 6    (IEEE-802)      tag 1
Tunnel-Private-Group-Id = "10" (ASCII digits)  tag 1
```

The VLAN itself is only in **Tunnel-Private-Group-Id**, as ASCII text, not a binary integer. The other two
must be present or most equipment ignores it, and all three share a tag so the NAS reads them as one set.
This is exactly what a MikroTik user group or a UniFi RADIUS profile expects.

### Compatibility toggles

| Setting | When you need it |
| --- | --- |
| **Tunnel tag = 0** | Equipment that rejects tagged attributes. The tag byte is omitted entirely, per RFC 2868's "unused" tag. |
| **Send Egress-VLANID** | Switches that want the RFC 4675 attribute instead. Encoded as `0x31 << 24 \| vlan`. |
| **Send Service-Type** | NAS models that need `Service-Type = Framed` before they will apply a VLAN. |
| **Acct-Interim-Interval** | Asks the router for periodic accounting updates, which is what keeps live counters moving. |

The Settings page previews the exact attribute set your choices produce, so you can compare it with what
your hardware documentation asks for before touching a device.

### Signing the reply

Two digests, in this order:

1. **Message-Authenticator** (RFC 3579): HMAC-MD5 over the response with the attribute zeroed and the
   *request's* authenticator in the header.
2. **Response Authenticator** (RFC 2865): MD5 of `code + id + length + request authenticator + attributes
   + shared secret`, written into the header afterwards.

Order matters — computing them the other way round produces a packet the router rejects.

## Accounting

| Acct-Status-Type | Effect |
| --- | --- |
| Start (1) | Opens a session |
| Interim-Update (3) | Refreshes counters and timestamps |
| Stop (2) | Closes it, recording the terminate cause |
| Accounting-On (7) / Off (8) | Closes every session the NAS left open, after a restart |

Counters combine `Acct-Input-Octets` with `Acct-Input-Gigawords`, which is what carries the high-order
bits once the 32-bit octet counter wraps — without it, a busy session appears to reset every 4 GB.

Sessions are keyed on `(NAS address, Acct-Session-Id)`, because Acct-Session-Id is only unique within a
NAS.

Every valid accounting request is acknowledged, **including when storage is disabled** for that NAS.
Staying silent would make the router retry the same record indefinitely.

## Storage

One SQLite file, with write-ahead logging so the admin UI reads while the listener writes.

| Table | Contents |
| --- | --- |
| `VlanDefinitions` | Name, VLAN ID, description |
| `ClientDevices` | Identity, VLAN assignment, enabled flag, last seen, auth count |
| `NetworkAccessServers` | Name, IP, shared secret, accounting and enabled flags |
| `AccountingSessions` | Session state, counters, timestamps |
| `ServerSettings` | The single row behind the Settings page |

A client's VLAN is a foreign key rather than a copied number, so editing a VLAN's ID immediately changes
what its clients receive. A VLAN in use, or the default one, cannot be deleted.

## Deliberate design choices

**Each datagram gets its own task and its own DbContext.** A slow write cannot stall the socket.

**Settings are read fresh per packet.** A change on the Settings page applies to the very next request,
with no restart and no cache to invalidate.

**Unknown clients are created, unknown routers are not.** A new device should get on the network; an
unknown router cannot be authenticated at all, since its secret is unknown. Auto-registration exists for
testing and is off by default.

**Binding failures stop the process.** A server that is listening on one of its two ports is worse than
one that is plainly down, because it looks healthy.

## What is not implemented

- EAP and 802.1X with certificates — this is MAC-based authentication only
- PAP/CHAP password verification
- CoA and Disconnect-Message (RFC 5176)
- Proxying to an upstream RADIUS server
- IPv6 transport for RADIUS itself
- RadSec (RADIUS over TLS)
