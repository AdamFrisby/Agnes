# The display channel

A graphical sandbox has a screen. This is how that screen reaches people and models, and how their
input reaches back — the host half of the feature. How the picture is *captured* from a VM is
`docs/graphical-sandbox.md`; everything here sits above that seam and is guest-agnostic.

Off by default. Nothing in this document happens on a host that has not set
`Agnes:Security:AllowGraphicalSandboxes`.

---

## Shape

```
guest VM ── IDisplaySource ── one IDisplaySession (capture out, input in)
                                     │
                              DisplayBroker (one per session)
                                ├─ DisplaySurface   the host's BGRA copy of the screen
                                ├─ InputArbiter     who is allowed to drive, and how fast
                                ├─ subscribers      one mailbox per watching client
                                └─ JPEG encoding    SkiaSharp, on the host
                                     │
     ┌───────────────────────────────┴────────────────────────────┐
 WebSocket  /display/{sessionId}                          MCP  computer_*
 (a person watching and driving)                          (the agent looking and driving)
```

One broker per session, and it owns the **only** capture connection. Every consumer reads the same
surface, so the agent's screenshot and the person's video cannot disagree about what is on screen.

The broker is created on the **first** consumer and torn down when the **last** one leaves and no
agent turn is running — a capture connection to a VM nobody is watching is pure cost. A sweep every
15 seconds collapses brokers idle for 30 seconds; the 30-second grace is there so a burst of tool
calls between two turns doesn't get the capture torn down underneath it.

---

## The wire

`DisplayWire` in `Agnes.Protocol` owns the contract. Host → client is **binary**: a fixed 28-byte
`DisplayFrameHeader` followed by a payload. `Full`/`Tile` carry JPEG; `Info`/`Control` carry small
UTF-8 JSON. Client → host is **text**: one `DisplayClientMessage` per message, discriminated by `t`.

The first message after open is always `Info` — a client cannot map a click to a guest coordinate,
or draw the right affordance for who is driving, until it has one.

### Why not the hub

Frames are **views**, the event log is **facts**. The SignalR hub is one ordered broadcast per
session with a default message cap and base64'd byte arrays; a slow phone on it would apply
back-pressure to the transcript everybody else is reading. A separate socket means a dropped frame
is a dropped frame and nothing else.

A person's pointer and keystrokes are **never** written to the log. They are routinely the credential
the person took control in order to type. The only trace their use of the display leaves is the
`DisplayControlChangedEvent` handover.

### Coalescing: Tile or Full

Every subscriber holds a **single-slot mailbox**: the union of the damage that has accumulated since
the last frame *it* sent, plus any control notices it still owes. When the sender wakes it takes the
slot atomically and encodes one frame.

- Damage covering **less than 40 %** of the surface (`Agnes:Display:FullFrameThresholdPercent`)
  becomes one **Tile** at its own position.
- **40 % or more** becomes one **Full** frame. Past roughly that point a tile stops paying for
  itself, and a Full frame doubles as a resync for a client that has been dropping.
- **No update from the guest, no frame.** A still screen costs one `Info` frame for the life of the
  connection.

A slow socket **drops rather than queues**. A queue of stale frames is a memory leak with a latency
problem attached: by the time the tenth is written it describes a screen that stopped existing
seconds ago. Unioning instead means a subscriber that fell behind sends *one* frame covering
everything it missed, with no branch anywhere that has to know it fell behind.

### Scaling

A subscriber states `MaxWidth`, `MaxFps` and `JpegQuality` in a `quality` message. The frame's
pixels are scaled to that width; a tile is scaled by the **same factor as the whole screen**, not
fitted to its own width, or a 100-pixel tile would arrive magnified and land in the wrong place.

The header's `DisplayWidth`/`DisplayHeight` are **always the guest geometry**, whatever the pixels
were scaled to. That is what a client maps clicks back through. `MaxFps` is clamped to
`Agnes:Display:MaxFps`; payloads are hard-capped at `DisplayWire.MaxPayloadBytes` (8 MB) and a frame
over it is dropped with a log line rather than sent.

---

## Auth and authorization

Endpoint: `wss://host/display/{sessionId}?access_token=<device token>` — the host's **main TLS
listener** only.

1. **Authentication.** The device token in the `access_token` query, read exactly as the hub's
   negotiate gate reads it, rejected with a plain HTTP **401** in middleware *before* the WebSocket
   upgrade. A **public link is deliberately not accepted**: it grants a read-only transcript, never a
   live screen of a machine somebody is working on.
