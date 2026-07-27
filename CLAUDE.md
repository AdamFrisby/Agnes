# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Agnes: a remote interface to coding CLIs. One **host** daemon runs coding agents (Claude Code, OpenCode, Codex) in their **ACP** (Agent Client Protocol) mode; **many clients** (Avalonia desktop, Avalonia Android, Uno web/WASM) connect to it, similar to `claude` in `tmux`+`ssh` but without a fixed character grid — sessions are event-sourced and reflow natively per client. Status: alpha (see `README.md`, `docs/architecture.md`).

## Build & test

Requires the **.NET 10 SDK** (pinned in `global.json`). The backend (core, host, client, UI view models) and all tests build without extra workloads via the `Agnes.Core.slnf` solution filter — this is what CI builds:

```bash
dotnet build Agnes.Core.slnf                          # backend + tests
dotnet test  Agnes.Core.slnf                           # all tests
dotnet test tests/Agnes.Host.Tests                     # one test project
dotnet test tests/Agnes.Host.Tests --filter FullyQualifiedName~AuthRateLimitTests  # one class/test
```

Tests use **xunit**. `tests/Agnes.TestKit` holds shared fakes (e.g. `FakeAcpAgent`); `tests/Agnes.Integration.Tests` and the `recordings/*.json` fixtures (`RecordedHost`) drive end-to-end scenarios offline without a real CLI or VM.

The Android client (`src/Agnes.App.Mobile`) needs the `android` workload, a JDK 17+, and the Android SDK (API 36 platform + build-tools):

```bash
dotnet build   src/Agnes.App.Mobile/Agnes.App.Mobile.csproj              # compile
dotnet publish src/Agnes.App.Mobile/Agnes.App.Mobile.csproj -c Release \
  -f net10.0-android                                                     # signed APK
```

Its views and view models are also compiled — and **rendered** — without the workload by the headless
preview harness, which is part of `Agnes.Core.slnf` and so runs in CI:

```bash
dotnet run --project tools/Agnes.MobilePreview -- screenshots/mobile     # PNGs of every screen
```

The Uno UI app (`src/Agnes.App`) is a separate subtree with its own solution, not in `Agnes.Core.slnf`. Its web head needs the `wasm-tools` workload:

```bash
dotnet build src/Agnes.App/Agnes.App/Agnes.App.csproj -f net10.0-desktop       # Linux/macOS/Windows (Skia)
dotnet build src/Agnes.App/Agnes.App/Agnes.App.csproj -f net10.0-browserwasm   # web (needs wasm-tools)
```

Run the host directly for manual testing: `dotnet run --project src/Agnes.Host` (logs a pairing code; configure agent launch commands in `appsettings.json`). Screenshots of the UI are generated offline against a simulated host via `dotnet run --project tools/Agnes.Screenshots`. `tools/Agnes.Record` records a live/sandboxed agent session to a `recordings/*.json` fixture.

Package distributable native builds with `./build.sh` / `./build.ps1` (outputs to git-ignored `builds/`); see the script headers for target flags (`linux windows mac android web`, `--client-only`).

## Architecture

```
Host daemon ── spawns each CLI (ACP mode, or a native stream-json adapter)
            ── normalizes updates -> event-sourced log (SQLite) + session catalogue
            ── ASP.NET Core + SignalR hub (TLS + per-device pairing tokens)
                     │  Agnes wire protocol
   Clients ── Agnes.Client connection pool (many hosts, dozens of agents)
            ── Avalonia desktop app · Avalonia Android app · Uno web (WASM) head
```

Full design rationale: `docs/architecture.md`. Deployment/auth/config reference: `docs/deployment.md`. Operator hardening guide (shared-host `Agnes:Security:*` guardrails, residual risks): `docs/security.md`. Incus sandbox live-testing notes and known gotchas: `docs/sandbox-live-testing.md`.

### The core idea: everything is a `SessionEvent`

Every `session/update` from an agent's ACP stream is normalized into a `SessionEvent` and **appended** to a per-session log with a monotonic sequence number. This one decision is why scrollback is unlimited, multiple clients stay consistent (a joining client requests `since = cursor`, gets a snapshot to `head`, then the live tail), and reconnects resume with no lost/duplicated events. Raw PTY fallback output is carried as its own `SessionEvent` kind, interleaved in the same order. Understand this model before touching `Agnes.Host`, `Agnes.Protocol`, or `Agnes.Ui.Core` — most cross-cutting behavior traces back to it.

### Project map (`src/`)

