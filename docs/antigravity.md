# Antigravity (`agy`)

Google Antigravity is the successor to gemini-cli. Agnes drives it through
`Agnes.Agents.Antigravity`, over the native stream-json adapter rather than ACP — Google ships no ACP for
it.

Everything below was established against **`agy 1.1.24`** on a live CLI and in a clean Incus guest. There
is no published protocol documentation; where this document states a behaviour, it was observed.

## Why it is not just "another `--print` agent"

`agy` supports `--input-format stream-json`, which reads **one NDJSON message per line from stdin and
runs a turn for each**. That makes it a persistent peer, not a one-shot command: the conversation stays
in the CLI's memory between turns.

Verified: two messages into one process, the second answered from the first's context, same
`conversation_id`, `num_turns` going 1 → 2.

This is the main difference from CodeyBox's runner, which invokes `agy --print` once per prompt and
reconstructs continuity with `--continue`. That is a reasonable shape for a batch orchestrator and the
wrong one for an interactive client.

## The protocol

**Input**, one line per turn:

```json
{"event":"user","message":{"role":"user","content":[{"type":"text","text":"…"}]}}
```

Note `event`, not `type`. It resembles Claude Code's stream-json and is not it — feeding a Claude-shaped
line answers `stream input message is missing the "event" field`.

**Output** frames:

| `event` | When | Carries |
| --- | --- | --- |
| `init` | once, at startup | `conversation_id`, model, cwd, the full tool list |
| `step_update` | many | `step_index`, `state`, `step_type`, and per type: `text_delta`, `tool_name`, `tool_info`, `usage` |
| `result` | **once per turn** | `status`, `response`, `error`, `num_turns`, `usage` |

`step_type` is one of `user_input`, `agent_response`, `tool`, `system_message`. A tool step appears twice
under a stable `step_index` — `ACTIVE`, then `DONE` or `ERROR` — which is why the index is used as the
tool-call id.

`result` ends a **turn**, not the session. The process stays open for the next line.

## Three traps

Each of these produces a plausible-looking success rather than an error, which is what makes them worth
writing down.

### 1. `--print` takes a value

In 1.1.24 `--print` is a string flag, so a bare `--print` swallows whatever follows it:

```
Error: --print took "--dangerously-skip-permissions" as its prompt, so the intended prompt was left as
an argument and ignored.
```

`--input-format stream-json` selects print mode on its own; Agnes passes no `--print` at all. CodeyBox's
runner still builds `[agy, --print, --dangerously-skip-permissions]`, which is malformed on this version.

### 2. Without `--dangerously-skip-permissions`, it does not ask — it pretends

Omitting the flag does not produce a permission prompt, an error, or a refusal. The CLI **silently
redirects file writes to `~/.gemini/antigravity-cli/scratch/`** and reports `SUCCESS`, describing edits
it appears to have made. The working directory is untouched.

There is no permission protocol to implement over the stream. So Antigravity is **autonomous-only** in
Agnes: `AntigravityAgentAdapter` refuses an attended session outright, because presenting one would mean
showing a gated session that is not gated — or worse, one that quietly does nothing.

### 3. Without `--add-dir <cwd>`, it writes to scratch *even with* the skip flag

This is the one that cost the most to find. In a **clean** guest, the identical prompt with
`--dangerously-skip-permissions` still wrote to `~/.gemini/antigravity-cli/scratch/` and reported success.
Adding `--add-dir <working directory>` fixed it; so did `--new-project`.

It looked correct on the developer host only because that machine had accumulated Antigravity workspace
state for the directory in use. A fresh sandbox has none — which is exactly the case that matters.

`NativeLaunchSpec.WorkingDirectoryArguments` exists for this: a hook for a CLI that will not treat its own
cwd as writable until told to. CodeyBox passes neither flag.

## Sandbox

Antigravity belongs in a sandbox — it is the only real boundary the CLI has.

**Image.** `agy` is a single ~200 MB dynamically-linked ELF. Nothing else is required: pushed alone into
a stock `ubuntu/24.04/cloud` guest it reports its version, lists models, and runs turns. The 79 MB
`~/.gemini/antigravity-cli/` support tree on the host is **not** needed.

```bash
sg incus-admin -c '
  incus init images:ubuntu/24.04/cloud agy-bake --vm --no-profiles \
    --storage codeybox-zfs --config limits.cpu=2 --config limits.memory=4GiB \
    --device root,size=16GiB
  incus config device add agy-bake eth0 nic nictype=bridged parent=cb-net name=eth0
  incus start agy-bake
  # …wait for the guest agent…
  incus file push ~/.local/bin/agy agy-bake/usr/local/bin/agy --mode=0755 --uid=0 --gid=0
  incus exec agy-bake -- /usr/local/bin/agy --version
  incus exec agy-bake -- cloud-init clean --logs
  incus stop agy-bake
  incus publish agy-bake --alias agnes-antigravity-baseline
  incus delete agy-bake --force
'
```

Keep the root at **16 GiB**: `IncusSandboxProvider` asks for a 16 GiB volume, and Incus refuses to create
an instance from an image whose declared size exceeds it (`Source image size … exceeds specified volume
size`). A 24 GiB bake fails at launch, not at bake time.

**Credentials.** `AntigravityCredentialProvider` materialises
`~/.gemini/antigravity-cli/antigravity-oauth-token` into the guest, and does **not** strip the refresh
token — unlike `ClaudeCredentialProvider`, which must.

The reason is observable: on this host the stored `token.access_token` had been expired for **69 days**
while `agy models` kept working. The CLI refreshes from `refresh_token` at run time and does not write
the new access token back. Shipping the sanitised half would ship a credential that is already dead.

The trade is that the guest holds a long-lived Google refresh token for the session's duration. That is
the same trade the orchestrator makes, and it is why these sandboxes should be short-lived.

## Models

`agy models` lists what the gateway currently offers, and it moves independently of the CLI version —
this host was serving Gemini 3.8 while a baked-in list elsewhere still named 3.5. So there is no static
catalogue; the adapter probes.

Reasoning effort is encoded in the model id (`gemini-3.8-flash-high`, `…-low`) rather than passed
separately, so `--effort` is not threaded.

## Configuration

```jsonc
{
  "Agnes": {
    "Antigravity": {
      "Command": "agy",
      "Args": ["--input-format", "stream-json", "--output-format", "stream-json"],
      // agy's own default is 5 minutes, and it aborts the whole session when a turn exceeds it.
      "PrintTimeoutSeconds": 1800
    }
  }
}
```
