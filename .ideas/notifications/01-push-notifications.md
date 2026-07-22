# Push notifications

| | |
|---|---|
| **Category** | Notifications |
| **Plugin surface** | New `INotificationChannel` (see `../00-plugin-architecture.md`) |
| **Priority** | P1 — desktop already has native OS notifications; mobile push is the real gap |
| **Rough effort** | M |
| **Depends on** | `../00-plugin-architecture.md` (defines `INotificationChannel`) |

## Background

Agnes sessions run for a while unattended: an agent works through a multi-step task, hits a permission request it needs a human to answer, or finishes a turn and is waiting. The host knows about all of this the instant it happens, because every one of these moments is already a `SessionEvent` in the session's event-sourced log. The problem is getting that fact in front of a human who isn't currently looking at the app.

On desktop this is a solved problem: the desktop app is a long-running process, so a normal OS notification (toast/banner) fired from that process is enough. On a phone it is not solved, because mobile operating systems aggressively suspend or kill background app processes to save battery — an app that isn't in the foreground generally cannot rely on "just leave a connection open and fire a local notification when something happens." The only way to reliably reach a user on their phone when the app isn't running is a real push notification, delivered by the OS's push service (APNs on iOS, FCM on Android) from a server-side sender that is awake even when the client isn't.

This matters a lot for Agnes specifically because the whole pitch of the product is "start an agent, walk away, come back when it needs you." Without mobile push, "walk away" only works if you keep glancing at the app — which defeats the point.

## Current state in Agnes

Agnes already has a client-local notification abstraction: `Agnes.Ui.Core.ViewModels.INotifier` takes an `AppNotification` (`Title`, `Body`, `NotificationKind` of `Blocker`/`Completion`/`Error`, `SessionId`, optional `AnchorId` to scroll to) and hands it to a per-frontend implementation. `Agnes.App.Desktop` has two real implementations wired up (`NativeOsNotifier`, which shells out to `notify-send`/`osascript`/a PowerShell toast script per OS, and `AvaloniaNotifier`); the Uno mobile head has `UnoNotifier`, which only raises an in-app `InfoBar` banner and a debug trace — there is no OS-level push behind it.

That means today, on Android, a notification only reaches the user if the app process happens to still be alive and in memory. There is no push-token registration, no server-side (host-side) store of which device wants pushes for which sessions, and no way to act on a notification (approve/deny) without first opening the app and finding the right session.

## Proposed design

```csharp
namespace Agnes.Abstractions;

public interface INotificationChannel
{
    string Id { get; }   // "mobile-push" | "desktop" (wraps the existing NativeOsNotifier/AvaloniaNotifier path)
    Task RegisterAsync(string deviceId, string channelToken, CancellationToken ct = default);
    Task SendAsync(NotificationPayload payload, CancellationToken ct = default);
}

public sealed record NotificationPayload(
    string DeviceId, NotificationTrigger Trigger, string ShortHint, string SessionId);

public enum NotificationTrigger { TurnReady, PermissionRequest, UserActionRequest }
```

`INotificationChannel` sits alongside `IAgentAdapter` and `ISandboxProvider` as a normal Agnes plugin point: `Desktop` wraps the notifier code that already exists, and a new `MobilePush` implementation is added without either one knowing about the other. The existing `INotifier`/`AppNotification` types stay exactly as they are — they're the client-side "how do I show this" abstraction; `INotificationChannel` is the new host-side "how do I get this to a device that isn't currently connected" abstraction, and the two compose (a push arriving on a phone can, once the app opens, feed the same `AppNotification` pipeline the in-app banner already uses).

**Registration and storage.** `Agnes.Host` already maintains a per-device record for pairing (a revocable bearer token per paired device, per `docs/deployment.md`). Registering a push token is a natural, small extension of that same record — store it alongside the pairing token, keyed by device id, so revoking a device's pairing access (something the host can already do) also stops any further pushes to it. This avoids inventing a second device-identity concept just for notifications.

**Sending is host-driven**, not something a separate server component polls for. The host is already the one place that observes every `SessionEvent` crossing a trigger condition (a turn ending, a `PermissionRequestedEvent` being raised) — that's exactly where the code deciding "does this cross a threshold that should page someone" already has to live, so firing `SendAsync` from there needs no new process, no new polling loop, and no risk of the host and a separate notifier drifting out of sync about session state.

