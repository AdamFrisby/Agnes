# Live-verification gaps & required credentials

Everything in the roadmap is built and passes the offline test suite (`Agnes.Core.slnf`).
A handful of features are **code-complete but not yet verified against real infrastructure or
third-party services**, because doing so needs a credential, a deployed service, or a network
topology that CI/headless builds can't provide. This file tracks exactly what remains to be
tested live and what each needs, so it can be picked up in a real environment later.

Nothing here is a missing feature — it's the "plug in the real thing and confirm" step.

## 1. Relay — cross-network smoke (`connectivity/01`)
- **Built & offline-tested:** the `Agnes.Relay` blind broker, the `agnes-relay`/Tailscale/Direct
  transports, host/client relay wiring (a prompt round-trips through an in-process relay), and the
  cert paths (self-signed pinning, Certes DNS-01, LettuceEncrypt on the relay endpoint).
- **Not verified live (needs a relay VPS + two networks):**
  - **AC2** — a host behind real NAT (no inbound ports) reachable by a client on a *different*
    network (e.g. a mobile hotspot).
  - **AC3** — Tailscale `serve` exposes nothing to the public internet (confirm via a port scan).
  - Real **Let's Encrypt** issuance: `Certes` DNS-01 for the host (DuckDNS/BYO domain) and
    `LettuceEncrypt` on the relay's own public endpoint.
- **To test:** stand up `src/Agnes.Relay` (Dockerfile provided) on a small public VPS with a
  domain; add the host's relay public key to the relay's authorized-hosts list; set
  `Agnes:Transport:Provider=agnes-relay` + `Agnes:Transport:Relay:*` on a NAT'd host; connect a
  client from another network.

## 2. Voice — OpenAI Realtime audio loop (`voice/01`)
- **Built:** the Agnes-as-MCP-server (`/mcp-agnes`, 7 authorized tools) + the OpenAI Realtime
  session-config seam (`Agnes:Voice:OpenAI:*`), all offline-tested.
- **Not verified (needs a key):** the actual WebRTC/WebSocket **audio** loop (mic in / speaker out)
  against OpenAI's Realtime endpoint.
- **To test:** set `Agnes:Voice:OpenAI:ApiKey` (+ `Model`, `McpEndpointUrl`); drive a real voice
  session.

## 3. Push notifications — FCM/APNs (`notifications/01`)
- **Built:** `FcmPushChannel` (Google `FirebaseAdmin`) behind `IFcmSender`, config-gated; dispatch,
  per-device toggles, and the untrusted-payload safety guard are offline-tested with a fake sender.
- **Not verified (needs credentials + a device):** real delivery to an Android device.
- **To test:** set `Agnes:Push:Fcm:ServiceAccountJson` (or `…File`) from a Firebase project; register
  a device token; trigger a permission request. iOS/APNs is a further (unbuilt) client-side half.

## 4. Channel bridges — real chat platforms (`extensibility/04`)
- **Built:** real Slack / Discord / WhatsApp transports (raw HTTP), tokens via
  `Agnes:Channels:<Bridge>:*`; Slack + WhatsApp inbound are BCL-HMAC-verified; Discord inbound
  Ed25519 is verified via NSec. Offline-tested with stub handlers.
- **Not verified (needs bot tokens/apps):** real message send + inbound webhook round-trips.
- **To test:** create a Slack app / Discord bot / WhatsApp Cloud API app; set the tokens + signing
  secrets; link a chat id to a device; approve a permission from the chat.

## 5. Connected-services broker — real GitHub OAuth (`providers/02`)
- **Built:** `GitHubConnectedServiceProvider` reusing the existing GitHub App / stored-token auth to
  mint a short-lived credential (offline-tested with a stub).
- **Not verified (needs a real GitHub App or token):** end-to-end credential minting against GitHub.
- **To test:** configure a GitHub App installation or `Agnes:ConnectedServices:GitHub:Token`.
- *(providers/03 Claude quota needs **no** new credential — it reuses the token
  `ClaudeCredentialProvider` already reads from `~/.claude/.credentials.json`.)*

## 6. Postgres event store — real round-trip (`ops/03`)
- **Built & offline-tested:** `PostgresEventStore` (Npgsql, config-gated behind `Storage:EventStore=postgres`
  + `Storage:Postgres:ConnectionString`) implements the same `IEventStore` contract as SQLite; the DDL/statement
  shape and config selection are unit-tested, and the full append/read-since/snapshot/head/multi-session
  contract runs against SQLite. SQLite stays the default — a zero-config host is unchanged.
- **Not verified (needs a Postgres server):** the same contract test against a live Postgres. It runs when a
  server is reachable, otherwise it dynamically **skips** (never faked with SQLite).
- **To test:** set `POSTGRES_TEST_CONNSTRING` (or run a local Postgres on `:5432`) and re-run
  `Agnes.Host.Tests` — `PostgresEventStoreTests.Postgres_satisfies_the_event_store_contract` exercises the real
  round-trip.

## Capability-gated (waiting on the coding CLIs, not on us)
- **sessions/04 subagent control** — routing a message to / stopping a *specific* subagent needs the
  CLI's own protocol to expose subagent addressing. The roster ships read-only with disabled controls
  + an explaining tooltip; flip `ParticipantRow.Controllable` and wire the control path once a CLI
  supports it. (sessions/03 in-turn steering is now implemented via an escape-then-message primitive
  for input-controlled sessions.)

## Descoped (won't build)
- `security/01` end-to-end encryption — TLS is deemed sufficient (the relay is a blind TLS-passthrough
  pipe, so it never sees plaintext).
- `delight/01` pets-companion — its tone-neutral ambient-status core is already covered by the inbox
  + the tray icon.

See also `docs/sandbox-live-testing.md` for the Incus sandbox live-testing notes.
