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

A **fifth** appears on a device that watches a CodeyBox fleet, and only there — see
[CodeyBox: the fifth destination](#codeybox-the-fifth-destination).

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

**And letting one in is two different decisions.** The card's primary action is **Let in as member** —
a device that can open sessions and see its own. **Let in as owner** hands over the run of the host, so
it is a separate, differently-worded button below the pair, and it appears only when *this* phone is
itself an owner: a member's attempt would be refused host-side, and a button whose only outcome is a
refusal teaches nothing. Decline stays exactly as cheap as it was.

**A member is told it is one.** A device that can see none of the host's sessions looks exactly like a
broken host, which is how a newly-paired phone ended up staring at an empty list with no explanation.
So: pairing says what it granted before the screen moves on (a member gets a card — "Paired as member",
plus the one line on what that means — and taps Continue; an owner has nothing to learn and goes
straight through); an empty Sessions list on a host that called this phone a member replaces the usual
teaching line with "This device is a member on *host*. It sees the sessions it starts and any shared
with it. An owner can make it an owner in More › Devices", and offers the button that goes there. The
role is remembered against the saved host so the sentence is on screen before the round trip that
confirms it — but only once a host has actually said so. Null means "not asked", and an owner must
never be told, even for a moment, that it is a member.

**More › Devices is a management page, not a list.** Each row carries the device's role as a plain pill
(never a status tint — those already mean needs-you / running / failed), how it was admitted in words
("paired with code", "vouched for by a device", "authorized key", "GitHub"), and when it was last used,
which is the field you came here for when you meant to revoke something. An owner also gets **Make
owner** / **Make member** per row — disabled on the only owner left, because the host refuses that
demotion and offering it would be a button whose purpose is to fail — and a prune that names the number
before it acts (first tap arms and counts, second removes; a phone has no hover and no undo). A member
sees the same list read-only under "Only an owner can change roles.", because who else is on the host is
not a secret; changing it is a permission.

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

### The status line

An agent reports, rarely and in its own words, what it is doing — "found the leak in the tail cursor;
rewriting the resume path so a reconnect can't replay" — through the host's status tool
(`AgentStatusEvent`, surfaced on the wire as `SessionSummary.LatestStatus`). It is the only line in the
app that is the agent's own account of itself rather than a derivative of its output, which is why the
**Sessions list** is where it belongs: one card per session, and now each card says what its agent is
doing, without opening any of them. That is the thing a phone is genuinely better at.

The rules, all in `ViewModels/AgentStatusLine.cs`:

- **Two lines, wrapped, then ellipsis.** A status is a sentence, and a phone card that clips it after
  eight words shows the setup and eats the point. There is no tooltip on a phone and a long-press to
  reveal is a gesture nobody discovers, so the line simply gets the room it needs — no more.
- **No status, no row.** A card whose agent has never reported grows nothing. A blank row would read as
  "idle", which is a different and wrong claim.
- **The age carries the staleness.** "4m" while things are moving; once a *working* agent has been quiet
  for ten minutes it reads **"no update for 12 min"** instead — because "12m" beside a running session
  looks like progress, and the whole reason for the line is to say when there hasn't been any. An idle
  session is never stale: nothing is overdue there.
- **Both fields are persisted** on `SavedSession`, like the title, so the list is right the second it is
  opened cold rather than one round trip later.

The session screen carries the same line under its app bar, in its own row rather than a third line in
the fixed 56dp bar. Above the transcript it also gets a **"while you were away"** band: when the session
was unattended and the agent said something in the meantime, one slim band in the sky hue — *in motion*,
never amber, because nothing here is blocked on you — with the line and its age. It is captured on
arrival and taken down by the first scroll, tap or keystroke (or a tap on the band itself). Two details
that are easy to get wrong: opening the page is *itself* the end of being away, so the band snapshots
the state before marking the session attended; and dismissal listens for pointer and text gestures, not
`ScrollChanged`, because the page scrolls itself to the tail on arrival and would otherwise dismiss the
band in the frame it appeared.

There is deliberately **no notification** for a status. A status is not a page: it is what you read when
you chose to look.

**No terminal.** The desktop head embeds a VT terminal; a phone does not get one. A 40-column terminal
behind a soft keyboard is worse than useless, and the things you'd use it for are covered by the git
sheet and the tool timeline.

---

## CodeyBox: the fifth destination

CodeyBox is the operator's own multi-agent orchestrator — a queue of work items that agents pick up, audit
in a loop, and land. The desktop drives it through the `Agnes.Plugins.CodeyBox` client plugin. The phone
gets its own surface, because the one thing that screen is for at arm's length is the one thing a phone is
best at: **an item has parked on a question and is waiting for a person**.

**A fifth tab, and only for a device that has one.** The four destinations above are four *jobs*, and a
fleet of autonomous agents is a fifth: it is not a session, it has its own three-segment page stack and its
own item pages, and a row at the top of Sessions would put every visit two taps deep, pop back into a list
it has nothing to do with, and spend the top of the one screen whose whole value is being readable at a
glance. So it is a tab — present only once More › CodeyBox has both an address and a key. Until then there
is no tab, no inbox section and no request of any kind; the app is byte-for-byte the four-destination app
described above, which is what almost every device will have. Five equal targets across 411 dp is 82 dp
each, comfortably over the 48 dp floor, and the bottom bar is a `UniformGrid` so it re-divides rather than
pushing a tab off the edge.

**Setup is two fields, because a phone cannot read the config file.** The desktop plugin resolves CodeyBox
the way CodeyBox's own CLI does — the environment, then `~/.config/codeybox/config.json` — so a machine
already set up for `codeybox` needs no second configuration. A phone is not that machine: it reaches the
orchestrator across the LAN, by address, over plain `http`, and Android has no `~/.config` to read. So the
address and the key are typed once and kept beside the app's other device-local state (`JsonStore`), like a
paired host's token. **Test** hits `/queue/status` and says what came back, because the two ways this goes
wrong — an address that is not on this network, a key the orchestrator refuses — otherwise produce the same
blank screen. CodeyBox is not an Agnes host: it has no TLS listener and no certificate to pin, so it takes
its own `HttpClient` rather than `AgnesHttp.For(pin)` (see the note on `CodeyBoxClient`).

**Three segments, because a fleet is asked three questions.**

| Segment | Answers |
| --- | --- |
| **Overview** | Should I do anything? The generated sentence naming the current constraint, six vitals as a two-column grid with sparklines and control bands, the rows that need a look, the folded groups that need a slot rather than a look, quota burn-downs, and the cumulative flow last. |
| **Now working** | What is it doing this second? One card per busy dispatch slot — agent, phase, a ticking elapsed, and the last three lines the agent actually printed — plus the headline figures, the orchestrator's own feed, and a heartbeat of events per minute. |
| **Queue** | What is in the pipeline, in the order the orchestrator will pick it: Now, Next, one section per waiting reason, one per landed day. |

**The item is a page, not a pane.** Tapping any row pushes a full screen that leads with the **decision
card** when there is one — `Decision.For` is a pure function of the row and its open questions, the same one
the desktop card uses, handed this head's own commands. Its choices are full-width buttons with their
consequence written under each; the destructive one wears the danger hue and **arms before it fires**, the
same two-step More › Devices uses for its prune, because a phone has no hover and no undo. The evidence —
the question verbatim, the failure message in full — is never truncated, and "Show output" / "Show diff" /
"Show timeline" open sheets rather than copying a view's content into the card.

**It is also in the Inbox**, which is the whole point. Items waiting on a person appear there across the
whole fleet, *below* the Agnes approvals: an Agnes approval is an agent stopped mid-turn in a session you
started, and if only one can be above the fold it is the one costing a live turn. A question is answerable
from the row (a sheet that keeps the question above the reply box); a failure is not — deciding what to do
about one needs the evidence, so that row opens the card instead of offering a guess.

**It runs only while it is on screen.** The change feed (SSE) and the wall's timers start when the tab is
entered and stop when it is left, and **calm is on by default** — calm keeps every live update and removes
every flash, pulse and count-up, which on a 33 ms frame timer is the difference between a screen you can
leave open and one that warms the phone in your hand. The gather behind the Overview is a dozen reads plus
one audit history per live item, so the feed only marks it dirty and it re-gathers at most every four
seconds, with a one-minute floor so the clock-driven figures do not rot. The Inbox's read is separate and
deliberately cheap (one list plus one per parked item), because "something needs you" must not depend on
the fleet tab being open.

### What is shared with the desktop, and what is not

Shared, and not copied: `CodeyBoxClient`, `OverviewGather`, `OverviewModel`, `BoardModel`, `Decision`,
`NowWorkingViewModel`, `QuotaHistoryMap`, and the small drawn controls (`Sparkline`, `BurnDown`,
`QuotaGauge`, `TraceGlyph`, `MotionDot`, `StepDot`, `ChainStrip`, `FlowChart`, `Heartbeat`). None of them
know what a window is. The **gather** in particular was lifted out of the desktop's section view model into
`OverviewGather` precisely so the second head could not end up with a second copy of the rules that keep it
affordable — a trace refetched only when its item's `UpdatedAt` moved, a week of quota series re-read at
most once a minute.

Not shared: the plugin's `Views/*.axaml`. They are laid out for a 660–1200 px pane and they ask for the
desktop head's role names. The phone's screens are its own.

Two consequences worth knowing:

- **The drawn controls look their colours up by name.** `ThemedDrawing` cannot use `DynamicResource` — a
  control that renders itself has no property to bind — so it asks for `Fg`, `FgDim`, `FgFaint`, `Line`,
  `Panel`/`PanelAlt` and the `Status*` hues, which are the *desktop's* vocabulary. `Themes/DrawingRoles.axaml`
  aliases them onto this head's (`Text`, `TextDim`, `TextMuted`, `Border`, `Surface1`/`Surface2`, and
  `Info`/`Warning`/`Success`/`Danger`) rather than teaching eight controls about two naming schemes. Those
  aliases restate their colours, because a `ResourceDictionary` entry cannot be a reference to another
  entry — so `CodeyBoxRoleAliasTests` asserts every pair resolves to the same colour in both variants.