| Project | Role |
| --- | --- |
| `Agnes.Abstractions` | Plugin & domain contracts: `IAgentAdapter`, `IAgentSession`, `SessionEvent`, `ICliFallback`. No external deps. |
| `Agnes.Acp` | Generic **ACP-over-stdio client** on StreamJsonRpc — child process lifecycle, JSON-RPC framing, capability negotiation, ACP↔`SessionEvent` mapping. Reused by every agent plugin. |
| `Agnes.Agents.ClaudeCode` / `Agnes.Agents.OpenCode` | Thin plugins over `Agnes.Acp`: launch command/args/env, auth handling, capability quirks. |
| `Agnes.Agents.Native` | Native stream-json adapter (e.g. `claude --print --input-format stream-json`) for agents driven outside ACP proper. |
| `Agnes.Agents.Codex` | Codex adapter (native app-server, persistent JSON-RPC over stdio). |
| `Agnes.Protocol` | Transport-agnostic host↔client wire contract (DTOs + hub interface: subscribe, send prompt, permission response, terminal I/O, snapshot/tail cursors). Default binding is SignalR but the contract doesn't assume it. |
| `Agnes.Registries.GitHub` / `Agnes.Registries.SkillsHub` | Skill-registry plugins (`IPromptRegistryProvider`): a GitHub repo of `SKILL.md` bundles (defaults to the official `anthropics/skills`), and the skillshub.wtf index. The GitHub package also holds the shared `GitHubSkillBundles` fetcher, which SkillsHub reuses — the same relationship the agent plugins have with `Agnes.Acp`. |
| `Agnes.Registries.McpRegistry` | MCP-catalogue plugin (`IMcpCatalogProvider`) over the official registry at `registry.modelcontextprotocol.io`. |
| `Agnes.Host` | ASP.NET Core daemon: plugin loader, `SessionManager`, event-sourced SQLite store, `PtyManager` fallback, SignalR hub, device-pairing/GitHub/keypair auth, scheduled tasks. |
| `Agnes.Client` | Frontend-agnostic client library: connection pool across multiple hosts, snapshot+tail replay, auto-reconnect, device-token store. |
| `Agnes.Client.Simulation` | In-memory simulated host/agent for offline UI development and screenshots. |
| `Agnes.Recording` | Support for recording real/sandboxed sessions to replayable JSON fixtures (used by `tools/Agnes.Record` and `RecordedHost` test fixtures). |
| `Agnes.Sandbox` / `Agnes.Sandbox.Incus` | Optional per-session VM sandboxing: credential broker, Incus provider. See `docs/sandbox-live-testing.md`. |
| `Agnes.Ui.Core` | Framework-agnostic view models + ACP-event render logic, shared by every UI head. |
| `Agnes.App.Desktop` | Avalonia desktop client — primary, full-featured. |
| `Agnes.App.Mobile` | Avalonia **Android** client. Shares `Agnes.Ui.Core` with the desktop head and **nothing else** — see below. |
| `Agnes.App` | Uno Platform app: web (WASM) plus a desktop head, composed from `Agnes.Ui.Core`. |

New agent CLIs are added as new `Agnes.Agents.*` packages implementing `IAgentAdapter`, not by changing core code. Likewise, new places to **get** things from are new `Agnes.Registries.*` packages implementing `ICatalogProvider<T>` — `IPromptRegistryProvider` for skill bundles, `IMcpCatalogProvider` for MCP servers. Each is opt-out per operator via `Agnes:Registries:<id>:Enabled`, since each reaches the internet.

### ACP surface implemented (protocol v1)

Client → Agent: `initialize`, `authenticate`, `session/new`, `session/prompt`, `session/load`, `session/set_mode`. Agent → Client: `session/request_permission`, `fs/read_text_file`, `fs/write_text_file`, `terminal/*`. Notifications: `session/update` (streamed), `session/cancel`. Conventions: JSON keys camelCase, discriminators snake_case, all paths absolute, line numbers 1-based.

### `agnes://` links and one window per machine

**Two kinds of link, kept rigidly apart.** `agnes://pair` enrols a device and hands it the run of a host — a
deliberate act, usually in person, carrying a one-time `grant`. `agnes://session?host=…&session=…&seq=…` is a
*pointer*: it carries **no credential**, so it is safe to paste in Slack and is useful only to someone whose
device is already paired with that host. A recipient who isn't paired is told they need access and **never
offered pairing** — a message that can talk a stranger's client into enrolling with an unknown host is a
phishing primitive. `AgnesLinkRoute` enforces this rather than trusting callers: a view link's `Secret` is
null however the URL is crafted.

