# Sandbox live testing

How the Incus sandbox was validated end-to-end against a real host, and how to
reproduce it. Unit tests cover command construction and credential handling
offline; this doc covers the **live** path (a real VM running a real `claude`).

## Host prerequisites

The developer host must have Incus running and the invoking user in the
`incus-admin` group (talks to `/var/lib/incus/unix.socket`). If the group was
added after login, pick it up per-command with `sg`:

```bash
sg incus-admin -c 'incus project list'
```

This host's Incus layout (shared with the CodeyBox project):

| Resource      | Value            |
|---------------|------------------|
| Project       | `default`        |
| Storage pool  | `codeybox-zfs`   |
| Network bridge | `cb-net` (10.99.5.1/24, NAT to the internet) |

These are the recorder/`IncusOptions` defaults used below. On a fresh host,
create a storage pool + a NAT bridge and pass `--pool`/`--bridge`/`--project`.

## The baseline image

The stock `images:ubuntu/24.04/cloud` image has **no `claude` binary** (only
python3). The agent binary must exist on the guest `PATH`. We bake a baseline
image by copying the host's self-contained `claude` ELF into `/usr/local/bin`
(the run-wrapper's scrubbed `PATH` includes it), then publishing:

```bash
sg incus-admin -c '
  incus init images:ubuntu/24.04/cloud agnes-probe --vm --no-profiles \
    --storage codeybox-zfs --config limits.cpu=2 --config limits.memory=4GiB \
    --device root,size=16GiB
  incus config device add agnes-probe eth0 nic nictype=bridged parent=cb-net name=eth0
  incus start agnes-probe
  # ...wait for the guest agent...
  incus file push "$(readlink -f "$(which claude)")" agnes-probe/usr/local/bin/claude --mode=0755 --uid=0 --gid=0
  incus exec agnes-probe -- /usr/local/bin/claude --version   # sanity
  incus exec agnes-probe -- cloud-init clean --logs           # so per-launch cloud-init re-runs on clones
  incus stop agnes-probe
  incus publish agnes-probe --alias agnes-claude-baseline
  incus delete agnes-probe --force
'
```

The host `claude` is a static-ish glibc x86-64 binary, so it runs unmodified in
the noble guest and stays version-matched to the operator's CLI.

## Recording a live session

`tools/Agnes.Record` gained a sandbox path. Run it through `sg` so the host-side
`incus` launcher can reach the daemon:

```bash
sg incus-admin -c 'dotnet run --project tools/Agnes.Record -c Debug -- \
  --agent claude-native --sandbox incus \
  --cwd /tmp/agnes-sandbox-work \
  --out recordings/sandbox-claude-qa.json --name "Sandboxed Claude Q&A" \
  "What is 12 * 34? Reply with only the number."'
```

Flags: `--sandbox incus` provisions a VM (image `agnes-claude-baseline`),
materialises credentials, runs the agent inside, records events, then deletes
the VM (pass `--keep` to leave it for inspection). `--image/--project/--pool/--bridge`
override the defaults.

The recorder provisions → materialises credentials → launches `claude` inside →
records → tears down. By **default** the agent asks before each tool call and
the recorder auto-approves (see the permission protocol below); pass
`--skip-permissions` to opt into autonomous operation.

## Permissions: ask by default, skip is opt-in

Agnes is interactive, so the agent must ask the user before tool calls (our
approve/deny UX) — `--dangerously-skip-permissions` is an opt-in, never the
default. claude's headless mode supports this over its stdio **control
protocol**, discovered from the CLI binary and confirmed live:

- Launch with `--permission-prompt-tool stdio`. Before each tool call claude
  emits `{"type":"control_request","request_id":"…","request":{"subtype":
  "can_use_tool","tool_name":"…"}}`.
- The client answers on stdin: `{"type":"control_response","response":{"subtype":
  "success","request_id":"…","response":{"behavior":"allow"|"deny"}}}` (no
  `updatedInput` needed — verified).

`ClaudeCodeStreamMapper` maps `can_use_tool` → `PermissionRequestedEvent` (which
surfaces in the UI); `NativeAgentSession.RespondToPermissionAsync` writes the
`control_response`. The mode is chosen per session:
`AgentSessionOptions.SkipPermissions` / `OpenSessionRequest.SkipPermissions`
(default false) selects `--permission-prompt-tool stdio` vs.
`--dangerously-skip-permissions`.

## Captured replay samples

Committed under `recordings/`, usable as `RecordedHost` fixtures:

| File | Exercises |
|------|-----------|
| `sandbox-claude-qa.json`    | text turn (answer `408`) |
| `sandbox-claude-tools.json` | Read + Write tools; edited file written back to the host via the virtiofs `/work` mount |
| `sandbox-claude-bash.json`  | Bash execution — `uname -sr` → `Linux 6.8.0-134-generic` (the **guest** kernel, not the host's, proving isolation) |
| `sandbox-claude-multiturn.json` | two turns in one **persistent** in-VM process: "remember 17" → later recalls `17` |
| `sandbox-claude-permission.json` | **default (permissioned) mode**: claude asks before Write → approved → file written |

## Findings from live testing

1. **Host launcher working directory (fixed).** The adapters set the host
   process `WorkingDirectory` to the *guest* path (`/work`), which doesn't exist
   on the host, so `incus exec` failed to start. The guest cwd already travels
   in the wrapped argv (`incus exec --cwd /work`), so the host launcher now uses
   `Environment.CurrentDirectory`. (`AcpAgentAdapter`, `NativeStreamAdapter`.)

2. **Credential env token (fixed).** `claude` 2.1.214 does **not** honour a
   materialised `~/.claude/.credentials.json`; it authenticates from the
   `CLAUDE_CODE_OAUTH_TOKEN` environment variable. `ClaudeCredentialProvider` now
   sets that env var from the extracted access token (delivered via the
   root-owned tmpfs env file the run-wrapper injects) in addition to the
   sanitised file. The refresh token is still never shipped into the VM.

3. **Persistent multi-turn session (fixed — the `setsid` trap).** We want a real
   long-lived session: **one** `claude --print --input-format stream-json
   --output-format stream-json` process kept alive and fed successive turns over
   its stdin (not `--resume`, which is crash recovery and breaks cron/long-lived
   use). claude fully supports this — but the sandbox's run-wrapper wrapped it in
   `setsid` (inherited from CodeyBox, which runs one process *per turn* so never
   noticed). With a detached session, claude **exits after turn 1's `result`**
   when it next reads stdin, so turn 2 got nothing. Bisected live: same command
   through `incus exec` works *without* `setsid` and dies *with* it. incus-exec
   allocates no controlling tty here, so `setsid` added no isolation — removed it
   from `IncusGuest.RunWrapper`. Now one in-VM process handles many turns and
   remembers context across them (`sandbox-claude-multiturn.json`).

   Also: the native adapter's `DefaultArguments` needed `--print` — without it the
   CLI starts its interactive TUI and emits nothing on a pipe. Added.


## The graphical probe: all four layers at once

`tests/Agnes.Integration.Tests/LiveGraphicalDisplayProbe.cs` is the end-to-end test for the
display feature. It is inert — silently passing — unless Incus answers *and*
`AGNES_LIVE_GRAPHICAL=1`, so it lives in the normal test project and costs CI nothing:

```bash
AGNES_LIVE_GRAPHICAL=1 dotnet test tests/Agnes.Integration.Tests \
  --filter FullyQualifiedName~LiveGraphicalDisplayProbe --logger 'console;verbosity=detailed'
```

What it does, in order: bakes `agnes-graphical` if it is missing (minutes, progress logged, and it
is **left behind** — it is the tier every graphical session launches from); provisions **one**
sandbox with `GraphicalDisplay.Default` through the real `IncusSandboxProvider`; stands up an
in-process host with the real `DisplayBrokerRegistry` + `/display/{sessionId}` endpoint; connects
the real `DisplayChannelClient`; then asserts the Info frame's geometry, a decodable 1280×800 JPEG,
a person taking control, a right-click producing a new frame, and the `computer_*` MCP tools taking
a screenshot, typing into the guest's xterm (verified by reading the file back through
`incus exec`), and being locked out while a person holds control.

Two settings are deliberately not the daemon's defaults. It names its instance with
`IncusOptions.InstancePrefix = "agnes-probe-"`, so its VM can never be confused with a session VM
somebody is working in, and it deletes exactly that instance in a `finally`. And it allows eight
minutes for the guest to report ready rather than three: on a developer machine sharing a pool with
other VMs, a first boot legitimately took longer than the daemon's default, and a probe that gives
up early reports "the feature is broken" when the truth is "the laptop was busy".

`AGNES_LIVE_GRAPHICAL_OUT` sets where it writes the frames it captured (default
`$TMPDIR/agnes-live-display`): the first frame, the frame after the injected click, the agent's
screenshot, and the `computer_frames` contact sheet — worth looking at, since "a JPEG arrived" and
"the desktop is actually drawn" are different claims.

## Graphical sandboxes: gotchas found live

Full write-up in [`graphical-sandbox.md`](graphical-sandbox.md); these are the
things that cost time on *this* host and would cost it again.

1. **`raw.qemu` only takes while the VM is stopped.** `incus config set` on a
   running VM fails with `Key "raw.qemu" cannot be updated when VM is running`.
   The provider sets it between `init` and `start`, which is the only window.

2. **QEMU connects to the display bus as root, not as `incus`.** Incus launches
   it with `-run-with user=incus`, but privileges are dropped *after* display
   setup, so the D-Bus EXTERNAL auth carries uid 0 (the AppArmor denial even says
   `fsuid=0 ouid=1000`). A bus policy that allows only `incus` refuses it with a
   flat "The connection is closed". Allow root.

3. **A 0700 `$XDG_RUNTIME_DIR` is not reachable.** `/run/user/1000/...` gives
   `Could not connect: Permission denied` before AppArmor even gets a say. The
   socket has to sit somewhere the QEMU uid can traverse; access control belongs
   in the bus policy instead.

4. **AppArmor denies it twice, in two different subsystems.** A file rule
   (`/tmp/agnes-display/** rwk,`) gets past the kernel's `connect` check; you then
   hit `dbus-daemon`'s *own* AppArmor mediation on `Hello`, which needs
   `dbus (send, receive, bind) bus=session,`. Both go in `raw.apparmor`. Watch
   `journalctl -k | grep DENIED` — `dmesg` is restricted for non-root here.

5. **The guest's DRM node is `/dev/dri/card1`, not `card0`.** An `xorg.conf`
   pinning `Option "kmsdev" "/dev/dri/card0"` fails with `(EE) No devices
   detected` → `no screens found`. Leave `kmsdev` out; `modesetting` finds the
   virtio-gpu on its own.

6. **`incus config set <inst> <key> -` reads stdin** (with a deprecation warning
   about the two-argument form) — but the `key=value` form used elsewhere in the
   provider does *not*. Mixing them silently sets the literal string `-`.

7. **`pkill -f` matches the shell you typed it in.** Killing a bus daemon with
   `pkill -f 'dbus-daemon --config-file=/tmp/agnes-display/...'` kills the command
   itself (exit 144). Use `pgrep -f '...disp[l]ay...' | xargs -r kill`.

8. **The scratch instance** used for this was `agnes-display-spike`, created from
   `images:ubuntu/24.04/cloud` on `codeybox-zfs` / `cb-net` with 4 vCPU, 4 GiB RAM
   and a 20 GiB root, and deleted afterwards. It was never one of the live
   `agnes-*` session VMs.
