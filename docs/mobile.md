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

**Received files land where a phone is best.** An agent can send you a file — a screenshot, a report, a
build — and the phone is the primary place to receive one. It arrives as a card in the transcript (an
image shows itself inline, fetched only once the card is on screen), as a **Sent to you** row in the
Inbox across every session and host, and, when the app isn't foreground, on its own default-importance
notification channel that can be silenced separately from the one that says an agent is blocked. Tapping
any of those lands on the card. The card opens a sheet with three verbs in the order a phone uses them:
**Share** first and primary — the screenshot is two taps from the group chat, which on a laptop is a
download, a file manager and an upload — then **Save to Downloads**, then **Open**. Each is a real
platform mechanism, not a wrapper: Save goes through `MediaStore` (no storage permission at all from API
29, staged `IS_PENDING` so nothing indexes a half-written file), Share and Open stage a copy in the app
cache and hand out a `content://` URI from `AgnesFileProvider` — a `file://` URI in an Intent has thrown
`FileUriExposedException` since Android 7, and the read grant has to travel with the URI. See
`Services/AndroidReceivedFileHandler.cs` and `Resources/xml/file_paths.xml`, which between them expose
exactly one directory and nothing else.

**No terminal.** The desktop head embeds a VT terminal; a phone does not get one. A 40-column terminal
behind a soft keyboard is worse than useless, and the things you'd use it for are covered by the git
sheet and the tool timeline.

---

## The screen

A session launched with a **graphical sandbox** has a desktop the agent can see and drive
(`OpenSessionRequest.Graphical`, `docs/architecture.md`). The phone can watch it, and take the mouse.

The session page gains a **Screen** segment beside Conversation — the one place this head shows two
different things in the same slot — and **the composer stays pinned below both**. Watching an agent
click through something wrong and having to navigate away to say "stop" would be the worst possible
version of this feature.

### Touch is not a mouse

An absolute mapping puts the pointer under your fingertip, where you cannot see it, at a precision of
about 9 mm — which on a 1280-wide desktop is roughly thirty pixels of "somewhere near the thing you
meant". So the surface is a **trackpad**, not a touchscreen:

| gesture | what it does |
| --- | --- |
| one finger, drag | moves a drawn cursor, **relative** to where it already was |
| one finger, tap | clicks *at the cursor* — not where you tapped |
| one finger, hold still | right-click at the cursor |
| two fingers, drag | scrolls at the cursor |
| pinch | zooms the view (1×–6×); sends nothing to the guest |
| one finger, drag, *while not driving* | pans the view |

Movement is 1:1 in guest pixels with no acceleration. Predictability beats reach — the answer to "that
target is too small" is the pinch zoom, not a curve nobody can learn. Zoomed in, the view follows the
cursor rather than letting it walk off the edge.

The guest is **never resized**: 1280×800 stays 1280×800 and the phone is a window onto it, scaled to
fit and letterboxed. Nothing at all is sent unless you hold control, so watching can never nudge the
agent's mouse. The rules live in `Controls/DisplayTrackpad.cs` (a pure state machine over positions and
timestamps, unit-tested) and the fit in `Controls/DisplayFit.cs`.

### The keyboard

Android hands an app *finished text*, not keystrokes — what you physically pressed to produce it (a long
press, a swipe, a suggestion) is not recoverable. So the Keyboard chip focuses a one-pixel invisible
`TextBox` to raise the IME, and each character that comes out is mapped to an X keysym name
(`Controls/DisplayKeysyms.cs`) and sent as a press and a release. A character with no keysym — an
accented letter, CJK, an emoji — is **dropped rather than guessed at**, because a wrong keysym types a
wrong character silently. Escape, Tab and the arrows get chips, since a soft keyboard has no room for
them. **Modifier chords (ctrl+letter and friends) are deliberately absent**: they need a sticky-modifier
model of their own, and half of one is worse than none. Drive them from the desktop or web client.

### What we ask the host to send

Two tiers, because there are two situations and no useful middle:

| | max width | fps | JPEG quality |
| --- | --- | --- | --- |
| unmetered | the panel's own pixel width, capped at 960 | 15 | 65 |
| metered | 640 | 8 | 55 |