`seq` addresses a moment. Transcript items carry the originating event's `Sequence` (stamped by
`TranscriptBuilder`), which is the same number on every client — unlike `AnchorId`, which is a per-render GUID
good only for scrolling locally. `SessionViewModel.ScrollToSequence` resolves one to whichever item this
client rendered, landing on the first item at or after it since not every event yields one.

What a link *means* is decided once, in `Agnes.Ui.Core.AgnesLinkRoute`; each head is a few lines of glue over
it, which matters because neither entry point is testable: Android's `MainActivity` only compiles with the
android workload, and macOS delivers links as an Apple Event.

Registration differs per platform and only two can self-register: Linux writes a `.desktop` entry and Windows
an `HKCU` protocol key, both at startup pointing at the current executable (Agnes ships as a bare download
with no installer). macOS reads `CFBundleURLTypes` from a bundle on disk, so `build.sh`/`build.ps1` produce a
real `Agnes.app` and the runtime registration no-ops there.

The desktop is **single-instance** (`SingleInstance`): one window reaches as many hosts as you like, so a
second copy only competes for the same saved tabs. A second launch forwards its link to the running one over a
named pipe and exits. Two rules: the first listener is opened *synchronously* before the claim is handed back
(otherwise clicking a link just after launch races it and silently does nothing), and any failure in the gate
resolves to "run anyway" — the worst outcome of a bug there must be two windows, never zero. Multiple windows
are still available within the one process by detaching a tab.

### Talking to a host: always through `AgnesHttp`

An Agnes host is commonly **self-signed and authenticated by a pinned certificate fingerprint**, learned from
the pairing QR/link (`PinnedTls`). A default `HttpClient` rejects exactly those certificates, so any REST call
to a host built with `new HttpClient()` fails the handshake on precisely the deployments Agnes is designed for
— while the SignalR hub, which does honour the pin, stays connected and makes it look like the host is fine.

So: every call to a host goes through `AgnesHttp.For(pin)`, and the pin comes from the connection
(`IAgnesHost.PinnedFingerprint`) or the saved host record — never from a fresh guess. The desktop carries
`HostEndpoint` (url + token + fingerprint, with an `Http` property); mobile has `HostLink.Http`; the CLI stores
`HostEntry.Fingerprint`. `AgnesHttp` pools the *handler* per pin and hands out a *client* per call, because the
management helpers set an `Authorization` header on the client they're given and a shared one would let two
hosts' tokens cross. A call that legitimately reaches two services with different trust rules takes two clients
(see `GitHubDeviceLogin.CompleteAsync`, which polls github.com and exchanges at the host).

### Security model

TLS listener on the host; new clients pair via a short code/QR (or GitHub device-flow SSO, or an `authorized_keys`-style P-256 keypair) and receive a per-device bearer token, individually revocable and stored hashed. SignalR connections authenticate with that token; per-session authorization gates group membership. Agents ask for permission per tool call by default (`--permission-prompt-tool stdio` control protocol) — `--dangerously-skip-permissions` / autonomous mode is opt-in per session, never default.

### Dependency policy

Reputable, first-party dependencies preferred — supply-chain risk is treated as real. Notably: **no** third-party ACP NuGet package (`dotacp.*`, `AgentClientProtocol4CSharp` are deliberately avoided — low downloads, single obscure owner); the ACP subset Agnes needs is hand-modeled on Microsoft's StreamJsonRpc instead. Keep this discipline when adding new plugin surfaces.

## Design directives

How to add behaviour to Agnes. These are defaults, not absolutes — deviate when a case genuinely warrants it, and say why in the code/PR.

