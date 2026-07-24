# Operating Agnes securely (shared / multi-tenant hosts)

Agnes runs coding agents with real filesystem, process, and (optionally) network and
git-credential access. On a **shared host** — one daemon that several developers pair to and
launch agents on — that access is the whole attack surface. This document is the operator
hardening guide: the config knobs that exist today, recommended settings, and the residual
risks you should know about.

The **sandbox is the primary isolation boundary** (per-session Incus VM). Everything below is
defence-in-depth *around* it, plus guidance for hosts that can't sandbox.

All keys live under the `Agnes:` configuration root (`appsettings.json`, environment variables,
or any ASP.NET configuration source). Every guardrail is **opt-in** and defaults to the
historical behaviour, so upgrading changes nothing until you turn it on.

## Session guardrails (`Agnes:Security:*`)

| Key | Type | Default | Effect |
| --- | --- | --- | --- |
| `AllowedSessionRoots` | string[] | `[]` (unrestricted) | Every session working directory must canonicalise to a location **inside one of these roots**. Boundary-aware (`/srv/work` admits `/srv/work/a`, not `/srv/work-evil`), collapses `..`, resolves symlinks on the existing prefix. Enforced on new / fork / handoff / resume, before any filesystem side effect. |
| `RequireSandbox` | bool | `false` | The host **refuses any session that would run outside a sandbox** (sandbox opted out, or no provider configured). Fails loud instead of silently running the agent on the host. |
| `RequirePermissionPrompts` | bool | `false` | The host **forbids autonomous / `--dangerously-skip-permissions` sessions** entirely — every tool call must be prompted. The strongest autonomy control. |
| `AllowUnsandboxedSkipPermissions` | bool | `false` | Whether autonomous mode may run **outside** a sandbox. Default `false`: dangerous autonomous mode is confined to a sandbox unless you explicitly opt in. |
| `AllowedHostMcpServers` | string[] | `[]` (unrestricted) | Allowlist (by MCP server **name**, case-insensitive) of the only servers permitted to run with `RunAt=Host` — i.e. execute a command **on the host, outside the sandbox**. A non-allowlisted host server is dropped from the session's MCP set (with a visible notice) on both the direct and the sandbox-forward paths. Sandbox-run servers are unaffected. |

All of these are enforced **server-side in `SessionManager`**, at the single shared open/resume
path — so they hold regardless of what a client (desktop, web, the MCP/voice backends) sends.
`RequireSandbox` and `RequirePermissionPrompts` are also advertised in `HostInfo` so a
cooperating client locks the corresponding toggles, but that is UX only; the host is the
enforcement point.

### Recommended shared-host baseline

```jsonc
"Agnes": {
  "Security": {
    "AllowedSessionRoots": ["/srv/agnes/work"],   // ideally a per-user subtree, e.g. /srv/agnes/work/<user>
    "RequireSandbox": true,                         // needs Agnes:Sandbox:Provider configured (see deployment.md)
    "RequirePermissionPrompts": false,              // set true to ban autonomous mode outright
    "AllowUnsandboxedSkipPermissions": false,       // keep false: autonomy only inside a sandbox
    "AllowedHostMcpServers": []                     // list names ONLY if you truly need host-run MCP servers
  }
}
```

> **`RunAt=Host` MCP servers are arbitrary host code execution** — that is by design (the
> host-forward shim runs the real command on the host when a sandboxed agent calls the tool).
> On a shared host, either leave `AllowedHostMcpServers` empty and configure every MCP server as
> `RunAt=Sandbox`, or allowlist a small, audited set of host servers by name. Treat adding a host
> MCP server as a privileged operation.

## Vendor-pin the agent CLIs

By default the Claude adapter launches `npx -y @zed-industries/claude-code-acp`, which **fetches
from npm at session start** — a runtime supply-chain dependency. Pin it to a vendored binary you
control via the existing launch-command config:

```jsonc
"Agnes": {
  "ClaudeCode": { "Command": "/opt/agnes/bin/claude-code-acp", "Args": [] }
  // likewise Agnes:OpenCode:*, Agnes:ClaudeCodeNative:*, Agnes:Codex:*
}
```

Bake the agent CLIs into the sandbox image (see `docs/sandbox-live-testing.md`) rather than
resolving them at runtime, and lock down who can edit the sandbox image manifest — a poisoned
manifest pre-compromises every subsequent sandbox. Plugin installation is admin-gated and refuses
unsigned packages by default; keep that on. `Agnes:CustomBackends` lets config point the launcher
at an arbitrary command — treat it as admin-only.

## Authentication

- **Bootstrap methods** are opt-in. Prefer **GitHub SSO restricted to your org/users** or OIDC
  over static pairing codes for a shared host. GitHub SSO already fails safe: it will not enable
  unless `Agnes:Auth:GitHub:AllowedUsers` or `AllowedOrgs` is non-empty.
- **Device tokens** are per-device, individually revocable, and stored **hashed**. Revoke on
  offboarding.
- **CORS**: never set `Agnes:AllowAllOrigins=true` on a shared/public host (it defaults to
  `false`). Set an explicit `Agnes:AllowedOrigins` for the web client.
- Auth endpoints are rate-limited (`Agnes:Auth:RateLimit:*`).

## Data at rest

The event store holds **full session transcripts** — which routinely contain secrets that flowed
through a session — plus the device registry (`~/.agnes/devices.json`, tokens hashed) and MCP
config. Agnes does not encrypt these at rest; that is a deployment responsibility:

- Run the daemon as a **dedicated, unprivileged user** and restrict the data directory
  (`~/.agnes/`, the SQLite DB file, `devices.json`) to that user (`chmod 700`).
- Put the data directory on an **encrypted volume** (LUKS / cloud disk encryption).
- If transcript confidentiality is critical, consider the Postgres event store on a hardened DB,
  or a custom `IEventStore` — the store is a plugin point.
- There is **no built-in retention/pruning** of the event log yet; scrub or rotate the store out
  of band if you need bounded retention.

## Known residual risks (not yet mitigated in code)

These are on the roadmap; until they land, compensate operationally.

- **No per-session ownership / tenant isolation.** Sessions are effectively owned by the *host
  owner* (the bootstrap/earliest-paired device), who can access every session; other devices need
  an explicit share. There is no per-user or per-group session isolation yet. Mitigate by giving
  each tenant their own host, or limit who pairs.
- **Global git-credential selection.** A session can mint git tokens for any linked account
  (routed by repo owner), not scoped to the session's user. Keep `GitCredentialMode=Off` by
  default and link only the accounts you're comfortable exposing host-wide.
- **Sandbox network egress is open by default.** A sandboxed agent can reach the internet and
  internal networks. Apply egress filtering at the Incus bridge (default-deny + allowlist your
  package registries and git host).
- **No resource quotas.** Concurrent sandboxes, disk (forks copy whole workspaces), and CPU are
  unbounded. Cap via the OS / Incus and monitor per-session usage for attribution.

See `docs/architecture.md` for the design and `docs/deployment.md` for the full auth/config
reference.