2. **Authorization to watch.** The same per-session decision the hub makes for `Subscribe`, asked
   through `Sharing.SessionAccessDecider` — the one implementation both front doors call, so a change
   to sharing reaches the screen and the transcript in the same commit. Refused with **403**, again
   before the upgrade.
3. **A session with no display** is **404**.
4. **Authorization to drive.** Any input or `control` message needs the `Prompt` decision. It is
   cached for **5 seconds** on an open channel: re-asking the share store for every pointer move
   would put an access lookup on the hottest path in the system, while never re-asking would let a
   revoked collaborator keep driving until they disconnected. A view-only client's input is
   **dropped, not fatal** — a stray move on a drag should not disconnect a watcher.
5. **Malformed or oversize** client message (over 8 KB) closes the socket with a policy-violation code.

The plaintext MCP listeners (the sandbox-bridge and loopback ones) 404 every path but `/mcp-agnes`,
so the display path is unreachable on them by construction. That gate is asserted by
`GuestMcpEndpoint.IsAllowedPath` and its tests; do not widen it.

---

## The arbiter: who is driving

Two hands on one mouse is a correctness problem, not a UI one — the agent's next screenshot would
show the consequences of somebody else's click and it would reason from it as its own. So control is
**held**, exactly one holder at a time (`None` / `Agent` / `User`), and every handover is appended to
the session log as `DisplayControlChangedEvent` **and** pushed to every watcher as a `Control` frame.

| Rule | |
| --- | --- |
| Agent takes control | Implicitly, on its first input of a turn — but only from `None` or itself. It never takes it from a person. |
| A person takes control | `{"t":"control","take":true}` from an authorized device. Wins **immediately**, even mid-turn. |
| While a person holds it | Every agent input tool throws `InvalidOperationException("The user has taken control of the display; ask before continuing.")`, surfaced as the tool's error. **Looking is still allowed**: a locked-out agent must be able to see why. |
| Release | Explicit hand-back (only by the holder), the device's channel closing, or **inactivity** after `Agnes:Display:ControlIdleSeconds` (60 s). The timeout exists because "person walks away" must not mean "agent locked out forever". |

Every injection — the agent's and a person's — dispatches `BeforeDisplayInputEvent` on the event
spine first. A veto is a **tool error** for the agent and a silent drop for a person (there is
nowhere to show an exception on a hot input channel; the client already learns state from `Control`
frames). The motivating interceptor: refuse `type` while a password field has focus.

### Budget and blocked chords

| Limit | Default | Applies to |
| --- | --- | --- |
| Input events per rolling minute | 240 | The session — agent and people together. The resource is the guest's input queue, and it does not care who filled it. |
| Input events per tool call | 32 | The agent, **except `computer_type`**. Checked **before** anything is injected, so a refused call injects nothing rather than half a keystroke leaving a modifier stuck down. |
| `computer_type` bytes | 4096 | The agent. Typing is the exception to the per-call ceiling and is budgeted **per keystroke**, not per key event. |
| Blocked chords | `super`, `super+*`, `meta`, `meta+*`, `ctrl+alt+*`, `alt+F2`, `ctrl+shift+i` | **The agent only.** |