- **Prefer events over direct calls.** Non-trivial actions flow through the event spine (`IEventBus` in `Agnes.Abstractions.Events`), not hard-wired method calls, so plugins can observe, intercept, mutate, or veto them. The convention: `Before*Event` is a `CancelableEvent` dispatched *before* an action commits (an interceptor may rewrite its settable payload or `Cancel()` it — each action defines what a veto does); `*edEvent` is an observe-only fact dispatched *after*. Inbound agent facts ride the spine too — `SessionEvent : IAgnesEvent`, so a plugin can observe `ToolCallEvent`, `TurnEndedEvent`, etc. Add a new action as **one event record + one dispatch at that action's own call site** — never a central router or `switch`. See `.ideas/00d-event-spine-and-ui-extensibility.md` for the full taxonomy and the implemented surface.
- **Prefer modularity and plugin-ness.** New capabilities are plugins, not edits to core. A new agent CLI is a new `Agnes.Agents.*` implementing `IAgentAdapter`; a new auth method, automation trigger, git host, MCP preset, transport, event store, or client UI extension is a new implementation registered through an `IPluginRegistry<T>` + `IPluginPointMerger`, merged from built-ins and NuGet-installed plugins alike. Do not special-case core to know about a concrete feature. **No god objects / monolithic hubs**: the `EventBus` knows about zero concrete event types, events are split one-file-per-domain, and registries are generic — keep that discipline.
- **Prefer pure, functional interfaces.** Model contracts as functions over their inputs that return values, rather than side effects on shared, mutable state. Take dependencies as constructor/parameter inputs (DI); don't reach for ambient singletons or module-global statics, and don't mutate shared state as a covert channel. Where a component must hold state, keep it local and explicit. On the spine specifically: an interceptor mutates only its own event payload, and an observer must never change the action's outcome (its exceptions are isolated). Favour immutable records for contracts and events.
- **Prefer strong static typing; keep loose JSON at the boundary.** Model data as typed records and enums the compiler can check. Untyped, dynamic JSON (`JsonElement`/`JsonDocument` traversal, the `dynamic` keyword, `Dictionary<string, object>` bags, `Deserialize<object>`) is acceptable **only** at a genuine external edge — parsing a CLI, API, or file whose schema we don't own — and even there you deserialize into typed records *immediately* rather than letting `JsonElement`/`object` flow inward. Our own wire contract, domain model, and internal data are always fully typed. A `JsonElement`/`object`/string-bag field anywhere non-boundary (especially in `Agnes.Abstractions` or `Agnes.Protocol`) is a red flag. When a boundary schema is genuinely polymorphic (a field that's a string in one message and an array in the next), keep just that sub-field as `JsonElement` and say why — don't untype the whole payload.

## Other notes

- `.ideas/` is git-ignored planning scratch (feature backlog specs + a phased dependency-ordered build plan) — not shipped docs. A spec gets promoted into `docs/` and deleted from `.ideas/` once actually implemented; don't treat its contents as current behavior.
- CI (`.github/workflows/ci.yml`) runs on PRs to `main`, daily (only if there were commits), and on demand — not on every push to main. Three jobs: `build-test` (restores/builds/tests `Agnes.Core.slnf`, with a single automatic retry on test failure to absorb a known cold-start JIT flake in the desktop simulation tests), `mobile-build` (packages the Android APK with the `android` workload), and `ui-build` (builds the Uno heads with `wasm-tools`).
- All projects: nullable enabled, warnings as errors, `LangVersion=latest` (`Directory.Build.props`) — expect a strict build. Philips.CodeAnalysis analyzers run too; the curated rule set (and why each is on/off) lives in `.editorconfig`.
- The Uno app (`Agnes.App`) is transitional: the **web** head is what's left of it, and it too is slated to consolidate onto Avalonia. Don't over-invest in Uno-specific shells — put genuinely shared logic in `Agnes.Ui.Core` and let the Avalonia heads lead.

- **Both Avalonia heads wear the Multitudal brand.** The palette, type and geometry live in
  `Themes/Tokens.axaml` in each head (desktop and mobile) as an Avalonia translation of the brand
  bundle's `tokens/*.css`; the desktop's brand styles (type classes, surfaces, the button family) are
  in `Themes/AppStyles.axaml`. Views ask for a role — `PanelAlt`, `Danger`, `Button.primary` — never a
  hex literal, and the Agnes gradient (violet → magenta → coral) appears at most once per view. The
  desktop token file also re-points Fluent's and Dock's own control resources at those roles, because
  both libraries alias their neutral chrome internally with `StaticResource`: override the leaf key a
  template actually reads, not the palette underneath it. `docs/mobile.md` § Brand states the rules.

