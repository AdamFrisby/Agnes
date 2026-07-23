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
- `[ ]` — **not built** (reason noted: infra-blocked, gated behind another unbuilt item, or a pending product decision).

**Tally of the 42 scheduled items: 13 `[x]` complete · 16 `[~]` basic/partial · 13 `[ ]` not built** (29 have at least a working MVP). `platform/04` remains excluded.

### Phase 1 — Foundation + two everyday wins
1. `[x]` `00-plugin-architecture.md` — the generalized `IPluginRegistry<T>`/`PluginRegistry<T>` pattern; `IAgentAdapter`/`ISandboxProvider`/auth/git-host/MCP/transport/event-store all migrated onto it (AC13.1–6); NuGet install + signature verification + `AssemblyLoadContext` loader; host + client capability negotiation; client-plugin registry; the event spine; and the plugin management UI. (Went well beyond the original "core scope only" pass.)
2. `[x]` `sessions/01-session-forking-and-replay.md` — branch a conversation instead of dead-ending it. High daily value, zero dependencies.
3. `[~]` `sessions/03-pending-queue-and-steering.md` — fixes "can't send while the agent's busy." **Partial:** queue + three send policies + send-now + discarded list shipped; true in-turn `ISteerableSession` injection deferred (uses the always-available cancel-then-resend fallback).

### Phase 2 — Structural enablers + remote-workflow basics
1. `[x]` `sessions/06-tool-timeline-normalization.md` — formalizes something that already half-exists; unlocks git integration and broader provider support.
2. `[~]` `git-and-files/03-attachments-and-file-browser.md` — **Partial:** attachment upload + workspace path-safety shipped; the full file-browser surface is not built.
3. `[ ]` `sessions/02-direct-vs-synced-sessions.md` — **Not built.** Buildable (discovering/attaching to CLI sessions started outside Agnes), just not reached; needs per-adapter on-disk-log reading.
4. `[x]` `sessions/05-session-read-state-and-shortcuts.md` — unread indicators and "same setup again"; cheap, high polish-to-effort ratio.

### Phase 3 — Review, terminal reuse, and social groundwork
1. `[x]` `git-and-files/02-review-comments.md` — leave feedback anchored to a diff line, not lost in chat scrollback.
2. `[~]` `platform/03-embedded-terminal-everywhere.md` — **Partial:** client terminal-I/O protocol over the existing `ICliFallback`/`TerminalOutputEvent`, a desktop panel via `Iciclecreek.Avalonia.Terminal`, and provider "Log in" routed through `ICliFallback` all shipped; streaming live login output back to a client-visible session + broader runtime polish deferred.
3. `[x]` `providers/06-provider-authentication-detection.md` — stop discovering "not logged in" by watching a session fail.
4. `[ ]` `collaboration/01-friends-and-social.md` — **Not built.** Needs a real account/identity-model decision first.

### Phase 4 — Low-priority polish (last of the no-dependency items)
1. `[x]` `ops/04-onboarding-showcase.md` — first-run setup wizard over `GET /auth/methods` + data-driven shown-once showcase cards.
2. `[x]` `platform/02-menubar-and-tray.md` — the system-tray icon (aggregate status + jump-to-session); the separate self-host operator status tool is intentionally out of scope per the spec.
3. `[ ]` `delight/01-pets-companion.md` — **Not built.** Deferred per its own spec pending a product-tone decision; its tone-neutral ambient-status core is already covered by the inbox + tray.

### Phase 5 — Reachability + the security work that must ride along with it
1. `[ ]` `connectivity/01-relay-and-tunneling.md` — **Not built.** Needs a hosted relay/tunnel service — external infrastructure, not verifiable headlessly here.
2. `[ ]` `security/01-end-to-end-encryption.md` — **Not built.** Coupled to the relay + a key-exchange design; gates the broker→quota→profiles and session-sharing chain.
3. `[x]` `providers/01-provider-breadth-acp-catalog.md` — a generic custom-ACP adapter plus the on-ramp for new agent CLIs.
4. `[~]` `notifications/02-inbox-and-approvals.md` — **Partial:** the tier-1 cross-session approvals inbox shipped (and is unioned with the external-webhook requests); richer inbox tiers/filters pending.

