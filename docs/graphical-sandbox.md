# Graphical sandboxes

A sandbox VM can have a screen: a virtual GPU, an X session at a fixed size, and
a capture path that reads its framebuffer and injects keyboard and mouse events —
**with nothing inside the guest doing the capture**.

That last part is the whole design. The obvious way to give an agent eyes is to
run a VNC server or a capture agent in the guest. That is also the way that
fails: it is software the agent can misconfigure, kill, or be lied to by, running
*inside* the blast radius it exists to observe. So the screen is read one layer
down, from QEMU itself. The guest runs plain X and knows nothing about any of it.

## How it works

```
Agnes host ──── private dbus-daemon (one per instance, /tmp/agnes-display/<vm>/bus)
      │                 ▲                         ▲
      │  control        │ org.qemu.Display1       │
      │  (RegisterListener, Keyboard, Mouse)      │
      │                                     QEMU (Incus VM)
      └──── peer-to-peer socket ◄── pixels ──┘        │
              org.qemu.Display1.Listener              │  virtio-gpu
                Scanout / Update (ay)                 ▼
                                                 guest: Xorg + openbox
```

Two connections, because QEMU insists on two:

* **Control** — we are a *client* on the instance's private bus. `RegisterListener`
  hands QEMU one end of a `socketpair`; `org.qemu.Display1.Keyboard` and `.Mouse`
  take input straight into the emulated devices.
* **Pixels** — QEMU makes a *peer-to-peer* D-Bus connection back over that socket,
  acting as the authentication server, and calls `org.qemu.Display1.Listener`
  methods on us: `Scanout` when the surface is created or resized, `Update` for
  each damage rectangle.

`src/Agnes.Sandbox/Display.cs` is the seam every consumer sees, and it names no
Incus or D-Bus type: a `GraphicalDisplay` size on `SandboxSpec`, an
`IDisplaySource` capability on a sandbox that has one, an `IDisplaySession` with a
channel of `DisplayUpdate`s, `SnapshotAsync`, and `InjectAsync`.
`src/Agnes.Sandbox/CapturedSurface.cs` holds the framebuffer and the rules for
composing rectangles, backend-free and unit-tested — distinct from the host's own
`Agnes.Host.Display.DisplaySurface` one layer up, which exists to be encoded and
fanned out; this one exists because `SnapshotAsync` has to be answerable from
inside a backend, and a backend cannot reach into the host.
`src/Agnes.Sandbox.Incus/Graphical/` is the QEMU implementation.

**This document stops at the seam.** Everything above it — the host broker, the
wire channel, the input arbiter, the `computer_*` MCP tools and the client
panels — is [`display-channel.md`](display-channel.md). The division is the point:
a Windows guest, or a different hypervisor, is a new `IDisplaySource` and no change
anywhere else.

Input is spoken in **X keysym names** — `Return`, `ctrl`, `a`, `KP_0`, the same
vocabulary `xdotool key` and Anthropic's computer-use tool take. Translating one
to a QEMU key number is `QemuKeyMap`'s problem, not a caller's.

## The mechanism, exactly

Everything below was established against Incus 7.0.1 with its bundled QEMU
11.0.3, and every line of it was found by hitting the failure first.

**`raw.qemu`** — the display is attached with one instance config key, applied at
create time (Incus refuses to change `raw.qemu` while the VM runs):

```
raw.qemu=-display dbus,addr=unix:path=/tmp/agnes-display/<instance>/bus
```

`p2p=on` is *not* usable: in peer-to-peer mode QEMU waits to be handed a socket
through the QMP `add_client` command, and Incus keeps the QMP socket in a
root-only directory the Agnes host cannot reach. Hence bus mode, and hence a bus.

**The bus** — one `dbus-daemon` per instance, because QEMU claims the well-known
name `org.qemu` and two of them on one bus would fight over it. It is started
with `--fork` and deliberately *not* parented to the Agnes host: if it died with
the host, every running graphical sandbox would lose its capture permanently,
since QEMU connects once at boot and has no reconnect. Orphaned, it survives a
host restart and the next attach simply reconnects.

**Where the socket lives** — not `$XDG_RUNTIME_DIR`. QEMU connects to the bus
while still **root**: Incus's `-run-with user=incus` drops privileges *after*
display setup. A 0700 runtime directory is unreachable, so the socket sits under
`/tmp/agnes-display/<instance>/` at mode 0711 and access control moves into the
bus policy — a uid allowlist (`root`, `incus`, the host's own user; everyone else
denied at connect), which D-Bus enforces with EXTERNAL auth against `SO_PEERCRED`.
That is a real access-control boundary, not a directory mode.

**`raw.apparmor`** — Incus confines QEMU with a generated per-instance AppArmor
profile that knows nothing about our socket. It needs **two** rules, and the
second one is the trap:

```
/tmp/agnes-display/** rwk,
dbus (send, receive, bind) bus=session,
```

