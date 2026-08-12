# Agnes security audit — 2026-08-12

**Status:** In progress  
**Revision:** `045e9260dd03afbaa5d784938982f67d627624b3`  
**Priority:** 2 of 3  
**Target:** OWASP ASVS 5.0 Level 2, plus applicable Level 3 identity, secrets, plugin, agent-execution, and sandbox controls

This living report covers pre-open-source readiness under a hostile authenticated-organization-user model. Repositories, prompts, agents, plugins, MCP servers, packages, and rendered output are untrusted.

## Threat model

- Device and identity credentials must be scoped, hashed where appropriate, revocable, and resistant to replay.
- Session, group, project, workspace, and administrative authorization must hold across every client and transport.
- Agent subprocesses and plugins are hostile code and must not inherit host-wide filesystem or credentials.
- A sandbox escape, cross-session credential read, or unauthorized host MCP execution is Critical.
- Cloudflare Access is an additional edge control; Agnes must enforce its own identity and session policy.

## Confirmed findings

| ID | Severity | Finding | Evidence | Required disposition | Status |
| --- | --- | --- | --- | --- | --- |
| AG-2026-001 | High | The deployed Agnes container ran with the image's default user and mounted the full deployment tree read-write at `/work`. A compromised agent could access or alter unrelated repositories and deployment files visible through that mount. | Deployment Compose file; live container/mount inspection on 2026-08-12 | Run non-root; mount only the assigned workspace; make code/config read-only where possible; prohibit the full-stack mount. | Remediated 2026-08-12: UID 10001, dedicated `/var/lib/agnes/workspaces`, no deployment-tree mount, read-only root. |
| AG-2026-002 | High | The deployed configuration does not enable `RequireSandbox`, allowed session roots, per-user/group isolation, owner-only configuration, or a sandbox provider, despite the shared-host threat model. | `../deploy/compose.yaml`; `docs/security.md` secure-baseline comparison | Configure a kernel-isolated provider and enforce the documented shared-host baseline before release. | Partially mitigated 2026-08-12: roots, attended execution, MCP deny-all and per-group isolation enforced. Host is explicitly trusted/shared-kernel; untrusted mode fails closed without dedicated-kernel capability. Dedicated-kernel test remains open. |
| AG-2026-003 | Medium | The deployed container has no CPU, memory, PID, disk, or bounded-log controls and uses a writable root filesystem. | `../deploy/compose.yaml` | Add explicit resource budgets, read-only root where supported, bounded writable volumes, and log rotation. | Partially remediated 2026-08-12: read-only root, noexec tmpfs, CPU/memory/PID budgets; the relay image also runs as an unprivileged user. Bounded logging/disk quota remains open. |
| AG-2026-004 | Medium | GitHub Actions used mutable major-version action tags and lacked dedicated CodeQL and dependency review. | `.github/workflows/ci.yml`, `.github/workflows/release.yml` | Pin actions by commit SHA, minimize permissions, and add required security checks. | Remediated 2026-08-12: actions pinned, permissions minimized, CodeQL/dependency audit/review plus blocking Gitleaks, zizmor, Trivy filesystem and production-image scans added. |
| AG-2026-005 | Medium | Release workflows published binaries and a container without a published SBOM, checksums, or provenance evidence. | `.github/workflows/release.yml` | Produce CycloneDX/SPDX SBOMs, checksums, and signed GitHub artifact/container attestations. | Remediated 2026-08-12 for binaries: CycloneDX SBOMs, SHA-256 checksums and GitHub provenance attestations added; image provenance is attached to its immutable digest. |

## Additional code hardening — 2026-08-12

- Claude Code ACP is installed at an exact version during image construction and is no longer resolved with `npx` while the service is running.
- Production rejects unsigned-plugin mode.
- Plugin extraction now rejects canonical paths outside the version root, including prefix-sibling traversal.
- Plugin state writes propagate failures and roll back their in-memory mutation rather than reporting an install that will disappear after restart.
- Production plugin source/version/SHA-512 approvals are exact, persisted with plugin state, and
  revalidated from the cached archive before enabled plugins load. Production rejects legacy or
  unapproved plugin state, non-HTTPS/unconfigured sources, unpinned installs, and unsigned mode.
- Plugin-management hub methods are host-owner-only; a paired collaborator cannot search, install,
  configure, enable, update, unload, or enumerate host-process plugins.
| AG-2026-006 | Medium | Repository governance does not currently enforce protected changes on the default branch. | GitHub default-branch protection and ruleset inspection on 2026-08-12 | Require reviewed pull requests and mandatory security/build checks; block force-push/deletion. | Open |

## Positive controls observed

- Device tokens are designed to be hashed and individually revocable.
- Pairing becomes approval/grant based after the bootstrap device.
- Server-side session guardrails exist for roots, sandbox requirements, permissions, isolation, owner-only configuration, and concurrency.
- Incus sandbox and host-enforced egress mechanisms are implemented and documented.
- Authentication, credential scoping, sandbox, path, session authorization, and rate-limit tests exist.
- `dotnet list Agnes.Core.slnf package --vulnerable --include-transitive` reported no known vulnerable packages during the initial pass.

These controls are not effective merely because they exist; deployed defaults and bypass resistance require verification.

## Dynamic tests pending

- Pairing, device-token replay/revocation, GitHub/OIDC, mTLS, keypair, and forwarded-header tests.
- Cross-user/session/group/project authorization through REST, SignalR, relays, and clients.
- Workspace traversal, symlink/hardlink, Git hook/config, submodule/LFS, malicious filename, and terminal-rendering tests.
- Autonomous and one-shot skipped-permission path review.
- Plugin/NuGet/MCP provenance, load, update, and host-execution tests.
- Sandbox escape, egress, metadata SSRF, persistence, fork bomb, memory, disk, and process-limit tests.

## Audit-environment status

The planned Multipass audit VM cannot start because nested KVM is unavailable on this host. Agnes escape and hostile-agent testing requires a separate kernel boundary. An external or software-emulated VM is required before destructive testing.

## Release gate

No Critical or High findings may remain. Medium findings require correction or named, time-bounded acceptance with a compensating control. Every fix requires a regression test and independent internal verification.
