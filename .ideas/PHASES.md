# Dependency graph & phased build plan

This file maps the hard and soft dependencies between every doc in this backlog, then groups the resulting work into phases of **at most 4 items each**. A phase never contains an item before all of its hard dependencies — every item in phase *N* can only depend on items in phase 1..*N-1* (one deliberate exception is called out explicitly below). Within a phase, and across phases at the same dependency depth, items are ordered by **usefulness to a developer actually using Agnes day to day** — not strictly by the priority label in each doc, though the two mostly agree.

`platform/04-standalone-ide-shell.md` is excluded from the phase plan entirely — it's a scope-boundary note recommending *against* building anything, not a spec, so there's nothing to schedule. `delight/01-pets-companion.md` is included but stays last and is explicitly optional — see its own doc for why.

## How to read the dependency graph

For each item: **Hard** = cannot be correctly built before this lands (the hub/interface/data model it needs doesn't exist yet). **Soft** = design guidance or a natural pairing, not a build blocker — sequencing it earlier is *recommended*, not *required*. **Unblocks** = the reverse view, listed once per item so you can see its leverage at a glance.

| Item | Hard depends on | Soft / related | Unblocks |
|---|---|---|---|
| `00-plugin-architecture.md` | — | — | nearly everything with a "New `I...Provider`" plugin surface |
| `connectivity/01-relay-and-tunneling.md` | plugin architecture | — | multi-server, session handoff, device linking, secure channel, deployment topology |
| `security/01-end-to-end-encryption.md` | relay & tunneling — but see note¹ | — | connected-services broker, session sharing, automations (at-rest, conditional) |
| `providers/01-provider-breadth-acp-catalog.md` | plugin architecture | tool-timeline normalization (for harder-to-reach providers) | model/engine selection, profiles |
| `connectivity/02-multi-server-support.md` | relay & tunneling | — | — |
| `connectivity/03-session-handoff.md` | relay & tunneling | session forking & replay (reuses its replay machinery) | multi-machine workspace model |
| `connectivity/04-device-linking-and-restore.md` | relay & tunneling | — | — |
| `connectivity/05-multi-machine-workspace-model.md` | deep git integration (worktrees), session handoff | — | — |
| `providers/02-connected-services-credential-broker.md` | end-to-end encryption | — | quota monitoring, profiles |
| `providers/03-quota-monitoring.md` | connected-services broker | — | — |
| `providers/04-profiles.md` | connected-services broker, provider breadth | — | — |
| `providers/05-model-and-engine-selection.md` | provider breadth | — | — |
| `providers/06-provider-authentication-detection.md` | — | — | — |
| `sessions/01-session-forking-and-replay.md` | — | — | session handoff, participant routing (soft) |
| `sessions/02-direct-vs-synced-sessions.md` | — | — | local CLI wrapper & handoff |
| `sessions/03-pending-queue-and-steering.md` | — | — | participant routing (soft) |
| `sessions/04-participant-routing-and-subagents-panel.md` | — | session forking, pending queue/steering (sequence after) | — |
| `sessions/05-session-read-state-and-shortcuts.md` | — | — | — |
| `sessions/06-tool-timeline-normalization.md` | — | — | deep git integration, provider breadth (soft) |
| `sessions/07-local-cli-wrapper-and-handoff.md` | direct-vs-synced sessions | — | — |
| `security/02-enterprise-auth.md` | plugin architecture | device linking & restore (natural pairing) | — |
| `collaboration/01-friends-and-social.md` | — | — | session sharing (direct-share path only) |
| `collaboration/02-session-sharing-and-public-links.md` | end-to-end encryption | friends & social (for direct sharing; public links don't need it) | — |
| `voice/01-voice-assistant.md` | plugin architecture | — | — |
| `git-and-files/01-deep-git-integration.md` | tool-timeline normalization | — | multi-machine workspace model |
| `git-and-files/02-review-comments.md` | — | — | — |
| `git-and-files/03-attachments-and-file-browser.md` | — | — | — |
| `extensibility/01-mcp-management.md` | plugin architecture | — | — |
| `extensibility/02-prompts-skills-library.md` | plugin architecture | — | — |
| `extensibility/03-automations.md` | plugin architecture | end-to-end encryption (conditional, only if templates need at-rest protection) | scriptable agent CLI (soft) |
| `extensibility/04-channel-bridges.md` | plugin architecture | — | — |
| `extensibility/05-scriptable-agent-cli.md` | — | automations (related, not blocking) | generic human-in-the-loop webhook (soft) |
| `extensibility/06-generic-human-in-the-loop-webhook.md` | inbox & approvals | scriptable agent CLI (natural first caller) | — |
| `notifications/01-push-notifications.md` | plugin architecture | — | — |
| `notifications/02-inbox-and-approvals.md` | plugin architecture | pairs naturally with push notifications | generic human-in-the-loop webhook |
| `ops/01-bug-reports-and-diagnostics.md` | plugin architecture | — | — |
| `ops/02-memory-search.md` | plugin architecture | — | — |
| `ops/03-deployment-topology-and-multi-db.md` | relay & tunneling | — | — |
| `ops/04-onboarding-showcase.md` | — | (needs real shipped features to showcase — low value if built too early) | — |
| `platform/01-ios-client.md` | — | — | — |
| `platform/02-menubar-and-tray.md` | — | — | — |
| `platform/03-embedded-terminal-everywhere.md` | — | provider auth detection (reuses its "Log in" action) | — |
| `platform/04-standalone-ide-shell.md` | *excluded from phase plan — see above* | | |
| `delight/01-pets-companion.md` | — | — | — |

¹ **`security/01` and `connectivity/01` are a deliberate exception to the "strictly earlier phase" rule.** `security/01`'s own priority note says it should land "alongside or before" the relay carries real traffic — the two are tightly coupled enough (a relay transport and the encrypted tunnel that makes it safe to use) that they belong in the same phase, built concurrently, rather than four phases apart. Every other dependency below is respected strictly.

## Phased build plan

Each phase title is the theme that ties its items together. Items are ordered within a phase by developer usefulness, highest first.

**Status legend** (as of the build-out through 2026-07):
- `[x]` — **implemented**, feature-complete for its intended scope, built + tested + on `main`.
- `[~]` — **basic / partial**: an MVP shipped and green on `main`, but a substantial part of the spec is deliberately deferred (noted per item). Provider-shaped items marked `[~] template` ship the plugin interface + a commented stub so a real backend wires in later — no real third-party integration yet.
- `[—]` — **descoped**: a maintainer decision not to build it.
- `[ ]` — **not built** (reason noted: buildable/unblocked-but-not-reached, or waiting on infra/runtime).

**Tally of the 42 scheduled items: 33 `[x]` complete · 5 `[~]` basic/partial · 2 `[—]` descoped · 2 `[ ]` not built.** The whole connectivity/relay family is now code-complete; the only `[ ]` left are `ops/03` deployment-topology (low priority) and `sessions/07` local-CLI-wrapper (wants a live PTY runtime). The 5 `[~]` need an adapter capability no CLI exposes (sessions/03 in-turn steering, sessions/04 subagent control), a maintainer-deferred sub-feature (ops/02 semantic search, security/02 interactive-OIDC redirect), or a live cross-network smoke on the relay VPS (connectivity/01). `platform/04` remains excluded. *(Two big autonomous passes drove this from 18→29 `[x]`: a "finish the unblocked tails" pass, then a "go build everything a decision unblocks" pass — descoping E2E + pets, turning the `[~] template` providers real (GitHub connected-service, Claude quota, FCM push), building the decision-gated items (friends on GitHub, profiles, direct-vs-synced, session-sharing), and rebuilding voice as an Agnes-MCP-server. Credentials for real integrations come from host settings. Then `connectivity/01` (the relay keystone) was largely built — self-hostable `Agnes.Relay` blind broker + Tailscale transport + host/client relay transports, proven end-to-end offline; only real-CA certs + the live cross-network smoke remain. The 6 remaining `[ ]` are the rest of the connectivity family + local-CLI PTY handoff — now UNBLOCKED by the relay transport (buildable), pending build + the relay VPS for live verification.)*

### Phase 1 — Foundation + two everyday wins
1. `[x]` `00-plugin-architecture.md` — the generalized `IPluginRegistry<T>`/`PluginRegistry<T>` pattern; `IAgentAdapter`/`ISandboxProvider`/auth/git-host/MCP/transport/event-store all migrated onto it (AC13.1–6); NuGet install + signature verification + `AssemblyLoadContext` loader; host + client capability negotiation; client-plugin registry; the event spine; and the plugin management UI. (Went well beyond the original "core scope only" pass.)
2. `[x]` `sessions/01-session-forking-and-replay.md` — branch a conversation instead of dead-ending it. High daily value, zero dependencies.
3. `[~]` `sessions/03-pending-queue-and-steering.md` — fixes "can't send while the agent's busy." **Partial:** queue + three send policies + send-now + discarded list shipped; true in-turn `ISteerableSession` injection deferred (uses the always-available cancel-then-resend fallback).

### Phase 2 — Structural enablers + remote-workflow basics
1. `[x]` `sessions/06-tool-timeline-normalization.md` — formalizes something that already half-exists; unlocks git integration and broader provider support.
2. `[x]` `git-and-files/03-attachments-and-file-browser.md` — attachment upload + workspace path-safety, plus the file browser (list/read/write/create/rename/delete/download, all through the shared traversal-safety guard, text+image preview). *(File browser completed in the finishing pass.)*
3. `[x]` `sessions/02-direct-vs-synced-sessions.md` — `IExternalSessionSource` discovery (Claude Code reads its own `~/.claude/projects/*.jsonl`) + a read-only "watch" tailing an external session into the Agnes event model (read-only enforced at four layers). Adoption is a documented seam.
4. `[x]` `sessions/05-session-read-state-and-shortcuts.md` — unread indicators and "same setup again"; cheap, high polish-to-effort ratio.

### Phase 3 — Review, terminal reuse, and social groundwork
1. `[x]` `git-and-files/02-review-comments.md` — leave feedback anchored to a diff line, not lost in chat scrollback.
2. `[x]` `platform/03-embedded-terminal-everywhere.md` — client terminal-I/O protocol over `ICliFallback`/`TerminalOutputEvent`, a desktop panel via `Iciclecreek.Avalonia.Terminal`, provider "Log in" via `ICliFallback`, and live login output streamed to a client-visible interactive terminal. *(Login streaming completed in the go-build pass.)*
3. `[x]` `providers/06-provider-authentication-detection.md` — stop discovering "not logged in" by watching a session fail.
4. `[x]` `collaboration/01-friends-and-social.md` — restructured on **GitHub identity**: verified friend directory, live org/team eligibility (never cached as trust), explicit revocable `AccessGrant`s via `IFriendAuthorizer`. *(Built in the go-build pass.)*

### Phase 4 — Low-priority polish (last of the no-dependency items)
1. `[x]` `ops/04-onboarding-showcase.md` — first-run setup wizard over `GET /auth/methods` + data-driven shown-once showcase cards.
2. `[x]` `platform/02-menubar-and-tray.md` — the system-tray icon (aggregate status + jump-to-session); the separate self-host operator status tool is intentionally out of scope per the spec.
3. `[—]` `delight/01-pets-companion.md` — **DESCOPED (won't build).** Maintainer decision 2026-07. Its tone-neutral ambient-status core is already covered by the inbox + tray.

### Phase 5 — Reachability + the security work that must ride along with it
1. `[~]` `connectivity/01-relay-and-tunneling.md` — **Code-complete; only the live smoke remains.** Host transport abstraction (AC13.6), a self-hostable **`Agnes.Relay`** blind broker (per-host-key auth, brute-force edge, Dockerfile), a **`TailscaleTransportProvider`** (tailnet-only default), the **relay + client transports** (host blind-pumps relay data-connections to its loopback TLS Kestrel; client validates a pinned self-signed **or** CA-named host cert; per-device auth unchanged in the tunnel) — a prompt round-trips through an in-process relay in tests (AC2/AC4/AC5/AC6) — and the **full cert story**: Certes DNS-01 for the NAT'd host via DuckDNS (DynDNS) / BYO-domain, LettuceEncrypt on the relay's own public endpoint, Tailscale auto-certs otherwise. **Still `[~]` only because** the live cross-network NAT-traversal smoke (AC2 on real networks) + real Let's Encrypt issuance can't be verified headlessly — both need the relay VPS. Maintainer decisions: blind pipe, self-host-only, per-host key, lean on Tailscale.
2. `[—]` `security/01-end-to-end-encryption.md` — **DESCOPED (won't build).** Maintainer decision 2026-07: TLS is deemed sufficient. This un-gates its former dependents — broker/quota/profiles are now built directly without it.
3. `[x]` `providers/01-provider-breadth-acp-catalog.md` — a generic custom-ACP adapter plus the on-ramp for new agent CLIs.
4. `[x]` `notifications/02-inbox-and-approvals.md` — tier-1 cross-session approvals inbox (unioned with webhook requests) + tier-2 generic approval-gating: `IApprovalGatedAction` + default-ungated per-surface table + durable `ApprovalRequest`s in the same inbox, wiring the existing GitCommit + credential-share gates. *(Tier-2 wired in the go-build pass.)*

### Phase 6 — Git, notifications, and the automation upgrade
1. `[x]` `notifications/01-push-notifications.md` — `INotificationChannel` plugin point + spine-driven dispatch (per-trigger/per-device toggles, active-session suppression, untrusted-payload safety guard), plus a **real FCM channel** via Google's `FirebaseAdmin` behind an `IFcmSender` seam, service-account credential from host settings. *(Real FCM built in the go-build pass; Android/iOS client SDK is the other half.)*
2. `[x]` `git-and-files/01-deep-git-integration.md` — stash/branch/pull(ff-only)/push + PR list/checkout with server-side safety, plus changed-file scoping (turn/session/repo, via the `NormalizedToolCall` timeline) and agent commit-message generation over the staged diff (through a shared one-shot-agent primitive). *(Scoping + commit-message generation completed in the finishing pass, once sessions/06 had landed.)*
3. `[x]` `extensibility/03-automations.md` — persistence + cron scheduling + pause/resume/run-now; a real "cron for agents."
4. `[ ]` `sessions/07-local-cli-wrapper-and-handoff.md` — **Not built.** Its deps (direct-vs-synced, handoff) are now done; the remaining work is a live `agnes claude`-style terminal-wrapper that needs a real PTY runtime to build+verify meaningfully — the one item still genuinely wanting a runtime here.

### Phase 7 — Extensibility surface
1. `[x]` `extensibility/01-mcp-management.md` — preset install + scope rules + strict/lenient toggle + effective-config preview, plus native-config detection (`IMcpDiscoveryAdapter`; Claude Code reads its own `.mcp.json`/`~/.claude.json`, folded into the preview flagged read-only). *(Native detection completed in the finishing pass.)*
2. `[x]` `extensibility/02-prompts-skills-library.md` — saved prompts + slash-token templates, plus skill bundles (`SKILL.md` + supporting files as a unit), external registries (`IPromptRegistryProvider` + a local-directory source), copy/symlink sync with SHA-256 content-digest conflict detection, and system-prompt additions (`--append-system-prompt`). *(All four deferred pieces completed in the finishing pass.)*
3. `[~]` `sessions/04-participant-routing-and-subagents-panel.md` — **Partial:** the subagents roster (visibility tier) shipped; true addressed message-routing / stop-a-subagent control deferred (capability flag in place).
4. `[x]` `voice/01-voice-assistant.md` — restructured as a dedicated **Agnes-as-MCP-server** (official `ModelContextProtocol.AspNetCore`): 7 device-token-authorized tools at `/mcp-agnes` routing through the same host paths, privacy-gated transcript, + an OpenAI Realtime config seam pointing at that endpoint (key from settings). The MCP interface is a reusable bonus; the audio loop needs a live key. The earlier client-side `IVoiceProvider` remains. *(MCP-server built in the go-build pass.)*

### Phase 8 — Diagnostics and the long tail of plugin points
1. `[x]` `ops/01-bug-reports-and-diagnostics.md` — GitHub-issue sink (with duplicate detection) + custom endpoint + prefilled browser fallback, plus the owner-only, opt-in host-log diagnostic payload and crash/error telemetry (both gates + per-report opt-in required; never on the public browser path). *(Diagnostics + telemetry completed in the finishing pass.)*
2. `[~]` `ops/02-memory-search.md` — **Partial:** the SQLite FTS5 full-text tier (the spec's intended cheap starting point) shipped; semantic/embedding search not built.
3. `[~]` `security/02-enterprise-auth.md` — **Partial:** OIDC token validation + mTLS client-cert + GitHub org/team gating shipped; the interactive OIDC authorization-code redirect deferred (token-validation core built).
4. `[x]` `extensibility/04-channel-bridges.md` — `IChannelBridge` plugin point + linking + authorized inbound + spine-driven outbound, plus **real Slack / Discord / WhatsApp transports** (raw HTTP, tokens via settings). Slack + WhatsApp inbound BCL-HMAC-verified; Discord outbound works, Discord inbound is a guarded seam pending a first-party Ed25519 (.NET 10's BCL has no standalone Ed25519 verify — not hand-rolled). *(Real transports built in the go-build pass.)*

### Phase 9 — Multi-account and multi-host connectivity
1. `[x]` `providers/02-connected-services-credential-broker.md` — plugin point + named multi-profile model + broker + store, plus a **real `GitHubConnectedServiceProvider`** reusing the existing GitHub App/stored-token auth (private key / refresh material never leave their sources). Template retained. *(Real GitHub provider built in the go-build pass.)*
2. `[x]` `connectivity/03-session-handoff.md` — `IHandoffCapableAdapter` (Replay reuses the fork/replay seed machinery; NativeFork is a ready-but-dormant seam). Host-to-host channel takes a **direct path when the target is reachable, relay fallback otherwise** (maintainer decision); workspace transfer with a conflict policy + unsafe-source-path refusal via the shared guard. *(Built once handoff was unblocked.)*
3. `[x]` `connectivity/02-multi-server-support.md` — first-class simultaneous multi-host: address→transport classification (`agnes-relay://` / `*.ts.net` / direct), per-host id + transport attribution, a cross-host `AllSessions` aggregate + per-host reconnect, surfaced via `MultiHostViewModel`. *(Built once the relay transport existed.)*
4. `[x]` `connectivity/04-device-linking-and-restore.md` — `AuthFlowKind` (NewDevice/RestoreAccount/ConnectTerminal) buckets over the pluginized auth methods (advertised via `/auth/methods`), and reachable-address pairing that encodes the active transport's `TransportEndpoint` (or `Agnes:PublicUrl`) into the QR/deep-link instead of a LAN address. *(Built once the relay transport existed.)*

### Phase 10 — Sharing, scripting, and account/model refinement
1. `[x]` `providers/05-model-and-engine-selection.md` — per-session model pick with client-side favorites reconciled against the live catalog (model threading wired for Claude Code; other adapters as their CLIs allow).
2. `[x]` `extensibility/05-scriptable-agent-cli.md` — the headless `agnes-agent` CLI (spawn/send/status/wait/stop/machines/auth), prefix matching, `--json`, CI-friendly exit codes.
3. `[x]` `collaboration/02-session-sharing-and-public-links.md` — direct shares (3 access levels + orthogonal permission-approval toggle, typed-error to enable on view-only/inactive) + always-view-only-**by-construction** public links (no write code path; BCL-hashed tokens, expiry/max-uses/revoke). Consumes the collaboration/01 grant primitive. *(Unblocked by the E2E descope; built in the go-build pass.)*
4. `[ ]` `ops/03-deployment-topology-and-multi-db.md` — **Not built.** Now that `Agnes.Relay` exists it's a real separately-deployed service; this doc's multi-DB/topology work becomes relevant. Buildable; low priority until real usage.

### Phase 11 — The advanced/compounding tier
1. `[x]` `providers/04-profiles.md` — named, reusable launch configs (agent/dir/worktree/skip-perms/mcp/git-cred/sandbox/model) with `OpenSessionFromProfile`, a picker + save-as, and a decoupled `ConnectedServiceProfileId` seam for later credential wiring. *(Built directly in the go-build pass — decoupled from the broker.)*
2. `[x]` `extensibility/06-generic-human-in-the-loop-webhook.md` — external `/v1/attention-requests` REST (create/poll) unioned into the inbox, callback delivery with retry, timeouts, per-caller scoping.
3. `[x]` `connectivity/05-multi-machine-workspace-model.md` — `Workspace` (logical project across hosts) + `Checkout` (a host's on-disk copy) with one shared repo-URL normalizer; `CheckoutManager` reuses deep-git (clone / same-host worktree, carry-stash branch switch, clean-up refusing uncommitted work); client `WorkspaceRegistry` aggregates checkouts across the multi-host pool; a which-checkout new-session step. *(Built on handoff + deep-git.)*
4. `[x]` `providers/03-quota-monitoring.md` — the optional `IQuotaReportingProvider` capability + cached-snapshot service + a client badge VM, plus a **real Claude quota reporter** (adapted from CodeyBox's `ClaudeQuotaProbe`; reuses the token `ClaudeCredentialProvider` reads; retain-last-good resilience). Template retained. *(Real Claude quota built in the go-build pass.)*

## Not scheduled

- **`platform/04-standalone-ide-shell.md`** — explicitly not recommended to build; revisit only on a deliberate product-scope decision, not by accretion.
