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
records → tears down. It uses the native adapter flags
`--print --output-format stream-json --input-format stream-json --verbose
--dangerously-skip-permissions` (the sandbox VM *is* the permission boundary).

## Captured replay samples

Committed under `recordings/`, usable as `RecordedHost` fixtures:

| File | Exercises |
|------|-----------|
| `sandbox-claude-qa.json`    | text turn (answer `408`) |
| `sandbox-claude-tools.json` | Read + Write tools; edited file written back to the host via the virtiofs `/work` mount |
| `sandbox-claude-bash.json`  | Bash execution — `uname -sr` → `Linux 6.8.0-134-generic` (the **guest** kernel, not the host's, proving isolation) |

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

3. **Multi-turn (known limitation, not fixed here).** The native adapter keeps
   one long-lived `claude --print` process and feeds turns reactively; `claude`
   exits after the first turn's `result`, so a second prompt yields no output.
   `claude`'s CLI is one-process-per-turn: multi-turn continuity is done with a
   fresh `claude --print --resume <session-id>` per turn (how CodeyBox does it).
   This is a native-adapter session-model issue independent of sandboxing; the
   ACP adapter is unaffected. Reworking `NativeAgentSession` to per-turn
   `--resume` is tracked separately.
