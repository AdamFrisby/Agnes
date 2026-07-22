# Deploying an Agnes host

The host runs your coding agents with your credentials and is reachable over the
network, so treat it like any small server: terminate TLS, and only let paired
devices in.

## Running the host

**Docker** (agents run inside the container):

```bash
docker compose up --build          # or: docker build -t agnes-host . && docker run …
docker compose logs agnes          # read the pairing code from the logs
```

The image ships Node + git (for the Claude Code ACP bridge and worktrees);
mount your projects at `/work` and agent credentials as needed (see
`compose.yaml`). The container serves plain HTTP on 5081 — put TLS in front of
it (below). The event log and device tokens persist in the `/data` volume.

**From source** (agents run on the host machine; needed for Incus sandboxing):

```bash
dotnet run --project src/Agnes.Host          # dev
# or a self-contained build:
dotnet publish src/Agnes.Host -c Release -r linux-x64 --self-contained -o out/host
```

**Desktop client** — self-contained builds per OS:

```bash
dotnet publish src/Agnes.App.Desktop -c Release -r linux-x64  --self-contained
dotnet publish src/Agnes.App.Desktop -c Release -r win-x64    --self-contained
dotnet publish src/Agnes.App.Desktop -c Release -r osx-arm64  --self-contained
```

**Web client** — the Uno WASM head, served by the host from the same origin (no
CORS needed):

```bash
dotnet workload install wasm-tools
dotnet publish src/Agnes.App/Agnes.App -f net10.0-browserwasm -c Release -o out/web
# point the host at the published wwwroot:
Agnes__WebRoot=out/web/wwwroot dotnet run --project src/Agnes.Host
```

Then open the host URL in a browser. The host serves the WASM framework assets
with the right MIME types and falls back to `index.html` for client routes.
The **mobile** heads (`net10.0-android`, `net10.0-desktop`) build from the same
`src/Agnes.App` project.

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

The pairing code is ~40 bits with rotate-after-5-failures — fine on localhost or a
private overlay, but a thin guard on the open internet. For an internet-facing host,
prefer **GitHub sign-in** below and turn the pairing code off:

```json
{ "Agnes": { "Auth": { "Pairing": { "Enabled": false } } } }
```

## GitHub sign-in (SSO)

Strong auth by GitHub identity + an allowlist — no shared secret, and it works on
every client (desktop/mobile/web) because it uses GitHub's **device flow** (no
callback URL). Clients discover it automatically via `GET /auth/methods`.

1. Register a **GitHub OAuth App** (Settings → Developer settings → OAuth Apps) and
   tick **Enable Device Flow**. Copy its **Client ID** (public — not a secret; no
   client secret is needed for the device flow).
2. Configure the host:

   ```json
   {
     "Agnes": { "Auth": { "GitHub": {
       "Enabled": true,
       "ClientId": "Iv1.abc123…",
       "AllowedUsers": [ "your-login" ],
       "AllowedOrgs":  [ "your-org", "your-org/your-team" ]
     } } }
   }
   ```

   A user may connect if their login is in `AllowedUsers` **or** they're an active
   member of a listed org (or `org/team`). Leave both empty and sign-in stays off.
3. On a client, **+ Add host** → enter the URL → **Sign in with GitHub**: authorize
   the shown code at `github.com/login/device`; the host verifies your identity,
   checks the allowlist, and issues the same per-device token pairing would. The
   GitHub token is used only to verify and is never stored. (Org/team checks need
   the `read:org` scope, which the flow requests.)

## Keypair sign-in (offline)

SSH-`authorized_keys` style: strong, no GitHub dependency. Each client holds a P-256
keypair; you add its public key to the host. The client authenticates by signing a
single-use challenge — no secret ever crosses the wire.

```json
{
  "Agnes": { "Auth": { "Keypair": {
    "Enabled": true,
    "AuthorizedKeysFile": "~/.agnes/authorized_keys"
  } } }
}
```

`authorized_keys` has one **base64 SPKI** public key per line, with an optional label:

```
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE…  alice-laptop
```

On a client, **+ Add host** → **Sign in with a key**: it generates a key on first use
(`~/.agnes/client_key.p8`) and shows the exact line to paste into the host's
`authorized_keys`; add it, retry, and you're connected.

## Rate limiting

The token-minting endpoints (`/pair`, `/auth/github/exchange`, `/auth/keypair`[`/challenge`])
are throttled **per client IP and globally** — on by default. A single IP can't
hammer them, and a distributed attempt is still capped overall. Discovery
(`/auth/methods`) is exempt. Defaults (per minute): `10` per IP, `100` global.

```json
{ "Agnes": { "Auth": { "RateLimit": {
  "Enabled": true, "PerIpPerMinute": 10, "GlobalPerMinute": 100,
  "TrustForwardedFor": true
} } } }
```

Set **`TrustForwardedFor: true` only behind a reverse proxy you control** — it takes
the client IP from `X-Forwarded-For`, which is spoofable if the host is reached
directly. The global limit is the backstop either way.

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
| `Auth:Pairing:Enabled` | Turn the pairing-code bootstrap off (default on) — e.g. GitHub-only. |
| `Auth:GitHub:{Enabled,ClientId,AllowedUsers,AllowedOrgs}` | GitHub-SSO sign-in + allowlist (see above). |
| `Auth:Keypair:{Enabled,AuthorizedKeysFile}` | Keypair (authorized_keys) sign-in (see above). |
| `Auth:RateLimit:{Enabled,PerIpPerMinute,GlobalPerMinute,TrustForwardedFor}` | Throttle the auth endpoints (see above). |
| `DevicesFile` | Where paired-device hashes are stored. |
| `AllowedOrigins` / `AllowAllOrigins` | Cross-origin browser policy. |
| `Database` | SQLite path for the event log (in-memory if empty). |
| `ClaudeCode` / `OpenCode` / `ClaudeCodeNative` | Agent launch commands. |
| `Sandbox:Provider` | `incus` to run agents in per-session VMs (see [sandbox-live-testing.md](sandbox-live-testing.md)). |
