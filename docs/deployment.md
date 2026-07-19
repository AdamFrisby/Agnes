# Deploying an Agnes host

The host runs your coding agents with your credentials and is reachable over the
network, so treat it like any small server: terminate TLS, and only let paired
devices in.

## TLS

Kestrel is configured for HTTPS on `https://0.0.0.0:5081` (`appsettings.json`).
In development it uses the ASP.NET dev certificate; for anything reachable off
your machine, supply a real certificate one of two ways:

**A — terminate TLS at a reverse proxy** (recommended). Run the host on plain
HTTP behind Caddy / nginx / Traefik and let the proxy hold the cert:

```
# Caddyfile
agnes.example.com {
    reverse_proxy 127.0.0.1:5081
}
```

**B — give Kestrel the certificate directly** via config (no code change):

```json
{
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://0.0.0.0:5081",
        "Certificate": { "Path": "/etc/agnes/cert.pfx", "Password": "…" }
      }
    }
  }
}
```

PEM pairs work too: `"Certificate": { "Path": "cert.pem", "KeyPath": "key.pem" }`.

If you don't want to expose a port at all, put the host on a private overlay
(Tailscale / WireGuard) and connect clients over that.

## Pairing devices

Auth is per-device bearer tokens. On startup the host logs a **pairing code**:

```
Agnes pairing code: ABCD-EF23  — enter this on a new client to pair it.
```

On a client (desktop → **+ Add host**), enter the host URL and that code. The
client calls `POST /pair`, receives a durable per-device token, and stores it;
the code is single-use and rotates after each pairing (and after repeated bad
attempts). Tokens are persisted **hashed** — `Agnes:DevicesFile` (default
`~/.agnes/devices.json`) never holds a usable token.

Manage devices with a valid token:

- `GET /devices` — list paired devices (id, name, paired/last-seen).
- `DELETE /devices/{id}` — revoke one.

For headless / automation, set `Agnes:PairingToken` to a fixed bootstrap token;
it's always accepted and skips the pairing handshake.

## CORS

The web client served from the **same origin** as the host needs no CORS. Only
when a browser client is hosted elsewhere:

- `Agnes:AllowedOrigins` — comma/space-separated allowlist (recommended).
- `Agnes:AllowAllOrigins` — dev only; reflects any origin. Do not use on a
  public network.

By default no cross-origin browser is allowed (native clients are unaffected).

## Configuration reference (`Agnes:` section)

| Key | Purpose |
|-----|---------|
| `DisplayName` | Host name shown to clients (defaults to the machine name). |
| `PairingToken` | Optional fixed bootstrap token (headless). |
| `DevicesFile` | Where paired-device hashes are stored. |
| `AllowedOrigins` / `AllowAllOrigins` | Cross-origin browser policy. |
| `Database` | SQLite path for the event log (in-memory if empty). |
| `ClaudeCode` / `OpenCode` / `ClaudeCodeNative` | Agent launch commands. |
| `Sandbox:Provider` | `incus` to run agents in per-session VMs (see [sandbox-live-testing.md](sandbox-live-testing.md)). |
