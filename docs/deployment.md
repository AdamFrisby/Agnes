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

The default `host-only` image contains the host daemon only. To include the
optional same-origin browser operator UI, build the `web-ui-host` target and
set `Agnes__WebRoot=/app/wwwroot` on the container:

```bash
docker build --target web-ui-host -t agnes-host .
docker run -e Agnes__WebRoot=/app/wwwroot ... agnes-host
```

The browser UI is served at the host root — it is not a separate `/admin`
route. Put the entire host, including that root route, behind the chosen
authentication gateway. Leave `Agnes__WebRoot` unset and build `host-only` for
an API/desktop-client-only installation.

The browser operator image deliberately does not enable offline/PWA mode by
default. Its manifest and service-worker fetches cannot follow an interactive
Cloudflare Access redirect, and an offline cache is inappropriate for a
protected admin surface. A non-gateway deployment may explicitly opt in with
`--build-arg AGNES_ENABLE_PWA=true` in its own build wrapper.

The protected build versions its bootstrap and configuration URLs, and serves
them with `Cache-Control: no-store`. This prevents an already-open browser from
reusing a PWA-enabled bootstrap after an operator switches the deployment to an
Access-gated UI.

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

## Google and Cloudflare Access sign-in

Agnes keeps its own per-device, revocable token after bootstrap. Two optional sources can mint
that token without using a typed pairing code:

- **Native Google** uses the existing OIDC configuration. Register
  `https://<agnes-host>/auth/oidc/callback` in Google, then set the Google issuer, client ID,
  optional client secret, audience, and redirect URI under `Agnes:Auth:Oidc`. Keep the normal
  OIDC JWKS validation enabled; do not accept a client-provided email as proof of identity.
- **Cloudflare Access** accepts only Cloudflare's signed
  `Cf-Access-Jwt-Assertion`, validates its issuer, exact Access application audience and signing
  key, then checks an Agnes-side email-domain allowlist. It never trusts
  `Cf-Access-Authenticated-User-Email`.

```json
{
  "Agnes": {
    "Auth": {
      "CloudflareAccess": {
        "Enabled": true,
        "TeamDomain": "your-team.cloudflareaccess.com",
        "Audience": "your-access-application-audience-tag",
        "AllowedEmailDomains": [ "example.com" ]
      }
    }
  }
}
```

An enabled-but-incomplete Cloudflare configuration fails host startup. A browser client that has
already completed Cloudflare Access can call `POST /auth/cloudflare-access/exchange` with a
`CloudflareAccessExchangeRequest` body containing only its device name; the assertion remains in
the forwarding header and a normal Agnes device token is returned. Native clients retain their
existing OIDC or device-token paths because browser Access cookies are not available to arbitrary
desktop processes.

When the optional browser UI is installed, it defaults its host field to its own protected origin.
Choose **Continue with Cloudflare Access** to perform this exchange. It is not a second Google
sign-in and it never exposes or stores the Cloudflare assertion in browser code; the proxy injects
that signed assertion into the same-origin request. The browser instead receives a standard Agnes
device token, which can be revoked through Agnes device management.

## Rate limiting

The token-minting endpoints (`/pair`, `/auth/github/exchange`,
`/auth/cloudflare-access/exchange`, `/auth/keypair`[`/challenge`])
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
| `Auth:Oidc:{Enabled,Issuer,Audience,JwksUri,ClientId,ClientSecret,RedirectUri}` | Native OIDC sign-in; Google is configured through this standard flow. |
| `Auth:CloudflareAccess:{Enabled,TeamDomain,Audience,AllowedEmailDomains}` | Exchange a validated Cloudflare Access browser assertion for a revocable device token. |
| `Auth:RateLimit:{Enabled,PerIpPerMinute,GlobalPerMinute,TrustForwardedFor}` | Throttle the auth endpoints (see above). |
| `DevicesFile` | Where paired-device hashes are stored. |
| `AllowedOrigins` / `AllowAllOrigins` | Cross-origin browser policy. |
| `Database` | SQLite path for the event log (in-memory if empty). |
| `Storage:EventStore` | Event-store backend: `sqlite` (default single-node) or `postgres` (optional shared DB). |
| `Storage:Postgres:ConnectionString` | Npgsql connection string; required when `Storage:EventStore=postgres`. |
| `ClaudeCode` / `OpenCode` / `ClaudeCodeNative` | Agent launch commands. |
| `Sandbox:Provider` | `incus` to run agents in per-session VMs (see [sandbox-live-testing.md](sandbox-live-testing.md)). |

## Storage topology (event store)

The event-store backend *is* the deployment topology choice (ops/03). Both backends implement the same
`IEventStore` contract (append / read-since / snapshot / head, monotonic per-session sequence), so the choice is
purely operational — no application behavior changes.

- **Single-node (default): SQLite.** With `Database` set to a file path (or in-memory when empty), the event log
  lives in one file on the host machine. This is the right shape for the standard "one host = one daemon on one
  machine" deployment: durable, ordered, single-writer, zero operational overhead. A zero-config deployment
  behaves exactly as it always has — nothing about the default changes.
- **Scaled / shared database (optional): Postgres.** Set `Storage:EventStore=postgres` and
  `Storage:Postgres:ConnectionString` to point the event log at a shared Postgres server — e.g. so a
  scaled/multi-instance host (or, later, a relay) can share one logical store. The Npgsql driver is only loaded
  when this is selected; SQLite deployments never touch it. v1 keeps a single logical store — there is no
  sharding.

Selection is per-store: the same seam could later give other durable stores (e.g. the memory-search index) a
Postgres backing the same way, without changing core storage code.
