# Generic human-in-the-loop webhook (attention API for any external system)

| | |
|---|---|
| **Category** | Extensibility |
| **Plugin surface** | Core protocol/host feature — a new public API surface consuming the existing inbox/notification pipeline; no new plugin interface needed |
| **Priority** | P2 |
| **Rough effort** | M |
| **Depends on** | `../notifications/02-inbox-and-approvals.md` (this reuses that pipeline rather than building a second one), `../extensibility/05-scriptable-agent-cli.md` (a natural first caller of this API) |

## Background

Every "needs a human" moment Agnes handles today — a permission request, a turn finishing, a structured decision an agent needs — originates from *inside* an Agnes-managed session. But the underlying problem those features solve — "something is running unattended and needs a person's attention, right now, routed to wherever that person actually is (phone, desktop) with a way to answer that resumes the thing that asked" — isn't actually specific to coding agents at all. A CI pipeline that wants to pause for a manual approval before deploying, a long-running data-processing script that hits an ambiguous case and wants a human to pick an option, a completely unrelated automation tool's workflow that needs someone to confirm before it proceeds — all of these are the exact same shape of problem, and none of them are coding-agent sessions Agnes itself is running.

Rather than build a second, parallel "ask a human and wait" mechanism for external callers, the existing inbox/notification pipeline can be generalized to serve both: an internal permission request and an external system's "please confirm" call become the same underlying primitive — a durable, resumable request for human input, differing only in *how the answer gets delivered back* (an internal method call vs. an HTTP callback to a URL the external caller supplied).

## Current state in Agnes

`../notifications/02-inbox-and-approvals.md` proposes an aggregated, cross-session inbox for permission requests and user-action requests, all originating from sessions Agnes itself is running. There is no external-facing API for a system outside Agnes to create an entry in that inbox and be notified when a human answers it — the inbox today is entirely internal.

## Proposed design

A small, public REST endpoint plus a webhook-based resume path, built directly on top of the inbox's existing data model rather than a new one:

```
POST /v1/attention-requests
  { "source": "my-ci-pipeline", "question": "Deploy to production?",
    "options": ["approve", "reject"], "callback_url": "https://...", "timeout_seconds": 3600 }
  -> { "request_id": "..." }

GET /v1/attention-requests/{id}          # poll for an answer, for callers that can't receive a webhook
```

When a human answers (from any Agnes client, the same inbox UI used for internal permission requests), the host:

1. Records the answer against the request, exactly like resolving any other inbox entry.
2. If a `callback_url` was supplied, POSTs the answer to it (with retry/backoff on failure, and a bounded number of attempts before giving up and leaving the answer available only via polling).
3. If no callback was supplied (or the caller prefers it), the answer is retrievable via the polling endpoint — this covers callers that can't easily expose an inbound webhook of their own (e.g. something running inside a locked-down CI runner with no public endpoint).

Design notes:

- **One inbox, one mental model.** A human answering an external attention request should look and feel exactly like answering an internal permission request — same notification, same inbox entry, same UI. The only thing that differs is what happens after the answer is recorded (an internal `RespondToPermissionAsync`-style call vs. an outbound webhook POST) — that's an implementation branch inside the resolution path, not a reason to build a second inbox.
- **Auth is per-external-caller, not per-session.** An external system authenticates with its own credential (the same device-token style used elsewhere in Agnes — see the caution about credential storage in `../extensibility/05-scriptable-agent-cli.md`), scoped to creating and reading its own attention requests only, never another caller's.
- **Two delivery modes because callers have two different shapes.** A callback URL suits anything that can expose a public (or Agnes-relay-reachable) inbound endpoint and wants to resume immediately without holding a connection open (a workflow-automation tool node, for instance). Polling suits anything that can't receive inbound traffic at all, or that's calling from inside a tool-use context where only synchronous request/response is available (an AI-agent tool-calling loop being a concrete example) — supporting both from day one, rather than only the callback path, avoids forcing every future integration into one shape.
- **This is genuinely agent-agnostic, on purpose.** Nothing in the request schema should assume the caller is a coding agent, an AI system, or even automated at all — `source` is a free-text label, not an enum of known integration types, so the same primitive serves a coding-agent permission prompt, a CI approval gate, and a completely unrelated script's "are you sure" moment without Agnes needing to know about each integration in advance.
- **Concrete first integrations, once the core API exists**: a thin adapter node/plugin for common workflow-automation tools (the same idea as `../extensibility/04-channel-bridges.md`'s bridge pattern, but targeting workflow tools rather than chat apps), and documentation showing how to call it directly from a CI step — no bespoke "GitHub Actions integration" needs building; a generic HTTP call from a workflow step is sufficient once the API exists.

## Acceptance criteria

- Given an authenticated external caller POSTs a valid attention request with a `callback_url`, when a human answers it from any Agnes client, then the callback URL receives a POST containing the answer within a bounded time, with retries on transient failure.
- Given the same request without a `callback_url`, when a human answers it, then polling `GET /v1/attention-requests/{id}` returns the answer.
- Given an attention request with a `timeout_seconds` value, when that time elapses with no answer, then the request is marked expired, no further answer is accepted for it, and (if a callback was supplied) a timeout notification is sent to the callback URL distinct from a real answer.
- An external caller can only read or act on attention requests it created — a request created by one authenticated caller is not visible or answerable via another caller's credential.
- An unanswered attention request appears in the same inbox UI, alongside internal permission/user-action requests, indistinguishable in interaction pattern (though visibly labeled with its `source`) from an Agnes-native request.
- Given a callback URL that is unreachable, the host retries with backoff up to a bounded attempt count, then stops and leaves the answer available via polling — it does not retry indefinitely.
- Non-regression: internal permission requests and user-action requests continue to work exactly as `../notifications/02-inbox-and-approvals.md` specifies — this feature only adds a new way to create inbox entries, it doesn't change how existing ones are created or resolved.

## Open questions

- Rate limiting and abuse prevention for a publicly-callable "create an attention request" endpoint needs its own design pass — an unauthenticated or poorly-scoped version of this could become a notification-spam vector.
- Should there be a way for a human to permanently ignore/mute future requests from a specific external `source` (the same way one might mute a noisy notification channel), given this is explicitly open to arbitrary external callers rather than only Agnes's own trusted session code?