Asking for more pixels than the panel has is bytes nobody can see. The metered tier is roughly a third of
the data for a picture that still answers "what is it doing". "Metered" is Android's answer
(`ConnectivityManager.IsActiveNetworkMetered` — a tethered Wi-Fi hotspot counts, and looks like ordinary
Wi-Fi from the app's side), injected into the shell rather than reached for; the person can turn the
behaviour off in **Appearance → Lower screen quality on mobile data**, which is on by default.

### Lifecycle

Connect on **entering the segment**; disconnect on **leaving the page**, not on switching back to the
conversation. That is what lets the transcript carry a live thumbnail (~120 px, Full frames only, at most
one repaint a second) with a driver chip over it — a graphical session is the one case where the
transcript alone lies about what is happening, because the tool calls say "moved the mouse", not what
appeared. The sessions list marks such a session with a small display glyph.

The screen is drawn into a `RenderTargetBitmap` at the guest's size: a Full frame replaces it, a Tile is
the decoded rectangle drawn into its own rect. That is a drawing-context call rather than a hand-rolled
`WriteableBitmap` blit because the blit can get the pixel format or the stride wrong and fails as a
smear rather than an exception.

### The web head, for comparison

`src/Agnes.App` (Uno, browser) gets the same view model and a **Screen** toggle beside the transcript,
but none of the trackpad: a browser has a real mouse and a real keyboard, so pointer positions map
straight to guest pixels and `VirtualKey` maps to keysyms (`Controls/DisplayKeys.cs` — note `Back` →
`BackSpace` and `Menu` → `alt`, both Win32 names that mean nothing to a guest). Two browser constraints
shape it: `BitmapDecoder` does not exist on WebAssembly, so frames go through
`BitmapImage.SetSourceAsync` over an in-memory stream — the browser's own image decoder; and there is no
pixel access to composite a tile into, so tiles are drawn as positioned `Image`s on a `Canvas` over the
last Full frame and cleared when the next one lands. The `Canvas` is laid out in *guest* pixels inside a
`Viewbox`, which means a pointer position read relative to it already **is** a guest pixel — no fit maths
to get wrong.

The web head also can't reach a host authenticated by a **pinned self-signed certificate**: the browser
terminates TLS itself, so the pin is unusable there (the same reason the hub doesn't connect to one from
a tab). The pane's empty state says so, because "couldn't connect" otherwise invites an hour of reading
the host's logs.

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
  icon font. **No emoji in the UI** — energy comes from colour. This head's *own* chrome is drawn from
  `Themes/Icons.axaml`, which matches the web design kit glyph-for-glyph; `FluentIcons.Avalonia` is also
  referenced, for the icons this head doesn't own — a `Symbol` named by a shared view model or
  contributed by a plugin, which has to render on a phone too. Reach for a Lucide glyph first, and add
  one to `Icons.axaml` if it's missing rather than mixing sets in this head's own screens.
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

**`Avalonia.Input.Gestures` is internal in Avalonia 12.** `AddHandler(Gestures.PinchEvent, …)` no
longer compiles; the pinch and pinch-ended events are reached as `InputElement.Pinch` /
`InputElement.PinchEnded` instead. Same events, different door.

**Only one `HeadlessUnitTestSession` may exist per process.** Starting a second one — even of the same
app type — throws *"a URI scheme name 'avares' already has a registered custom parser"* and takes an
unrelated test down with it. Every rendering test class therefore joins the `AvaloniaCollection` xunit
collection and shares one session (`tests/Agnes.Mobile.Tests/AvaloniaSession.cs`). It draws with **Skia**,
not the null backend, because the display surface's job is to decode a JPEG and composite it.

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

The graphical sandbox is verified there too. The simulated host has no display, so the harness supplies
its own — `tools/Agnes.MobilePreview/FakeDisplayHost.cs` is an `IAgnesHost` that can do exactly one thing
(open a display channel) plus a synthetic desktop drawn and JPEG-encoded on the spot. That is enough to
shoot `13-screen-agent-driving`, `13b-screen-you-driving` (a tile blitted over the picture, the cursor
drawn, the chip amber) and `13c-screen-thumbnail`, and enough for `DisplayRenderTests` to assert the
thing that actually matters: **input goes nowhere until control is taken**.

The harness also accepts synthetic input (`window.MouseDown(...)`), which is how hit-testing is checked
without a device — useful, because a software-only emulator (no KVM, `-accel off`) does not deliver
touch to an Avalonia surface at all: a bare one-button Avalonia app gets nothing there either. Rendering,
layout, navigation and lifecycle are all verifiable on such an emulator; **touch is not**, and has to be
confirmed on real hardware.