- **Sky, not mint, for in motion.** This head's session cards call a running session mint; the fleet's
  vocabulary is the desktop's — sky in motion, mint landed — and the motion dot beside a row is drawn by the
  plugin's own control. So the fleet's chips use the `info` pill, because a dot and a chip on the same row
  disagreeing about what a colour means is how the colours stop meaning anything.

**The one rule for a live orchestrator: reads only.** Everything the screens do on arrival is `GET`. The
mutations are wired to the exact endpoints the desktop uses — retry `POST /workitems/{id}/retry`,
raise-the-ceiling that retry *then* `PATCH /workitems/{id}` (the patch is refused on a terminal item and
`AuditFailed` is terminal), replay `POST …/replay`, promote `POST …/promote`, cancel `DELETE /workitems/{id}`,
answer `POST …/answer`, dismiss `POST …/dismiss-question` with the reason the orchestrator requires — and
they are tested against a recording handler, never against a running fleet.

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

**Pairing a typed address on a self-signed host learns the certificate first.** A scanned QR carries the
host's fingerprint; a typed `https://` address carries nothing, so the first request used to fail on trust
and the screen said "can't reach that address" about a host that was right there. `ConnectPageViewModel`
now reads the certificate the host presents (`HostFingerprint.ProbeAsync`), shows the SHA-256 grouped for
comparison with the host's log, probes and pairs through a client pinned to it, and saves that pin. A
host this device already pinned that presents a different key is refused outright.