**Per-trigger, per-device controls.** Each of the three triggers (`TurnReady`, `PermissionRequest`, `UserActionRequest`) is independently toggleable per device, plus a master on/off, because a user waiting on a long build might want turn-ready pings but find permission-request pings noisy (or vice versa on a security-sensitive repo). A session the user is actively viewing on that specific device is suppressed on that device only — other paired devices for the same account still get notified, since "I'm looking at it on my laptop" says nothing about whether the person carrying the phone is also looking at it.

**Interactive actions and untrusted-host safety.** A push notification can carry `Allow`/`Deny` (for permission requests) or `Answer` (for user-action requests) as tap targets. The critical design constraint here is security, not convenience: a push payload is not a trusted, authenticated channel by itself — it can be delayed, replayed, or (in principle) crafted to look like it's about a session the user thinks they recognize. So `Allow`/`Deny` must only auto-execute (i.e. call `RespondToPermissionAsync` directly from the notification action, without opening the app) when the device already holds a currently-valid bearer token for the host that session belongs to. If it doesn't — a new host, a revoked pairing, an unrecognized session — tapping the action opens the app to a confirmation/pairing screen instead of silently approving anything. This turns "notification spoofing" from a security bug into, at worst, a mildly annoying extra tap.

**Content minimization.** The notification body (`ShortHint`) is deliberately small — a file path, a command name, a question count — never raw tool arguments or file contents, because push notifications are commonly shown on a lock screen where anyone glancing at the phone can read them. `ShortHint` should be computed host-side, once, from the already-normalized tool call (`NormalizedToolCall.Summary`, see `../sessions/06-tool-timeline-normalization.md`) rather than the raw event — centralizing the redaction logic in one place means a new client head doesn't have to remember to re-implement it, and there's exactly one place to audit for "did we ever leak something sensitive into a push payload."

**Push credentials: bring-your-own, not shared.** A tempting shortcut is for the Agnes project itself to hold a single APNs/FCM credential and relay every self-hosted deployment's pushes through it, so individual self-hosters never touch a push-service developer console. That trades a one-time setup cost for a standing liability: a shared relay becomes a single high-value target (whoever compromises it can send push traffic — including, if the interactive-action design above were done carelessly, spoofed approval prompts — on behalf of every Agnes deployment that uses it), and it makes every self-hosted install quietly dependent on a project-run cloud service staying online and funded indefinitely. For an early-alpha, self-hosted-first project, the more defensible default is that each deployment supplies its own FCM/APNs credentials (a one-time console setup, well-trodden for any app with mobile push) and the host talks to the push service directly. A project-run convenience relay can be revisited later as an opt-in, not the default, once there's an operational team to run it responsibly.

## Acceptance criteria

- Given a device has registered a push token and the app is fully backgrounded/killed, when a watched session completes a turn, then the device receives an OS-level push notification (not just an in-app banner) whose body is limited to the minimized `ShortHint` text.
- Given a device has disabled the "Permission requests" trigger but left "Turn ready" enabled, when a permission request fires on a watched session, then no push is sent for it, while a turn-ready event still sends one.
- Given a session is open and foregrounded on device A, when a push-eligible event fires for that session, then device A does not receive a push for it while device B (paired to the same account, not viewing that session) still does.
- Given a push notification's `Allow`/`Deny` action, when the receiving device holds a currently-valid bearer token for the session's host, then tapping `Allow` calls `RespondToPermissionAsync` directly without opening the app.
- Given a push notification's `Allow`/`Deny` action, when the receiving device does not hold a valid token for that host (e.g. its pairing was revoked, or the notification references an unrecognized host), then tapping the action never auto-approves — it opens the app to a confirmation/pairing flow instead.
- Given a device's pairing is revoked through existing device management, when a subsequent trigger fires, then that device receives no further pushes (its push token is invalidated along with its pairing token).
- Non-regression: after `INotificationChannel` and the `Desktop` channel implementation are introduced, existing desktop native OS notifications (`NativeOsNotifier`/`AvaloniaNotifier`) continue to fire exactly as before.

## Open questions

- Native push integration is a real per-platform engineering cost on Agnes's Uno/Avalonia stack (APNs and FCM each need platform-specific registration code per UI head) — worth scoping as its own spike before committing to the full feature.
- Should desktop ever move off process-resident native notifications onto the same push path (e.g. for a desktop app that isn't always running)? Not needed for v1 — deferred until there's a concrete case where a desktop client is expected to be closed for long stretches yet still notified.
- Exact push-payload retry/expiry semantics (what happens to a push queued for a device that's offline for hours) need a decision once a concrete FCM/APNs integration is chosen — both services have their own delivery/expiry semantics worth reusing rather than re-inventing.
