# Agnes feature backlog

This folder is a working backlog of capabilities Agnes needs to grow from an early alpha into a well-rounded, best-in-class remote interface to coding-agent CLIs — one spec file per feature, each self-contained enough to hand to an implementer with no other context.

This directory is gitignored — it's planning scratch space, not product docs. Promote a file into `docs/` (and delete it from here) once its feature actually lands.

## How to read a spec file

Every file follows the same shape:

- **Background** — the problem this solves and why it matters for Agnes, on its own terms.
- **Current state in Agnes** — what exists today (grounded in Agnes's actual code and docs) and what's missing.
- **Proposed design** — a C# interface sketch that fits Agnes's existing plugin pattern (see [`00-plugin-architecture.md`](00-plugin-architecture.md)). Not every feature is plugin-shaped — some are core protocol/host work — and each file says so up front.
- **Acceptance criteria** — concrete, testable conditions a real implementation needs to satisfy before it's considered done.
- **Open questions** — things a real implementation will need to decide that this doc doesn't prejudge.

## Read this first

[`00-plugin-architecture.md`](00-plugin-architecture.md) — proposes generalizing Agnes's existing `IAgentAdapter`/`ISandboxProvider` pattern into a first-class plugin system with several new extension points (transport, auth, secure channel, voice, notifications, sharing, memory-index, git-host, automation-trigger, bug-report-sink). Most feature docs below hang off one or more of these new interfaces.

[`security/01-end-to-end-encryption.md`](security/01-end-to-end-encryption.md) is also worth reading early even if you're not implementing it yet — it sets the cryptographic ground rules (mutually-authenticated TLS, no custom protocols) that several other docs (connectivity, connected-services, automations, sharing) build on.

[`PHASES.md`](PHASES.md) maps the hard/soft dependencies between every doc in this folder and groups the work into 11 phases of at most 4 items each, ordered by usefulness to a developer. Use it instead of the "Suggested build order" list below for anything beyond a rough first pass.

## Index, by category

### Connectivity — reaching your machine from anywhere
- [`connectivity/01-relay-and-tunneling.md`](connectivity/01-relay-and-tunneling.md) — the single biggest gap. No NAT-traversal story today.
- [`connectivity/02-multi-server-support.md`](connectivity/02-multi-server-support.md)
- [`connectivity/03-session-handoff.md`](connectivity/03-session-handoff.md)
- [`connectivity/04-device-linking-and-restore.md`](connectivity/04-device-linking-and-restore.md)
- [`connectivity/05-multi-machine-workspace-model.md`](connectivity/05-multi-machine-workspace-model.md)

### Providers — which agent CLIs and accounts Agnes can drive
- [`providers/01-provider-breadth-acp-catalog.md`](providers/01-provider-breadth-acp-catalog.md)
- [`providers/02-connected-services-credential-broker.md`](providers/02-connected-services-credential-broker.md)
- [`providers/03-quota-monitoring.md`](providers/03-quota-monitoring.md)
- [`providers/04-profiles.md`](providers/04-profiles.md)
- [`providers/05-model-and-engine-selection.md`](providers/05-model-and-engine-selection.md)
- [`providers/06-provider-authentication-detection.md`](providers/06-provider-authentication-detection.md)

### Sessions — the core interaction loop
- [`sessions/01-session-forking-and-replay.md`](sessions/01-session-forking-and-replay.md)
- [`sessions/02-direct-vs-synced-sessions.md`](sessions/02-direct-vs-synced-sessions.md)
- [`sessions/03-pending-queue-and-steering.md`](sessions/03-pending-queue-and-steering.md)
- [`sessions/04-participant-routing-and-subagents-panel.md`](sessions/04-participant-routing-and-subagents-panel.md)
- [`sessions/05-session-read-state-and-shortcuts.md`](sessions/05-session-read-state-and-shortcuts.md)
- [`sessions/06-tool-timeline-normalization.md`](sessions/06-tool-timeline-normalization.md)
- [`sessions/07-local-cli-wrapper-and-handoff.md`](sessions/07-local-cli-wrapper-and-handoff.md)

### Security
- [`security/01-end-to-end-encryption.md`](security/01-end-to-end-encryption.md)
- [`security/02-enterprise-auth.md`](security/02-enterprise-auth.md)

### Collaboration
- [`collaboration/01-friends-and-social.md`](collaboration/01-friends-and-social.md)
- [`collaboration/02-session-sharing-and-public-links.md`](collaboration/02-session-sharing-and-public-links.md)

### Voice
- [`voice/01-voice-assistant.md`](voice/01-voice-assistant.md)

### Git & files
- [`git-and-files/01-deep-git-integration.md`](git-and-files/01-deep-git-integration.md)
- [`git-and-files/02-review-comments.md`](git-and-files/02-review-comments.md)
- [`git-and-files/03-attachments-and-file-browser.md`](git-and-files/03-attachments-and-file-browser.md)

### Extensibility
- [`extensibility/01-mcp-management.md`](extensibility/01-mcp-management.md)
- [`extensibility/02-prompts-skills-library.md`](extensibility/02-prompts-skills-library.md)
- [`extensibility/03-automations.md`](extensibility/03-automations.md) — "cron for agents"; a natural extension of Agnes's existing scheduled-task support.
- [`extensibility/04-channel-bridges.md`](extensibility/04-channel-bridges.md)
- [`extensibility/05-scriptable-agent-cli.md`](extensibility/05-scriptable-agent-cli.md) — a headless CLI for scripts/CI, distinct from both the interactive clients and from automations.
- [`extensibility/06-generic-human-in-the-loop-webhook.md`](extensibility/06-generic-human-in-the-loop-webhook.md) — an "ask a human" API for *any* external system, not just Agnes-native sessions.

### Notifications & attention
- [`notifications/01-push-notifications.md`](notifications/01-push-notifications.md)
- [`notifications/02-inbox-and-approvals.md`](notifications/02-inbox-and-approvals.md)

### Platform
- [`platform/01-ios-client.md`](platform/01-ios-client.md)
- [`platform/02-menubar-and-tray.md`](platform/02-menubar-and-tray.md)
- [`platform/03-embedded-terminal-everywhere.md`](platform/03-embedded-terminal-everywhere.md)
- [`platform/04-standalone-ide-shell.md`](platform/04-standalone-ide-shell.md) — a scope note, not a build recommendation. Read before adding more session-adjacent editor features.

### Operations
- [`ops/01-bug-reports-and-diagnostics.md`](ops/01-bug-reports-and-diagnostics.md)
- [`ops/02-memory-search.md`](ops/02-memory-search.md)
- [`ops/03-deployment-topology-and-multi-db.md`](ops/03-deployment-topology-and-multi-db.md)
- [`ops/04-onboarding-showcase.md`](ops/04-onboarding-showcase.md)

### Delight
- [`delight/01-pets-companion.md`](delight/01-pets-companion.md)

## What Agnes already does well (don't regress these)

Keep these in mind while building the above — they're real strengths worth protecting, not just gaps to fill:

- **Per-session VM sandboxing** (`Agnes.Sandbox.Incus`) with a credential broker and audit trail — many comparable tools run agents unsandboxed directly on the host machine; don't lose this isolation while adding new capabilities.
- **Scheduled tasks with an inbox** already exist in a simpler form (`ScheduledTaskManager`/`ScheduledRunner`) — see `extensibility/03-automations.md` for how to grow this into a fuller feature rather than replace it.
- **No third-party ACP package dependency** — hand-modeled on StreamJsonRpc for supply-chain reasons. Keep this discipline as new plugin interfaces are added: prefer reputable, first-party, or well-audited dependencies over convenient but obscure ones.

## Suggested build order

See [`PHASES.md`](PHASES.md) for the full dependency graph and an 11-phase build plan (≤4 items per phase, ordered by developer usefulness). The short version: plugin architecture first, then relay/tunneling + end-to-end encryption together (the relay isn't safe to use without it), then provider breadth — everything else builds on that foundation.