- **Icons are named, never typed as characters.** Every icon in every head is a glyph from Microsoft's
  fluentui-system-icons, reached through `FluentIcons` — `<ic:SymbolIcon Symbol="Dismiss" />`, or
  `{icx:SymbolIcon Symbol=Dismiss}` in a `Content=`. The whole ~2800-icon catalogue is available on
  purpose, so a plugin screen can ask for any glyph without editing a head. **Never an emoji, and never
  a stand-in text character** (`▦`, `⋯`, `✕`, `★`): an emoji is a colour bitmap that ignores
  `Foreground`, so it can't take a status hue, and neither emoji nor the geometric-shapes block is
  covered by the three embedded brand fonts — those glyphs fell through to the OS and rendered as tofu
  boxes on Linux. A `SymbolIcon` inherits `Foreground` like text, so it wears the roles and the
  `.tone.*` hues; `FontSize` does *not* inherit (FluentIcons re-registers it), which is why
  `Themes/AppStyles.axaml` defaults it. Variant carries meaning: `Regular` for an affordance, `Filled`
  for a state that is on. `IconVariant="Color"` is unused — unreliable on Skia, and multi-colour breaks
  one-meaning-per-hue. Which icon to show is view-model state only when it *varies*: shared view models
  in `Agnes.Ui.Core` name a `Symbol` (via the dependency-free `FluentIcons.Common`) and never embed a
  glyph in a label string; a constant icon belongs to the view. The mobile head is the one exception —
  its own chrome stays on the Lucide geometry in its `Themes/Icons.axaml` that matches the web design
  kit, and it carries FluentIcons only for symbols it doesn't own.
- **One meaning per hue.** State is shown in colour, and each colour means exactly one thing across the
  whole desktop app: **sky** = in motion (a turn running, a tool in flight), **amber** = blocked on you
  (permission cards, the answer bar, the state banner, the approvals count), **mint** = done/healthy
  (completed tools, plan entries, added diff lines, `main`), **pink** = failed or destructive, and
  **violet** stays reserved for selection and brand so it never competes. The hues are the
  `Status*` roles in `Themes/Tokens.axaml`; views bind `Classes.working`/`.attention`/`.review`/`.error`
  and the tone styles in `Themes/AppStyles.axaml` do the colouring. Don't paint something a status hue
  because it looks good — a permanently-mint label is the same mistake as a monochrome one.

- **Themes are colours, not stylesheets.** `Themes/Tokens.axaml` defines each variant as ~32 **colours**
  (`AccentColor`, `PanelColor`, …); every brush the app binds — the roles, the status hues, Dock's
  chrome, Fluent's leaf keys — is declared once over `{DynamicResource <role>Color}`. So a new theme is
  one `<ResourceDictionary>` of colours keyed by its own `ThemeVariant` (which inherits Dark or Light,
  so anything it omits falls through) plus a `ColorPaletteResources` block, and one line in
  `Themes/ThemeCatalog.cs`. `Themes/Spacegray.axaml` is the worked example. Two Avalonia facts shape
  this: `FluentTheme.Palettes` is the supported way to retint stock controls and *does* reach Fluent's
  neutral ramp, but it throws on any variant that isn't Light or Dark — hence the leaf-key bridge for
  ported themes, and `ThemeManager` swapping a flavour's palette into the inherited slot at runtime.
  Never alias a themeable brush with `StaticResource`: it binds at load and no theme can move it.

- **Adding a theme is a colour list, not code.** `Themes/Tokens.axaml` defines each variant as
  *colours* (`AccentColor`, `PanelColor`, …); every brush downstream — the roles views bind, the status
  hues, Dock's chrome, Fluent's leaf keys — is declared once over `{DynamicResource <role>Color}`, so a
  theme states ~34 colours and everything follows. A theme is a `ThemeVariant` in `Themes/ThemeCatalog.cs`
  that *inherits* Dark or Light (anything it omits falls through), a colour set keyed by that variant, and
  a `ColorPaletteResources` beside it; `Themes/Spacegray.axaml` is the worked example. Two Avalonia
  constraints shape this: `FluentTheme.Palettes` is the only hook that reaches Fluent's neutral ramp
  (it aliases that ramp internally with `StaticResource`), but it throws on any variant other than Light
  or Dark — hence the leaf-key bridge for custom themes, and `ThemeManager` swapping a flavour's palette
  into the slot it inherits.

- **The Android client is not the desktop one reflowed.** `Agnes.App.Mobile` shares view models with the
  desktop head only through `Agnes.Ui.Core` (`SessionViewModel` and friends); its shell, navigation,
  screens and theme are its own. The desktop is a workbench — docked panels, a tab strip, a terminal. A
  phone is a cockpit: four destinations (Sessions · Inbox · Search · More), one navigation stack, a
  session screen that owns the whole display, and every secondary surface summoned as a bottom sheet.
  Approvals are promoted out of the transcript and answerable from the Inbox without opening a session,
  because unblocking a stuck agent is the thing a phone is genuinely better at. If you're tempted to
  port a desktop panel over, add a sheet instead. See `docs/mobile.md`.
