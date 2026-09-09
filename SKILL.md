---
name: agnes-operations
description: Install, configure, validate, operate, and troubleshoot an Agnes host. Use when an agent needs to deploy Agnes, choose its authentication or session-isolation topology, configure the optional browser operator UI, add agent CLIs, or verify a live installation.
---

# Agnes Operations

Use this skill for production installation and operations. Read `AGENTS.md`
before changing source and `docs/deployment.md` plus `docs/security.md` before
changing the host configuration.

## Run an installation preflight

Before changing a production installation, inspect the host and ask about
unresolved policy choices. Do not silently select production defaults.

Ask explicitly: **"Do you want the Agnes browser admin/operator UI installed?"**
Do not infer the answer from an API-only or desktop-client deployment. If yes,
confirm the hostname, authentication gateway, and that the root route will be
the UI; Agnes does not have a separate `/admin` route.

Also confirm:

- agent CLIs and credentials the host may use;
- session roots, isolation mode, sandbox provider, and workload-trust policy;
- browser and native-client authentication, allowed identities, and device
  revocation procedure;
- persistent event/device storage, backups, log retention, and monitoring;
- public ingress, TLS termination, and whether clients can ever bypass it.

Record non-secret decisions in version-controlled configuration. Keep OAuth
credentials, pairing/bootstrap secrets, and agent credentials out of source
control and out of logs.

## Choose the browser UI deliberately

If the answer is **yes**:

1. Build Docker with `--target web-ui-host`.
2. Set `Agnes__WebRoot=/app/wwwroot`.
3. Protect the same host origin with the configured gateway before exposing it.
   The WebAssembly client and SignalR hub share that origin, so do not expose a
   public UI while leaving the hub reachable without equivalent controls.
4. Open the host root and choose **Continue with Cloudflare Access**. The browser
   exchanges the proxy-injected, validated assertion for an Agnes revocable device
   token; native clients retain their normal authentication paths.
   The standard protected build intentionally disables offline/PWA mode, because
   a browser cannot follow an interactive Access redirect for a service-worker
   or manifest fetch.

If the answer is **no**, build `--target host-only` (the default) and leave
`Agnes__WebRoot` unset. Use the desktop/mobile client or the authenticated host
API instead.

## Configure safely

- Prefer a reverse proxy or Cloudflare Access for TLS and network admission.
  Configure `Agnes:Auth:CloudflareAccess` with the exact application audience
  and an Agnes-side identity allowlist; never trust an unsigned email header.
- Keep pairing, OIDC, GitHub, and keypair bootstrap methods explicit. Disable
  unused bootstrap methods after confirming the intended recovery path.
- Use a dedicated writable data volume and narrowly scoped session workspace.
  Do not mount the deployment tree or unrelated repositories into `/work`.
- Choose `incus` for hard per-session isolation when available. Use shared
  kernel/process providers only when their trust boundary is accepted and
  documented.
- Retain per-tool permission prompts unless a trusted, bounded automation case
  has been approved.

## Validate the deployment

1. Confirm the host starts, persistent storage is writable, and no secrets are
   logged.
2. From the production ingress, verify the required authentication gateway
   rejects an unapproved identity and admits an approved one.
3. If the browser UI is installed, verify `/` returns its HTML and that a
   static framework asset and the SignalR hub load over the same protected
   origin. Otherwise verify `/` remains the protocol banner.
4. Complete Agnes authentication, create a disposable ask-first session, and
   verify the permission prompt, live updates, reconnect, and session history.
5. Verify that a revoked device token can no longer access the host and that a
   restart preserves the intended data.

Do not report success based only on a running process. Report ingress,
authentication, UI (when selected), session execution, persistence, and
revocation separately.
