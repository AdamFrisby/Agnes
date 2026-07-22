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

### Phase 1 — Foundation + two everyday wins
1. `00-plugin-architecture.md` (DONE — core scope only) — unlocks nearly everything else; do this first. Delivered: the generalized `IPluginRegistry<T>`/`PluginRegistry<T>` pattern, migrating `IAgentAdapter` and `ISandboxProvider` onto it with no behavior change (AC1, AC4), and host-level capability negotiation (`GetCapabilities()` end to end through `Agnes.Protocol`/`Agnes.Client`, AC2/AC3). Deliberately **not** built in this pass: NuGet-based third-party plugin distribution, package signature verification, `AssemblyLoadContext` hot-reload, capability-consent enforcement, and the plugin management UI (AC5–AC13) — that's real, separate scope (the doc rates it "L" on its own); tracked as follow-on work against the same doc rather than attempted half-finished here.
2. `sessions/01-session-forking-and-replay.md` — branch a conversation instead of dead-ending it. High daily value, zero dependencies.
3. `sessions/03-pending-queue-and-steering.md` — fixes "can't send while the agent's busy," a constant point of friction today.
4. `platform/01-ios-client.md` — doubles Agnes's reachable mobile surface; the hard UX work already exists in the Android/desktop shells.

### Phase 2 — Structural enablers + remote-workflow basics
1. `sessions/06-tool-timeline-normalization.md` — formalizes something that already half-exists; unlocks git integration and broader provider support.
2. `git-and-files/03-attachments-and-file-browser.md` — get files in and out of a remote session; a real gap today.
3. `sessions/02-direct-vs-synced-sessions.md` — attach to a session started outside Agnes instead of losing track of it.
4. `sessions/05-session-read-state-and-shortcuts.md` — unread indicators and "same setup again"; cheap, high polish-to-effort ratio.

### Phase 3 — Review, terminal reuse, and social groundwork
1. `git-and-files/02-review-comments.md` — leave feedback anchored to a diff line, not lost in chat scrollback.
2. `platform/03-embedded-terminal-everywhere.md` — one terminal transport reused everywhere instead of reinvented per feature.
3. `providers/06-provider-authentication-detection.md` — stop discovering "not logged in" by watching a session fail.
4. `collaboration/01-friends-and-social.md` — foundational for session sharing later; flagged as needing a real account-model decision first.

### Phase 4 — Low-priority polish (last of the no-dependency items)
1. `ops/04-onboarding-showcase.md`
2. `platform/02-menubar-and-tray.md`
3. `delight/01-pets-companion.md` — included for completeness; genuinely optional, see its own doc.

### Phase 5 — Reachability + the security work that must ride along with it
1. `connectivity/01-relay-and-tunneling.md` — the single biggest gap: use Agnes away from your own LAN.
2. `security/01-end-to-end-encryption.md` — built alongside #1, not after it (see the exception noted above); makes the relay safe to actually use.
3. `providers/01-provider-breadth-acp-catalog.md` — a generic custom-ACP adapter plus the on-ramp for new agent CLIs.
4. `notifications/02-inbox-and-approvals.md` — one place to see everything across every session that needs you.

### Phase 6 — Git, notifications, and the automation upgrade
1. `notifications/01-push-notifications.md` — the mobile client is only as useful as its ability to reach you when you're not looking at it.
2. `git-and-files/01-deep-git-integration.md` — stash, branch, pull, push, PR checkout, without dropping to a separate terminal.
3. `extensibility/03-automations.md` — grows something Agnes already has into a real "cron for agents."
4. `sessions/07-local-cli-wrapper-and-handoff.md` — start a session the way you already start one (`agnes claude`), with clean remote handoff.

### Phase 7 — Extensibility surface
1. `extensibility/01-mcp-management.md` — quick-install presets, native-config detection, a preview before you commit to a session.
2. `extensibility/02-prompts-skills-library.md` — stop retyping the same instructions; adopts the emerging `SKILL.md` convention.
3. `sessions/04-participant-routing-and-subagents-panel.md` — a real roster view once forking and steering (phase 1) exist to build on.
4. `voice/01-voice-assistant.md` — novel and effort-heavy, but no longer blocked once the plugin system exists.

### Phase 8 — Diagnostics and the long tail of plugin points
1. `ops/01-bug-reports-and-diagnostics.md` — a structured way to report what broke.
2. `ops/02-memory-search.md` — full-text search over every session ever run, starting cheap (SQLite FTS5).
3. `security/02-enterprise-auth.md` — OIDC/mTLS/org gating; low priority until there's an actual organization asking.
4. `extensibility/04-channel-bridges.md` — real but niche; approve a permission request from a chat app.

### Phase 9 — Multi-account and multi-host connectivity
1. `providers/02-connected-services-credential-broker.md` — one login per provider, reused across every machine you pair.
2. `connectivity/03-session-handoff.md` — move a live session to a different machine without losing it.
3. `connectivity/02-multi-server-support.md` — more than one relay active in the same client at once.
4. `connectivity/04-device-linking-and-restore.md` — the three pairing/restore flows, properly distinguished in the UI.

### Phase 10 — Sharing, scripting, and account/model refinement
1. `providers/05-model-and-engine-selection.md` — pick a model per session, with favorites.
2. `extensibility/05-scriptable-agent-cli.md` — a headless CLI for CI and shell scripts, distinct from both automations and the interactive clients.
3. `collaboration/02-session-sharing-and-public-links.md` — direct sharing plus always-read-only public links.
4. `ops/03-deployment-topology-and-multi-db.md` — only relevant once the relay is a real, separately-deployed service.

### Phase 11 — The advanced/compounding tier
1. `providers/04-profiles.md` — named, reusable launch configs.
2. `extensibility/06-generic-human-in-the-loop-webhook.md` — the same "ask a human" primitive, exposed to any external system, not just Agnes-native sessions.
3. `connectivity/05-multi-machine-workspace-model.md` — one logical project, tracked across every machine it's checked out on.
4. `providers/03-quota-monitoring.md` — usage visibility for connected provider accounts.

## Not scheduled

- **`platform/04-standalone-ide-shell.md`** — explicitly not recommended to build; revisit only on a deliberate product-scope decision, not by accretion.