**Why typing is charged differently.** A character expands to two key events, four when it needs shift.
Charged and capped like a chord, `computer_type` would refuse anything past sixteen characters — shorter
than a URL, a filename or any shell command worth typing — and its own 4096-byte ceiling would be
unreachable by a factor of 256. So the per-call ceiling does not apply to it (the byte ceiling is that
tool's limit, checked before the text is expanded) and the rolling budget counts **keystrokes**, which is
the unit of intent "input events per minute" is trying to bound. This was found by typing a shell command
at a real guest; every unit test until then typed two characters.

Chord matching is on a normalized form: modifiers lower-cased and canonically ordered, so
`alt+ctrl+Delete` and `ctrl+alt+Delete` cannot be two different answers to the same question. A
pattern ending `+*` matches any key held with exactly those modifiers.

The list is agent-only on purpose. A person driving the display is already authorized to do anything
the guest allows, and blocking their window manager would simply be broken. What it stops is an agent
leaving the application it is working in — dropping to a TTY, opening a run dialog, bringing up
devtools.

---

## The `computer_*` tools

Exposed on Agnes's own MCP server alongside `send_user_file`, authorized the same way: a session
token can only ever drive **its own** session's screen, whatever `sessionId` it passes. On a session
with no display every one of them throws `This session has no display.`

One tool per action, named and shaped the way the 2026 computer-use toolsets are, because that shape
is what the models were trained against.

| Tool | Signature |
| --- | --- |
| `computer_screenshot` | `(maxWidth?, sessionId?)` → image + `image WxH; display 1280x800` |
| `computer_frames` | `(count=5, spanMs=500, maxWidth?, sessionId?)` → **one** contact-sheet image + the grid and timing |
| `computer_click` | `(x, y, button="left", count=1, modifiers?, sessionId?)` |
| `computer_move` | `(x, y, sessionId?)` |
| `computer_drag` | `(fromX, fromY, toX, toY, button="left", sessionId?)` |
| `computer_key` | `(keys, count=1, sessionId?)` — xdotool chord syntax: `Return`, `ctrl+shift+t`, `alt+F4` |
| `computer_type` | `(text, sessionId?)` |
| `computer_scroll` | `(x, y, direction="down", amount=3, sessionId?)` |
| `computer_hold_key` | `(key, downMs=500, sessionId?)` |
| `computer_wait` | `(ms=500, sessionId?)` — capped at `Agnes:Display:MaxWaitMs` |
| `computer_cursor_position` | `(sessionId?)` |

**Coordinates are always guest pixels.** When `maxWidth` scaled a screenshot, the text block states
both sizes and tells the model to keep using display coordinates: clicks are **not** rescaled.
Scaling the picture must never silently move the mouse.

`computer_frames` is the tool that lets a model judge *motion* — did the spinner stop, did the dialog
land, did the click register. One image of the sequence is far easier to reason about than several
separate attachments, and costs a fraction of the tokens.

`computer_type` is **ASCII only**. A keysym press reaches the guest through its own keyboard layout,
and only the ASCII range is a stable layout-independent mapping; a keysym for `é` lands wherever the
guest's current layout happens to put it, which is usually nowhere. Non-ASCII is refused by name with
an instruction the model can act on ("write the text to a file in the working directory and open it
in the guest"), rather than typed into the void. Newline types `Return`, tab types `Tab`.

`computer_hold_key` holds the injection gate across the whole press-wait-release, so no other input
interleaves and the release is guaranteed even if the hold is cancelled — a key stuck down in the
guest is a failure nothing downstream would notice.

---

## Configuration

| Key | Default | Effect |
| --- | --- | --- |
| `Agnes:Security:AllowGraphicalSandboxes` | `false` | **The switch.** A session asking for a display is refused unless this is on. Implies a sandbox: the display lives at the VM boundary. |
| `Agnes:Display:ControlIdleSeconds` | `60` | How long a person may hold the display without touching it before control falls back to nobody. |
| `Agnes:Display:MaxFps` | `15` | Ceiling on a subscriber's requested frame rate. |
| `Agnes:Display:JpegQuality` | `75` | Default JPEG quality (1–100). |
| `Agnes:Display:FullFrameThresholdPercent` | `40` | Damage at or above this share of the surface is sent as a Full frame instead of a Tile. |
| `Agnes:Display:InputEventsPerMinute` | `240` | Per-session rolling budget across the agent and every person. |
| `Agnes:Display:InputEventsPerToolCall` | `32` | What one agent tool call may expand to. |
| `Agnes:Display:MaxTypeBytes` | `4096` | `computer_type` ceiling. |
| `Agnes:Display:MaxWaitMs` | `10000` | `computer_wait` ceiling. |
| `Agnes:Display:BlockedChords` | see above | Chords the **agent** may not press. |

A project may set `Defaults.Graphical` in `~/.agnes/projects.json` to open its sessions graphical by
default. It can only ever raise the floor: the operator guardrail is re-checked, so a project file
cannot turn on a capability the host has switched off. *(There is no wire DTO field for it yet, so it
is host-side configuration only until `ProjectDefaultsDto` grows one.)*

---

## Out of scope, deliberately

- **The relay.** The display channel is **not** carried over the relay transport in this pass. It
  lives on the main TLS listener only; a client reaching a host through a relay gets the transcript,
  not the screen.
- **A video tier.** Only JPEG frames. No H.264/VP9/AV1, no delta codec beyond the Tile/Full split.
- **GPU.** No hardware encode, no accelerated guest rendering.
- **Audio.** The guest's sound is neither captured nor injected.
- **Clipboard.** No copy/paste bridge between host and guest, in either direction.
- **Multiple displays.** One logical display per sandbox, fixed at launch — a guest's framebuffer
  cannot be added to a running VM.
