# iOS client

| | |
|---|---|
| **Category** | Platform |
| **Plugin surface** | None — this is a UI head, not a plugin |
| **Priority** | P1 — clear, well-scoped gap; Agnes already has the shared substrate |
| **Rough effort** | L (mostly platform/store-process overhead, not architecture) |

## Background

Agnes's whole value proposition is "check on and steer your agents from wherever you are," which only holds up if "wherever you are" includes a phone in your pocket — and a meaningful fraction of phones are iPhones. Right now Agnes has no iOS presence at all: no build target, no App Store listing, nothing to install. Anyone who wants mobile access today needs an Android device. That's a real, well-defined product gap, not a speculative one.

The reason this is worth calling out as its own scoped doc rather than folding it into general mobile-platform work is that Agnes's UI architecture already separates "what a phone-sized Agnes client looks like" (a shared, already-solved problem) from "which OS build targets exist" (the actual gap here) — see Current state below.

## Current state in Agnes

Agnes's UI is built on Uno Platform with a shared core: `Agnes.Ui.Core` holds the view models and per-`SessionEvent` render components (message stream, tool-call cards, diff viewer, plan view, permission prompt, terminal view), and `Agnes.App` composes two genuinely distinct shells on top of that core — a multi-pane **Desktop shell** and a single-column, navigation-stack **Mobile shell** (`docs/architecture.md`). Today `Agnes.App` ships that Mobile shell only on Android (`net10.0-android`). There is a separate, Avalonia-based desktop app, `Agnes.App.Desktop`, which is architecturally unrelated to the Uno mobile/web heads and out of scope here.

Because the Mobile shell already exists and already lives in its own namespace (not a resized desktop layout bolted on), the hard product-design work — "what does a phone-sized Agnes client look like, and how does it differ from the desktop one" — is done. Uno Platform natively supports iOS as a build target alongside its existing Windows/macOS/Linux/Android/WASM heads, so what's missing is narrower than it sounds: a `net10.0-ios` head project, the surrounding build/CI plumbing, and the App Store distribution process.

## Proposed design

This is deliberately not an architecture problem — it's a build-target-and-distribution addition to an existing, working shared layer:

1. **Add an iOS head** to `Agnes.App` using Uno Platform's standard iOS target. It reuses `Agnes.Ui.Core`'s existing shared view models and the Mobile shell already built for Android as-is — the same shell serves both phone form factors, which is the entire point of having factored the shell out from the Android-specific project in the first place. Any iOS-specific work should be limited to platform glue (push registration, notification permission prompts, safe-area handling) rather than new UI.
2. **Build tooling.** `build.sh`/`build.ps1` already skip gracefully when a workload (e.g. the Android or WASM workload) isn't installed locally, so the same pattern extends naturally to an iOS workload check. CI (the `ui-build` job in `.github/workflows/ci.yml`) needs an iOS workload install step, which in practice means adding a macOS runner to the build matrix — a real cost Android and WASM didn't incur, since iOS toolchains only run on macOS/Xcode.
3. **Distribution process.** Apple Developer Program enrollment, provisioning profiles, App Store Connect app registration, and a release channel (App Store and/or TestFlight) are pure process work with real calendar lead time (account approval, first-submission review) independent of any engineering effort — worth starting early if this is prioritized, since it can run in parallel with step 1.

Given Agnes is an early-stage, fast-moving project, a full public App Store listing is probably premature: App Store review adds friction and lag to every release at a point where the product is still churning quickly. **TestFlight-first** (internal/external testing distribution, no public store listing) is the more defensible sequencing — it gets a real iOS build into real hands with a fast iteration loop, and the public App Store submission can follow once the product has stabilized enough that review overhead isn't fighting the release cadence.

There is no sandboxing concern to design around here: the phone is always a Agnes **client**, never a **host** — it never spawns or runs a coding-agent CLI itself, it only talks to a host daemon over the network. iOS's restrictions on arbitrary subprocess execution are therefore irrelevant to this feature; they would only matter if Agnes ever needed a phone to run an agent CLI locally, which is explicitly not this product's model.

## Acceptance criteria

- An `Agnes.App` iOS head builds successfully from the existing shared `Agnes.Ui.Core` and Mobile-shell code, with no duplicated view-model or rendering logic introduced for iOS specifically.
- The iOS build runs the Mobile shell (not a repurposed Desktop shell) and reaches functional parity with the Android Mobile shell for core flows: pairing to a host, viewing a session's live transcript, responding to a permission request, and receiving a notification.
- CI builds the iOS head on every relevant PR/branch (via a macOS runner in the `ui-build` matrix) and fails the build on iOS-specific compile errors, the same as it already does for Android/WASM.
- A build without the iOS workload installed locally (e.g. on a non-macOS dev machine) still completes the rest of the build via the existing graceful-skip pattern, rather than failing outright.
- The app is distributable via TestFlight to a defined test group, with a documented process for cutting a new TestFlight build from CI or a local machine.
- Non-regression: adding the iOS head does not change build output, behavior, or build time materially for the existing Android, WASM, or desktop targets.

## Open questions

- Public App Store listing timing — deferred until the product's release cadence stabilizes, per the TestFlight-first reasoning above; worth revisiting once churn slows.
- Mac Catalyst (running the iOS build as a native Mac app via Catalyst) is a plausible bonus given Uno's iOS support often enables it near-for-free, but it's a distinct distribution surface from the existing Avalonia desktop app and shouldn't be assumed in scope without a separate decision.
- Push notification entitlements for iOS (APNs) are a shared concern with `../notifications/01-push-notifications.md` — sequencing/dependency between the two docs should be confirmed so the iOS head isn't built twice (once without push, once with).