### Phase 6 — Git, notifications, and the automation upgrade
1. `[~] template` `notifications/01-push-notifications.md` — **Partial:** `INotificationChannel` plugin point + a template mobile-push stub + a Desktop channel + spine-driven dispatch with per-trigger/per-device toggles, active-session suppression, and the untrusted-payload safety guard. No real FCM/APNs backend.
2. `[~]` `git-and-files/01-deep-git-integration.md` — **Partial:** stash/branch/pull(ff-only)/push + PR list/checkout with server-side safety shipped; changed-file scoping and agent-generated commit messages deferred.
3. `[x]` `extensibility/03-automations.md` — persistence + cron scheduling + pause/resume/run-now; a real "cron for agents."
4. `[ ]` `sessions/07-local-cli-wrapper-and-handoff.md` — **Not built.** Depends on direct-vs-synced sessions + a live PTY/handoff runtime.

### Phase 7 — Extensibility surface
1. `[~]` `extensibility/01-mcp-management.md` — **Partial:** preset install + scope rules + strict/lenient toggle + effective-config preview shipped; native-config detection deferred.
2. `[~]` `extensibility/02-prompts-skills-library.md` — **Partial:** saved prompts + slash-token templates shipped; skill bundles, external registries, copy/symlink sync, and system-prompt additions deferred.
3. `[~]` `sessions/04-participant-routing-and-subagents-panel.md` — **Partial:** the subagents roster (visibility tier) shipped; true addressed message-routing / stop-a-subagent control deferred (capability flag in place).
4. `[~]` `voice/01-voice-assistant.md` — **Partial:** `IVoiceProvider` plugin + hidden controller (transcript→host-call intent mapping) + privacy-default summarizer, proven with a fake provider; real STT/TTS engines deferred.

### Phase 8 — Diagnostics and the long tail of plugin points
1. `[~]` `ops/01-bug-reports-and-diagnostics.md` — **Partial:** GitHub-issue sink (with duplicate detection) + custom endpoint + prefilled browser fallback; crash/error telemetry and the owner-only host-log diagnostic attachment deferred.
2. `[~]` `ops/02-memory-search.md` — **Partial:** the SQLite FTS5 full-text tier (the spec's intended cheap starting point) shipped; semantic/embedding search not built.
3. `[~]` `security/02-enterprise-auth.md` — **Partial:** OIDC token validation + mTLS client-cert + GitHub org/team gating shipped; the interactive OIDC authorization-code redirect deferred (token-validation core built).
4. `[~]` `extensibility/04-channel-bridges.md` — **Partial:** `IChannelBridge` plugin point + chat-id↔identity linking + authorized inbound routing + spine-driven outbound, proven with a fake bridge; real Telegram/Slack transport deferred.

### Phase 9 — Multi-account and multi-host connectivity
1. `[~] template` `providers/02-connected-services-credential-broker.md` — **Partial:** `IConnectedServiceProvider` plugin point + a named multi-profile model + broker + profile store + a commented template stub. No real vendor OAuth yet.
2. `[ ]` `connectivity/03-session-handoff.md` — **Not built.** Needs the relay + multi-node runtime.
3. `[ ]` `connectivity/02-multi-server-support.md` — **Not built.** Needs the relay + multiple live hosts.
4. `[ ]` `connectivity/04-device-linking-and-restore.md` — **Not built.** Needs the relay + a second reachable device.

### Phase 10 — Sharing, scripting, and account/model refinement
1. `[x]` `providers/05-model-and-engine-selection.md` — per-session model pick with client-side favorites reconciled against the live catalog (model threading wired for Claude Code; other adapters as their CLIs allow).
2. `[x]` `extensibility/05-scriptable-agent-cli.md` — the headless `agnes-agent` CLI (spawn/send/status/wait/stop/machines/auth), prefix matching, `--json`, CI-friendly exit codes.
3. `[ ]` `collaboration/02-session-sharing-and-public-links.md` — **Not built.** Gated on end-to-end encryption.
4. `[ ]` `ops/03-deployment-topology-and-multi-db.md` — **Not built.** Only relevant once the relay is a real, separately-deployed service.

### Phase 11 — The advanced/compounding tier
1. `[ ]` `providers/04-profiles.md` — **Not built.** Gated on the connected-services broker + provider breadth being real (needs the OAuth backends).
2. `[x]` `extensibility/06-generic-human-in-the-loop-webhook.md` — external `/v1/attention-requests` REST (create/poll) unioned into the inbox, callback delivery with retry, timeouts, per-caller scoping.
3. `[ ]` `connectivity/05-multi-machine-workspace-model.md` — **Not built.** Needs deep-git worktrees (done) **and** session handoff (not built) + multi-machine runtime.
4. `[~] template` `providers/03-quota-monitoring.md` — **Partial:** the optional `IQuotaReportingProvider` capability + cached-snapshot service + a client badge VM + a template stub. No real usage-endpoint backend.

## Not scheduled

- **`platform/04-standalone-ide-shell.md`** — explicitly not recommended to build; revisit only on a deliberate product-scope decision, not by accretion.
