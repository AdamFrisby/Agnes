# The Android client

`src/Agnes.App.Mobile` is Agnes on a phone: an Avalonia app that connects to the same host, replays the
same event-sourced sessions, and speaks the same wire protocol as the desktop client — but is designed
from the ground up for one thumb.

It is **not** the desktop layout reflowed. The only thing the two heads share is `Agnes.Ui.Core`
(`SessionViewModel`, the transcript builder, the diff model, the prompt/permission stores). Shell,
navigation, screens, theme and interaction model are the mobile head's own.

---

## What a phone is for

The desktop client is a **workbench**: docked panels, a tab strip, an embedded terminal, three columns
of a session visible at once. That's the right shape when you're sitting in front of the work.

A phone is a **cockpit**. You are not writing code on it. You are:

1. **checking what your agents are doing** — at arm's length, in a queue, between other things;
2. **unblocking one** — an agent waiting on an approval is an agent doing nothing, and answering takes
   two seconds;
3. **reading what changed** — a diff, a plan, what it touched;
4. **saying one more thing** — a steer, a correction, a next instruction.

Everything in the design follows from that list.

---

## Shape

```
┌──────────────────────────────┐
│  screen                      │   one navigation stack over four tabs;
│                              │   a pushed page owns the whole display
│                              │
├──────────────────────────────┤
│  Sessions · Inbox · Search ·…│   bottom navigation, thumb height
└──────────────────────────────┘
```

**Four destinations**, matching the four jobs:

| Tab | Answers |
| --- | --- |
| **Sessions** | What is happening right now. One card per session, ordered by need: blocked first, then running, then unread, then recent. |
| **Inbox** | What is waiting on *you*. Devices asking to join a host, then open approvals and questions across every host, answerable inline. Finished background runs below. |
| **Search** | What was ever said. Open sessions searched locally as you type; the host's full-text index over every recorded session on submit. |
| **More** | Hosts and pairing, appearance, notifications, prompts, paired devices. |

**One back gesture**, handled in one place (`ShellViewModel.GoBack`): close the sheet → let the page
handle it → pop the page → return to the first tab → let Android leave the app. The app is a single
activity, so this is the only back semantics that exists.

**Sheets, not panels.** Everything the desktop shows in a docked column is summoned as a bottom sheet
and dismissed: files changed, the tool timeline, git, session info, the agent roster, the queue. Sheets
are draggable and flingable by their grabber, tappable-away by the scrim, and closable with back.

---

## The decisions worth knowing

**Approvals are promoted.** A pending permission request is lifted out of the transcript into a card
pinned directly above the composer, and it also appears in the Inbox where it can be answered without
opening the session at all. The card states the two facts that decide the answer — what the tool
touches, and whether it can be undone — and puts Deny and Allow as full-width targets. This is the
single thing a phone is genuinely *better* at than a laptop, so it never requires scrolling to find.

**Letting a device in is an inbox item too.** A device asking to join a host sits above the agent
approvals, because it is the larger and less reversible of the two decisions and the one an attacker
would want you to skim past. The card leads with the six digits that must match the asking device's
screen — see [security.md](security.md) for why those digits are what makes the approval mean
anything.

**Send never means two things silently.** The same gesture sends when idle and queues while a turn is
running; the composer says which, above the field, whenever it isn't obvious. Stop appears beside Send
only while there is something to stop, so the destructive action is never where your thumb rests.

**The transcript is the screen.** Agent replies are full-width markdown, not bubbles — a bubble would
spend a third of a phone's width on the thing you most want to read. Your own messages are right-aligned
bubbles so they're findable when scrolling back. Tool calls collapse to one line with the detail a tap
away, because a phone transcript drowns if every `Read` is a paragraph. Thinking is hidden by default.

**It follows the tail only when you're at the tail.** Scroll up to read history and the view stays put,
with a "Latest" pill offering the way back.

**Nothing is smaller than 48dp** in its tappable dimension, press feedback is a scale-down (touch has no
hover to lean on), and haptics fire only when something actually changed in the world: a prompt left the
device, a turn ended, something needs you.

**Pairing lands on the work, not on an empty form.** After a host is paired, the phone asks it what it's
already running and offers those sessions ("On this host"), because the work usually predates the pairing:
an agent mid-turn on the desktop you walked away from, or one blocked on a permission an hour ago. Joining
one picks up its whole history — nothing is started. A host with nothing running skips the screen and goes
straight to starting a session, since an empty list isn't worth a tap. The same list is reachable later per
host from the Hosts sheet, which is how you find a session this device forgot but the host still holds.

**It ships with a demo.** On a first launch with nothing paired, the app seeds a session on the built-in
offline simulated host and primes it with a prompt. A remote-agent client is inert until you have a
host, and "install it, then go stand up a server before you can see anything" is a bad first minute. The
demo runs the real event pipeline, so what you see is honestly how the app behaves.

**Platform affordances are used where they earn their keep**, and hidden where they don't:

- **Notifications** — a blocked agent posts to the shade on a high-importance channel, a finished turn on
  a quieter one; both are suppressed while the app is foreground (you're already looking). Tapping one
  deep-links to the session.
- **Dictation** — the system speech recognizer fills the composer. Typing a paragraph of instructions on
  a phone keyboard is the worst part of driving an agent from one. The mic is *hidden entirely* on a
  device without a recognizer rather than shipping a button that does nothing.
- **Deep links** — `agnes://pair?host=…&code=…` opens the connect screen pre-filled, so scanning a host's
  QR with the system camera removes the address-and-code typing.
- **Edge-to-edge** — bar backgrounds run to the screen edge while their content clears the status bar and
  the gesture handle (`SafeSpacer`).

**No terminal.** The desktop head embeds a VT terminal; a phone does not get one. A 40-column terminal
behind a soft keyboard is worse than useless, and the things you'd use it for are covered by the git
sheet and the tool timeline.

---

## Brand

The app implements the **Multitudal** design system (`multitudal.com`), of which Agnes is one product:

- **Follows the device by default.** In dark mode that's the Agnes console palette (`#0F0A22` /
  `#16112E` / `#1C1740`, accent `#B06CF0`); in light mode it's the shared cool-violet neutrals on
  near-white with ink `#1D1546`. Either can be pinned in Appearance.
- **The Agnes gradient** (violet → magenta → coral) is the signature and appears at most once per view —
  the primary CTA, the launcher mark, a switch's on-state.
- **Type**: Space Grotesk (display), Manrope (UI/body, 15px), JetBrains Mono (code, logs, paths), all
  embedded so typography is identical on every device.
- **Icons**: Lucide-style line glyphs on a 24×24 grid at 1.75px, stroked from geometry rather than an
  icon font. **No emoji in the UI** — energy comes from colour.
- **Voice**: sentence case, plain and confident, verb-first actions.

The launcher icon is the Agnes squid mark as an adaptive icon (gradient foreground, ink background, and
a flat monochrome layer for themed icons).

---

## Traps, so nobody rediscovers them

**The app theme must derive from `Theme.AppCompat`.** Avalonia's `AvaloniaActivity` extends AndroidX's
`AppCompatActivity`, which asserts this in `onCreate` and throws *"You need to use a Theme.AppCompat
theme (or descendant) with this activity"* otherwise. A platform theme (`Theme.Material.NoActionBar`)
looks equivalent, builds fine, and **crashes every launch on every device**. This one shipped once; see
`Resources/values/styles.xml`.

**Use `IActivityApplicationLifetime.MainViewFactory`, not `ISingleViewApplicationLifetime.MainView`.**
Android recreates the activity independently of the application object; Avalonia logs
"not fully supported on Android" and leaves a stale view behind.

**Reflection bindings can't resolve custom types.** `{Binding $parent[v:MyView]...}` and attached
properties like `(v:SafeArea.Top)` throw at runtime — only built-in types (`UserControl`,
`ItemsControl`, `ScrollViewer`) resolve. That's why safe-area insets are a `SafeSpacer` control rather
than an attached property, and why the session screen's back button is a command on its own view model.

**A control that binds its own `DataContext` must not then assign to it.** The sheet host binds
`Sheet` from the shell's `CurrentSheet`; setting `DataContext = sheet` on itself made that binding
re-resolve against the sheet, yield null, and close the sheet the instant it opened.

**A full-screen overlay must start input-transparent.** The sheet layer spans the window; its
`IsHitTestVisible` is set per sheet in `Present()`, which only runs on a *change* — so the initial
state has to be set in the constructor, or it silently eats every tap on the app.

## Building it

Needs the `android` workload, a JDK 17+, and the Android SDK (API 36 platform + build-tools):

```bash
dotnet workload install android
dotnet publish src/Agnes.App.Mobile/Agnes.App.Mobile.csproj -c Release -f net10.0-android
# or, packaged into builds/android/Agnes.apk:
./build.sh android
```

The APK carries `arm64-v8a` and `x86_64`, so it installs on a phone or an emulator. It is **not**
trimmed or AOT-compiled — bindings and the wire contract are resolved reflectively, and trimming is a
prerequisite for AOT — which is most of why it's large.

## Verifying it without a device

`tools/Agnes.MobilePreview` compiles the same views, view models and theme files against desktop
Avalonia and renders them offscreen with Skia at phone dimensions:

```bash
dotnet run --project tools/Agnes.MobilePreview -- screenshots/mobile
```

This exists because the mobile head can otherwise only be exercised on a device, where a missing
resource, an unresolvable binding or a font that fails to load are all silent at build time and fatal at
run time. It drives the simulated host through the real event pipeline, so the captures are what the app
actually does. It's part of `Agnes.Core.slnf`, so CI compiles the phone UI on every run, and
`tests/Agnes.Mobile.Tests` covers the shell's navigation, the session list and the card projections
against it.

The harness also accepts synthetic input (`window.MouseDown(...)`), which is how hit-testing is checked
without a device — useful, because a software-only emulator (no KVM, `-accel off`) does not deliver
touch to an Avalonia surface at all: a bare one-button Avalonia app gets nothing there either. Rendering,
layout, navigation and lifecycle are all verifiable on such an emulator; **touch is not**, and has to be
confirmed on real hardware.
