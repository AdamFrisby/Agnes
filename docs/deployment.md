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

The browser head uses Uno's native WebAssembly renderer. The desktop target
continues to use its Skia desktop renderer, but the browser admin UI does not
require WebGL; this avoids startup failures in privacy-hardened browsers that
withhold WebGL renderer-identification capabilities.

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

Every paired device is an **Owner** or a **Member**, decided by how it was admitted rather than by when
it arrived — the typed code and an authorized key admit an Owner, a federated sign-in admits a Member
unless the login is in that method's `Owners` list, and an approval admits at most what the approver
holds. [security.md](security.md#device-roles-owner-and-member) has the full table and the reasoning.
A Member opens sessions and always sees the ones it started; it does not see other people's.

Manage devices with a valid token:

| | | |
|---|---|---|
| `GET /devices` | any paired device | List paired devices: id, name, paired/last-seen, `role`, and the `kind` that admitted each one. |
| `GET /devices/me` | any paired device | Just the calling device's own row — how a client says "you are a Member on this host" without listing everybody. 404 for the configured bootstrap token, which is an operator but not a device. |
| `PUT /devices/{id}/role` | **Owner** | Body `{ "role": "Owner" \| "Member" }`. 403 for a Member, 409 when it would leave the host with no Owner (including demoting yourself as the last one), 404 for an unknown device. |
| `POST /devices/prune` | **Owner** | Body `{ "unusedForDays": 30 }`. Removes devices not seen for that long (measured from last-seen, or from pairing when a device has never connected), **never** the calling device and **never** the last Owner. Returns what it removed. |
| `DELETE /devices/{id}` | any paired device | Revoke one. |

Owner lists, where a method supports them: `Agnes:Auth:GitHub:Owners`, `Agnes:Auth:Oidc:Owners`,
`Agnes:Auth:CloudflareAccess:Owners` (matches the subject *or* the email), `Agnes:Auth:Mtls:Owners`, and
`Agnes:Auth:Keypair:Role` (`Owner` by default). All are additive to the existing allowlists — an owners
list decides what an *admitted* identity is worth, never whether it is admitted.

Signing in again with the same credential from the same device **rotates** that device's token rather
than adding a row: same id, same role, and the previous token stops working.

For headless / automation, set `Agnes:PairingToken` to a fixed bootstrap token;
it's always accepted, skips the pairing handshake, and counts as an Owner.

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
    "AuthorizedKeysFile": "~/.agnes/authorized_keys",
    "Role": "Owner"
  } } }
}
```

`Role` is what an authorized key is admitted as. **Owner** by default — an operator edited
`authorized_keys` to put it there — but set it to `Member` on a host that hands keys out to a team, and
promote individually with `PUT /devices/{id}/role`.

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

## Agnes's own MCP tools (`agnes`)

As well as wiring *other* MCP servers into an agent, the host offers its own tool set back to the agent it is
running — `send_user_file`, `report_status`, `arm_goal`, `list_goals`, `disarm_goal` — as an MCP server named
`agnes`. It is materialized into whatever config file that CLI reads, with a per-session bearer token;
nothing is configured per session by hand.

The server also states one **standing instruction** in its MCP `ServerInstructions`, which clients put in the
model's context: call `report_status` every few minutes with one line about what you found, what you are
doing, and how it fits the plan. Adapters whose CLI takes a system prompt get the same sentence appended
there too — see [agent-status.md](agent-status.md).

| Adapter | Sandboxed session | Unsandboxed session | Token carried as |
|---|---|---|---|
| `claude-code-native` | `~/.agnes/mcp.json`, passed as `--mcp-config` | temp JSON, passed as `--mcp-config` | `headers.Authorization` |
| `copilot` | `~/.agnes/mcp.json`, passed as `--additional-mcp-config` | temp JSON, same flag | `headers.Authorization` |
| `codex` | `~/.codex/config.toml` in the guest home | **not offered** — see below | `bearer_token_env_var` + `AGNES_MCP_BEARER` in the environment |
| `opencode` (native) | inline config in the environment | not offered | `Authorization` header in the inline config |
| `claude-code` / `opencode` (ACP) | inline config in the environment where the adapter supports it | not offered | as above |
| `pi` | **never** — Pi ships no MCP client at all, by explicit design | never | — |
| `antigravity` | **never** — no MCP config surface | never | — |

Two gaps are deliberate rather than pending:

- **Codex on the host.** Codex discovers its config at a fixed path in the *real* home directory. That file
  is the operator's — their own servers, models and auth live in it — and Agnes writing or merging into it
  would be editing someone's configuration behind their back. Only a config Agnes generates and *points* a
  CLI at (a launch flag) is safe to write for an unsandboxed session. Run Codex sandboxed to get the tools.
- **An operator-defined server already called `agnes`.** Yours wins; Agnes's own is not written, and the host
  logs a warning naming the session. Rename yours to get the Agnes tools back.

Adding an adapter to this table is the whole job of wiring it up — `SessionManager.McpTargetFor` is the one
place that says which file, which format, and how a token is carried, and everything MCP-related for that
adapter follows from it.

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
| `Home` | The host's state directory. **Every** other path below defaults to something under it: devices, MCP config, projects, checkouts, review comments, prompts, launch profiles, connected services, attention/approval requests, the sandbox registry and image manifest, channel links, push registrations, scheduled tasks, session goals, plugins, the relay key, the linked GitHub app. Default `~/.agnes`. Set it to run a second host on one machine without the two treading on each other — and set it for anything that boots the host in a test or a tool. (With `AGNES_REFUSE_DEFAULT_HOME=1` in the environment, a host with no `Home` set refuses to start rather than fall back to `~/.agnes`; the test suite sets that variable, which is how a forgotten override becomes a loud failure instead of an edit to your real host state.) |
| `DevicesFile` | Where paired-device hashes are stored. Defaults to `<Home>/devices.json`. |
| `Auth:Keypair:Role` | What an authorized key is admitted as: `Owner` (default) or `Member`. |
| `Auth:{GitHub,Oidc,CloudflareAccess,Mtls}:Owners` | Identities admitted as **Owner** by that method; everyone else it admits is a Member. |
| `AllowedOrigins` / `AllowAllOrigins` | Cross-origin browser policy. |
| `Database` | SQLite path for the event log (in-memory if empty). |
| `Storage:EventStore` | Event-store backend: `sqlite` (default single-node) or `postgres` (optional shared DB). |
| `Storage:Postgres:ConnectionString` | Npgsql connection string; required when `Storage:EventStore=postgres`. |
| `ClaudeCode` / `OpenCode` / `ClaudeCodeNative` / `Codex` / `Copilot` / `Pi` | Agent launch commands. |
| `Copilot:Provider:*` | Copilot bring-your-own-key provider (`BaseUrl` activates it; also `Type`, `ApiKey`/`BearerToken`, `WireApi`, `Transport`, `AzureApiVersion`, `Headers`, `Model`). Rendered to the `COPILOT_PROVIDER_*` environment, which is the only place Copilot exposes this. |
| `Copilot:FleetMode` | Starts each Copilot session in fleet mode (parallel subagent execution — its own UI calls it "autopilot + /fleet"). Off by default: a fleet session spends far more. Copilot exposes this only as the in-session `/fleet` command — no flag, no environment variable, no settings key — so Agnes invokes that command once as the session opens, best-effort. |
| `Copilot:SubagentNames` | Which built-in Copilot subagents get pointed at the session's model. Defaults to the ones whose shipped definition pins one (`explore`, `task`, `research`) — under BYOK those ids resolve to nothing, so without this a session can only dispatch to the agents that pin nothing. Agnes merges `subagents.agents.<name>.model` into `~/.copilot/settings.json` at launch and again on every model switch, leaving every other setting in the file alone. Only applied when `Copilot:Provider:BaseUrl` is set; set this to `[]` to leave the file untouched entirely. |
| `Sandbox:Provider` | `incus` to run agents in per-session VMs (see [sandbox-live-testing.md](sandbox-live-testing.md)). |
| `Sandbox:Incus:InstancePrefix` | Name prefix for the VMs this daemon creates (default `agnes-`). Change it when a **second** daemon shares one Incus — a screenshot run, a live probe — so its instances can be told from the operator's session VMs by name, and cleaned up without guessing. |
| `Sandbox:Incus:GuestReadySeconds` | How long to wait for a new VM to report ready (default 180). Raise it on a workstation already running other VMs, where a first boot is legitimately slower than on a host that exists to run sandboxes. |
| `Sandbox:GuestMcpBindUrl` / `Sandbox:GuestMcpUrl` | Where a **sandboxed** agent reaches Agnes's own MCP tools — the address the host binds on the sandbox bridge, and the same address as the guest sees it (e.g. `http://10.99.5.1:5099` and `http://10.99.5.1:5099/mcp-agnes`). Off unless the bind address is set. |
| `Mcp:LocalEnabled` | Whether an agent running **on the host** (an unsandboxed session) is offered Agnes's own MCP tools over a loopback listener. Default **true**. Set false on a machine whose local users you don't trust — sessions then simply get no `agnes` server. |
| `Mcp:LocalUrl` | Bind address for that loopback listener. Default `http://127.0.0.1:5117`. Change it if 5117 clashes (a second Agnes on the same box); if the port is already in use the host logs it and starts *without* the local endpoint rather than failing. Both MCP listeners are **added** to whichever listener you configured (`Kestrel:Endpoints` or `ASPNETCORE_URLS`); configure neither and they are skipped with a log line rather than displacing Kestrel's default. |
| `Security:AllowGraphicalSandboxes` | Whether a session may ask for a **graphical** sandbox — a VM with a display the agent drives over `computer_*` and a person watches over the display channel. **Default false**; implies a sandbox. See [security.md](security.md) and [display-channel.md](display-channel.md). |
| `Display:{ControlIdleSeconds,MaxFps,JpegQuality,FullFrameThresholdPercent}` | The display channel's stream shape: how long an untouched human hold survives before control falls back (60 s), the per-subscriber frame-rate ceiling (15), JPEG quality (75), and the damage share at which a Tile becomes a Full frame (40 %). |
| `Display:{InputEventsPerMinute,InputEventsPerToolCall,MaxTypeBytes,MaxWaitMs}` | What may be injected into a graphical guest: a per-session rolling budget across the agent and every person (240/min), what one agent tool call may expand to (32), the `computer_type` ceiling (4096 bytes), and the `computer_wait` ceiling (10 s). |
| `Display:BlockedChords` | Key chords the **agent** may not press, e.g. `["super", "super+*", "ctrl+alt+*", "alt+F2", "ctrl+shift+i"]` (the default). Agent-only: what it stops is an agent leaving the application it is working in. A person driving the display is already authorized to do anything the guest allows. |
| `Status:MaxChars` | Longest one-line status an agent may report with `report_status` (default 240, about two sentences). A longer report is **kept up to the limit**, cut at a word boundary — never refused — and the agent is told what was kept, so the next one is shorter. A non-positive value falls back to the default. See [agent-status.md](agent-status.md). |
| `Status:MinIntervalSeconds` | Coalescing window for status reports (default 20). At most one line per window reaches the log; a report inside the window *replaces* whatever was pending and is written when the window closes, so the latest line always lands and a chatty agent can't bury its own transcript. `0` writes every report. |
| `Sharing:MaxBytes` | Largest file an agent may send the user with `send_user_file` (default 26214400 — 25 MB). Sending copies the file into the workspace and every connected client then downloads it, phones on mobile data included, so the cap turns "send you the build" into a refusal the agent can act on rather than a silent, very slow success. See [send-user-file.md](send-user-file.md). |

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

