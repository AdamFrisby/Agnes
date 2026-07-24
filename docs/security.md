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
| `SessionIsolation` | `Shared` \| `PerUser` \| `PerGroup` | `Shared` | How sessions are scoped to callers. `Shared` = today's behaviour (host owner sees all; others need an explicit share). `PerUser` also lets a caller reach the sessions **they own** (matched across their devices). `PerGroup` also lets **group members** reach a session (read/drive, not manage) via an `IGroupProvider`. The host owner stays an admin super-user in every mode; these are additive grants on top of shares. |
| `RestrictConfigToOwner` | bool | `false` | Restricts host-wide config mutations — sandbox image manifest, project config, MCP registry, sandbox delete/reap — to the **host owner** rather than any paired device. |
| `MaxConcurrentSandboxes` | int | `0` (unlimited) | Caps the number of concurrently-running sandboxes host-wide; a new sandboxed session is refused once the ceiling is reached. |
| `TranscriptRetentionDays` | int | `0` (keep forever) | A daily sweep prunes event-log entries older than this many days. Session catalogue rows are kept (sessions still restore); only aged transcript content is removed. |

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
    "AllowedHostMcpServers": [],                    // list names ONLY if you truly need host-run MCP servers
    "SessionIsolation": "PerGroup",                 // Shared | PerUser | PerGroup (see below)
    "RestrictConfigToOwner": true,                  // only the operator edits image/project/MCP config
    "MaxConcurrentSandboxes": 32,                   // 0 = unlimited
    "TranscriptRetentionDays": 90                   // 0 = keep forever
  }
}
```

### Session isolation & groups

`SessionIsolation` controls who can reach whose sessions. Groups are a **plugin point**
(`IGroupProvider`): the shipped backend treats a session's **repo as its group** and **GitHub write
access** as membership (via the linked GitHub App's collaborator-permission API). So under `PerGroup`,
everyone who can push to repo X can collaborate on X's sessions, and no one else can. Other membership
backends (LDAP, SSO teams, a static roster) can ship as additional `IGroupProvider` plugins without
touching core. A session's owner is the caller's GitHub login (falling back to device id), recorded at
open time; the host owner remains an admin who can reach every session.

### Egress control

`RequireSandbox` isolates the agent, but a sandbox still has open network by default. Two options, both
**host-enforced** (a sudo agent inside the VM can't disable them):

**Per-profile bridges (CodeyBox-compatible, recommended).** Egress policy lives in the host kernel
(nftables) on one filtered bridge per profile, and the *bridge choice is the policy*. This reuses
[CodeyBox](https://github.com/AdamFrisby/CodeyBox)'s `scripts/setup-host-networks.sh` verbatim: run it once
to create the `cb-*` bridges (each with its own allowlist — `-` = no egress, `internet` = no-LAN, or a
hostname allowlist), then map profile names to those bridges and pick a default:

```jsonc
"Agnes": { "Sandbox": { "Incus": {
  "NetworkProfiles": { "locked": "cb-locked", "internet": "cb-internet" },
  "NetworkProfile": "locked"          // default for every sandbox; a spec may request another
} } }
```

Agnes creates each VM with `--no-profiles` and a single bridged NIC on the profile's bridge, so that
bridge is the VM's only path out — nothing in the guest to flush. See CodeyBox's `docs/host-firewall.md`.

**Incus network ACLs (Incus-only alternative).** Define a default-deny + allowlist ACL in Incus and
attach it to every sandbox NIC:

```jsonc
"Agnes": { "Sandbox": { "Incus": { "NetworkAcls": ["agnes-egress"] } } }
```

Either way, the trusted image-bake VM is left open so it can install tooling.

### Usage attribution

`GET /admin/usage` (owner-gated when `RestrictConfigToOwner` is on) returns live per-owner session /
sandbox counts, and the same summary is logged every 15 minutes — so you can see who is consuming the
host. Combine with `MaxConcurrentSandboxes` to bound total load.

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

## Known residual risks

Mitigated by the options above (turn them on) but worth stating plainly:

- **Session ownership is by GitHub login / device id, not a full tenant model.** `PerUser`/`PerGroup`
  scope access, but the host owner remains an admin who can read every session. For hard tenant
  separation (operator can't read tenant transcripts), give each tenant their own host.
- **Group membership is only as fresh as its cache.** Repo write-access results are cached ~5 min, so a
  revoked collaborator may retain access briefly. Membership checks also require the linked GitHub App;
  with none linked, `PerGroup` grants no group access (owner-only).
- **Credential scoping needs `Project.CredentialAccount` set.** Minting is pinned to a project's linked
  account only when configured; otherwise it routes by repo owner. Keep `GitCredentialMode=Off` by
  default and pin accounts per project.
- **Egress ACLs are enforced by Incus, not Agnes.** Correct policy definition (default-deny + a tight
  allowlist) is on you; a permissive ACL is no protection. Disk usage (forks copy whole workspaces) is
  still unbounded — cap it at the OS / storage-pool level.
- **Retention/attribution are host-side, not tamper-proof.** `TranscriptRetentionDays` deletes old
  content but isn't a legal-hold/audit system; the usage report reflects live state only.

See `docs/architecture.md` for the design and `docs/deployment.md` for the full auth/config
reference.