**The typed pairing code closes when the host's first device pairs.** That is the host's rule (see
`security.md`); the phone learns it from `/auth/methods` (`AuthMethods.PairingCodeOpen`) and replaces the
code field with the two ways that still work — ask for approval from a paired device, or scan a QR from
one. A 401 from `/pair` carries the host's own sentence, and that is what the screen shows.

**Sessions attach tail-first.** Every card used to subscribe from sequence zero; one live session held
338,000 events (169 MB), and the tablet spent minutes downloading it — for every card, since the list
attached them all. A card now attaches only when opened, from the last `SessionsViewModel.TailWindow`
events (the head comes from `SessionSummary.HeadSequence`, kept on `SavedSession`), and the page offers
"Load everything" (`IAgnesHost.LoadHistoryAsync` → `SessionView.Prepend`, then a rebuilt view model). A
card that was never opened shows what the host last said the session was doing, not "Reattaching".

**A page pushed before its subscription lands must be told.** `SessionPageViewModel.Adopt` existed and
nothing called it, so the page sat on "Reattaching…" until Retry. `SessionsViewModel.AttachAsync` now
adopts the session into any open page for that entry.

## Iterating on a real device

With USB debugging on, `adb` is enough: publish the APK (`dotnet publish … -f net10.0-android`), `adb
install -r`, `adb exec-out screencap -p > shot.png`, `adb shell input tap X Y` / `input text` to drive,
`adb logcat --pid=$(adb shell pidof -s dev.agnes.app)` for the app's own log. To check the phone form
factor on a tablet, `adb shell wm size 720x1560; wm density 280` gives a 411×891 dp display (the app
reads dp, so an over-large density on a small pixel size makes everything giant); `wm size reset; wm
density reset` restores it. uiautomator dumps are empty for Avalonia — read screenshots instead.

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

The CodeyBox screens are captured there too (`14-fleet-*`). The fleet they draw is
`tools/Agnes.MobilePreview/FakeFleet.cs` — a work-item list run through the *real* `OverviewModel`,
`BoardModel` and `QuotaHistoryMap`, because the only CodeyBox on the machine is the operator's own and a
screenshot run must not touch it. `tests/Agnes.Mobile.Tests/CodeyBoxRenderTests` shoots the same screens
from the plugin's canned samples at 411×891 and at 800×1340, which is the tablet they were verified on.

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