## Troubleshooting

### "Why can't this device see any sessions?"

An empty session list is almost always an *answer*, not a fault: the catalogue advertises exactly what
the caller could subscribe to, so it goes empty when the caller may reach nothing. Work down this list.

1. **Ask the device what it is.** `GET /devices/me` returns its `role` and the `kind` that admitted it
   (the desktop and phone show the same two facts on their Devices screen). A **Member** sees only the
   sessions it started plus what has been shared with it — that is working as intended, not a bug.
2. **Is the session actually theirs?** A session is owned by whoever opened it (their GitHub login,
   falling back to the device id). Sessions opened by somebody else need an explicit share, or Owner.
3. **Does the host still have a real Owner?** `GET /devices` as any paired device: if the only row
   marked `Owner` is something you don't recognise — a stale test fixture, a device you revoked and
   re-paired — that is the old "earliest device wins" rule showing through a migrated store. Promote
   the right device with `PUT /devices/{id}/role`, then demote or `DELETE` the stale one. (The host
   refuses to be left with no Owner, so promote before you demote.)
4. **Check `Agnes:Home`.** If a test run, a script, or a second daemon booted a host without setting it,
   it wrote into the same `~/.agnes` as your real host and its device records are now in your registry.
   `POST /devices/prune` with a suitable `unusedForDays` clears out what nobody has used; set
   `Agnes:Home` on the stray process so it stops happening.
5. **Isolation.** Under `Agnes:Security:SessionIsolation=PerUser`/`PerGroup` a device also needs to own
   the session or be in its group — see [security.md](security.md#session-isolation--groups).