Without the first, the VM will not start: `failed to connect to DBus: Could not
connect: Permission denied`, with `apparmor="DENIED" operation="connect"` in the
kernel log. Without the second it gets *further* and then fails differently —
`GDBus.Error:org.freedesktop.DBus.Error.AccessDenied: An AppArmor policy prevents
this sender from sending this message ... member="Hello"` — because `dbus-daemon`
does its own AppArmor mediation, in userspace, separately from the kernel's file
check.

**A clone re-points its bus.** A copied VM inherits its source's `raw.qemu`,
which names another VM's socket; `CloneAsync` rewrites it before the clone ever
starts.

## Two things the D-Bus library made us do

`Tmds.DBus.Protocol` is the right dependency (already in the tree via
Avalonia.FreeDesktop, low-level, allocation-light) but it has no peer-to-peer
mode, and two consequences land in `ListenerHandshakeStream`:

1. **It sends `Hello`.** Even handed a ready-made stream, it sends `Hello` to
   `org.freedesktop.DBus` and waits for a unique name — on a connection where
   there is no bus and nothing will ever answer, so `ConnectAsync` hangs forever.
   The stream answers that one message itself with about forty bytes of
   hand-built `METHOD_RETURN`. Owning forty bytes beats owning a second
   implementation of the wire format.
2. **It won't take a method handler before connecting**, and QEMU makes its first
   call (a properties round trip on the listener object) *during* the handshake.
   Registering afterwards is therefore always too late — the call arrives with no
   handler, gets "unknown method", and QEMU ends up with a listener that never
   receives pixels. So the stream holds QEMU's messages until the session says
   `Release()`. After that the gate is open for good and the pixel path pays
   nothing for it.

One more QEMU-side requirement: the listener object must expose an `Interfaces`
property (an `as`), and it must be **present**. QEMU reads it to decide whether to
send pixels through shared memory or a dmabuf, and passes the value to
`g_strv_contains()`, which does not tolerate a missing one. We answer with an
empty array, which is both true and what keeps us on the simple path where pixels
arrive inline.

## The guest recipe

Rendered by `src/Agnes.Sandbox.Incus/Graphical/GraphicalGuest.cs` into the
session's cloud-init, so the geometry is per-sandbox while the packages come from
the baked image:

* `/etc/agnes-display-geometry` — `<width> <height>`.
* `/etc/X11/xorg.conf.d/10-agnes-virtual.conf` — `modesetting` on the virtio-gpu,
  with the requested size as `PreferredMode`, `Modes` and `Virtual`.
  **Do not pin `Option "kmsdev"`**: the DRM node in an Incus VM is `card1`, not
  `card0`, and pinning `card0` fails with `(EE) No devices detected` /
  `no screens found`.
* `/usr/local/bin/agnes-session` — waits for X, forces the mode (adding a `cvt`
  modeline if the driver did not offer one, since a headless virtio-gpu advertises
  no EDID), paints a background, runs `openbox` and one `xterm`.
* `agnes-x.service` — `Xorg :0 vt7 -nolisten tcp -noreset`, as root, restart
  always. Its `setpgid`/`setsid`/`xf86EnableIO` warnings under systemd are normal
  and harmless.
* `agnes-desktop.service` — the session script as the agent user.

The apt packages are the graphical **image tier**: `SandboxImageManifest.AsGraphical()`
publishes the baseline plus `xserver-xorg-core`, `x11-xserver-utils`, `xinit`,
`openbox`, `xterm`, `fonts-dejavu-core`, `dbus-x11`, `x11-utils` under the alias
`agnes-graphical`. A host that never asks for a display never bakes or pays for
it. Sandboxes with a display also get a **24 GiB** root volume rather than 16
(`IncusOptions.GraphicalResourceOverride`), because Incus refuses at *launch* time
to put an image into a smaller volume.

## Measured

From `tools/Agnes.DisplaySpike` against a live 4-vCPU Incus VM at 1280x800
(Incus 7.0.1, QEMU 11.0.3, virtio-gpu, ZFS pool), across four runs. Everything here
is measured, not estimated; the one thing that is *not* measurable is noted below.
Ranges are the spread across runs on an otherwise busy developer machine.

| | |
|---|---|
| Session open (connect → first frame) | **75–85 ms** |
| Idle update rate (desktop with a blinking cursor) | **0.3 updates/s** |
| Peak update rate (terminal flooded with `yes`) | **320–470 updates/s** |
| Bytes at peak | **1.2–1.8 GiB/s** |
| Mean update size | **4000 KiB** — every rectangle is the whole surface |
| Handler cost (message arrival → published update) | **mean 0.6 ms, p95 1.2 ms, max 6.5 ms** |
| Queue cost (published → consumer dequeue) | **mean 0.1–0.4 ms, max 18–30 ms** |
| Key press → first resulting update | **mean 9.1–9.7 ms, p95 10.3–12.5 ms** |
| Right-click → first resulting update | **6–12 ms** |

