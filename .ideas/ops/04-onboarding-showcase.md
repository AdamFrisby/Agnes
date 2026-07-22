# Onboarding showcase / setup wizard

| | |
|---|---|
| **Category** | Operations |
| **Plugin surface** | None — client UI feature only |
| **Priority** | P3 |
| **Rough effort** | S |

## Background

`docs/deployment.md` documents Agnes setup thoroughly, but it's written for a reader working through a terminal and a text editor: run Docker or `dotnet run`, configure TLS, read a pairing code out of the logs, paste it into a client. That's a reasonable path for someone comfortable operating a small server, but it means a brand-new user's first experience of Agnes is entirely outside the app itself — there's no in-app flow that walks them through connecting to their first host, and no in-app introduction to what Agnes can actually do once they're connected.

Two distinct problems live under "onboarding," and it's worth keeping them separate because they have different costs and different payoffs:

1. **Getting connected at all** — picking how to reach a host (direct connection today; other transports once `../connectivity/01-relay-and-tunneling.md` exists) and completing one of Agnes's existing pairing/auth flows (pairing code, GitHub device flow, keypair — all documented in `docs/deployment.md`). This is pure friction reduction: the capability already exists, the problem is that a new user has to go read docs to discover and drive it manually.
2. **Discovering what's there** — once connected, a new user has no way to know what Agnes can do beyond whatever screen they happen to land on first. As more of this backlog ships (MCP management, automations, memory search, and so on), the number of features a user could easily miss by never opening the right settings screen grows. A first-run tour is a cheap way to close that gap without requiring every feature to be independently discoverable through UI affordances alone.

Both are low-effort relative to most of this backlog because neither requires new host capability — they're client-side UI wrapping functionality (pairing, auth, feature settings) that already exists or is already planned elsewhere.

## Current state in Agnes

There is no in-app first-run wizard and no feature-showcase surface. A new user pairs by following the written docs: start the host, read the pairing code from logs or a QR the docs describe, open a client, and manually enter the host URL and code (or walk through GitHub sign-in / keypair setup, per `docs/deployment.md`). All three auth mechanisms (pairing code, GitHub device flow, keypair) already exist and are discoverable via `GET /auth/methods`, so the underlying capability is there — it's just not sequenced into a guided flow.

## Proposed design

Two independent, low-effort additions, neither needing new backend work:

- **First-run setup wizard** in `Agnes.App.Desktop` (and the mobile shell, once initial pairing there is fleshed out): a thin UI sequence over functionality that already exists — pick a transport if more than one is available (falls back to "connect to a host directly" under today's direct-only model), then run through whichever auth method the target host supports, using the existing `GET /auth/methods` discovery endpoint to decide which options to show rather than assuming one path. This is pure client-side sequencing; it doesn't need a new host capability, and it doesn't need to duplicate `docs/deployment.md`'s content — the wizard should link out to it for anything that needs host-side configuration (e.g. registering a GitHub OAuth app), since that step happens outside the client entirely.
- **Onboarding showcase**: a simple first-run card sequence in `Agnes.Ui.Core`, shown once per install, highlighting whichever of Agnes's features have actually shipped at the time. Building it as a reusable component (rather than a one-off screen) means the same renderer can later serve a "what's new" surface for release notes, without a second implementation. The showcase's content should be data-driven (a list of feature cards with title/description/screenshot) rather than hardcoded per release, so adding or removing a highlighted feature doesn't require touching the rendering code.

## Acceptance criteria

- Given a fresh install with no paired hosts, when a user opens the desktop client for the first time, then they are guided through adding a host and completing pairing without needing to consult `docs/deployment.md`, provided the host's auth configuration matches one of the client's built-in flows.
- Given a host offers more than one auth method (e.g. pairing code and GitHub sign-in both enabled), when the wizard runs, then it presents the options actually returned by `GET /auth/methods` rather than a hardcoded list — a host with GitHub sign-in disabled should not show that option.
- Given a user has already paired at least one host, when they relaunch the client, then the first-run wizard does not appear again.
- Given the onboarding showcase has been shown once, when the user relaunches the app, then it does not reappear automatically (it should remain reachable manually, e.g. from a help/about menu, but not force itself back onto a returning user).
- Given the showcase's feature list is data-driven, when a new feature card is added, then no changes are needed to the card-rendering component itself — only to the data feeding it.
- Given a user cancels or closes the setup wizard partway through, when they reopen the client, then they can resume from where they left off (or restart cleanly) rather than losing all progress and hitting a broken intermediate state.

## Open questions

- Low priority relative to actually building the features it would showcase — reasonable to defer the showcase specifically until there's a meaningful set of shipped features worth highlighting, rather than building the mechanism first and having little to put in it. The setup wizard doesn't have this dependency and could ship independently, sooner.
- Should the setup wizard be skippable entirely for users who prefer the documented manual flow (e.g. headless/automation setups using `Agnes:PairingToken`)? Likely yes — the wizard should be a convenience layered on top of the existing flows, not a mandatory gate in front of them.