The headline finding is the one that shapes anything built on top: **QEMU's
virtio-gpu path never narrows the damage rectangle.** In a 10-second flood, 2940
of 2940 updates covered all 1,024,000 pixels. The rectangle API is still the right
contract — other backends do narrow, and the fake exercises partial updates on
purpose — but a remoting layer must do its own diffing, and must expect ~4 MiB per
update at up to a few hundred a second under a pathological load. Idle costs
essentially nothing, which is the case that actually matters.

The handler cost was two `memcpy`s of the frame — one out of the D-Bus message by
the library, one into the framebuffer, plus a third to publish it. Letting the
surface *adopt* an already-packed incoming buffer instead of copying it took the
mean from 1.25 ms to 0.6 ms: at a few hundred frames a second that is the
difference between 40% and 20% of a core.

The key-to-pixel figure is genuine end-to-end latency: injected key → guest →
xterm → X → virtio-gpu → QEMU → our buffer. It does not include a client, an
encoder or a network. What is *not* measured is QEMU's own send time, because
QEMU does not timestamp the call; "handler cost" starts when the message reaches
us.

Correctness was checked by driving the guest, not by looking at pixels: the spike
types `echo AGNES123 > /tmp/kt.txt` into the xterm and reads the file back through
`incus exec`; then `echo XY`, `Left Left`, `Z`, `End` to prove arrows and `End`
(the file contains `ZXY`); then `Ctrl+C` followed by another command, to prove a
modifier reaches the tty.

## Limits

* **No GPU, no virgl.** The bundled `/opt/incus/bin/qemu-system-x86_64` has
  display backends `none`, `spice-app`, `dbus` and devices `virtio-vga`,
  `virtio-gpu-pci`, `qxl-vga` — but **no** `virtio-vga-gl`, so there is no
  OpenGL passthrough and no dmabuf path. Everything is software rendering in the
  guest. Good enough for a browser and a desktop; not for anything 3D.
* **No `-vnc`** in that QEMU either, which is why this is a D-Bus display and not
  a VNC client.
* **Audio is out of scope.** `org.qemu.Display1.Audio` exists; nothing uses it.
* **No resize.** The size is fixed when the sandbox is created and applied once at
  guest start. A mid-session resize would need a guest mode-set, a re-layout in
  every client, and would invalidate coordinates an agent has already reasoned
  about.
* **The bus daemon must outlive nothing but the VM.** If it is killed while the VM
  runs, QEMU's display connection is gone and cannot be re-established without
  restarting the VM. Opening a session ensures a bus exists but cannot resurrect
  that connection.
* **One console.** `Console_0` only; multi-head is not modelled.

## Configuration

All under `Agnes:Sandbox:Incus:*` (`IncusOptions`):

| Key | Default | What it is |
|---|---|---|
| `GraphicalImage` | `agnes-graphical` | Image alias a sandbox with a display launches from |
| `GraphicalResourceOverride:DiskBytes` | 24 GiB | Root volume floor for graphical sandboxes |
| `DisplayRuntimeDirectory` | `/tmp/agnes-display` | Where per-instance bus sockets live; must be traversable by the uid QEMU runs as |
| `DisplayBusQemuUser` | `incus` | The unix user Incus's QEMU runs as, allowed on the bus |
| `DbusDaemonPath` | `dbus-daemon` | The bus daemon binary |
| `DisplayReadyTimeout` | 30 s | How long to wait for QEMU's first scanout |

## Testing without a VM

`tests/Agnes.TestKit/Display/ScriptedDisplaySource.cs` is an `IDisplaySource`
that renders a synthetic 1280x800 login page, records every injected input, and
*responds* to it: click a field to focus it, type into it, press the button (or
`Return`) and the page changes to a signed-in state. It is a behaving fake rather
than a pixel generator on purpose — the thing usually under test is a loop (look,
decide, click, look again), and a fake that never changes in response to input
lets a completely broken loop pass. It also publishes **partial** updates, which
the real QEMU backend never does, so a consumer that silently assumes full frames
is caught.

`tools/Agnes.DisplaySpike` is the live driver that produced the numbers above. It
needs a real VM, so it is deliberately outside `Agnes.Core.slnf` and outside CI:

```bash
# 1. Start the instance's bus and print the two config values it needs.
dotnet run --project tools/Agnes.DisplaySpike -- prepare agnes-display-spike

# 2. Apply them (raw.qemu only takes while stopped) and start the VM.
incus stop agnes-display-spike
incus config set agnes-display-spike raw.qemu='-display dbus,addr=unix:path=/tmp/agnes-display/agnes-display-spike/bus'
incus config set agnes-display-spike raw.apparmor - < apparmor.txt
incus start agnes-display-spike

# 3. Attach, save a PNG a second, inject input, print the numbers.
dotnet run --project tools/Agnes.DisplaySpike -- measure agnes-display-spike /tmp/agnes-display-out
```
