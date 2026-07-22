# In-app bug reporting & diagnostics

| | |
|---|---|
| **Category** | Operations |
| **Plugin surface** | New `IBugReportSink` (see `../00-plugin-architecture.md`) |
| **Priority** | P2 |
| **Rough effort** | S–M |

## Background

Agnes is a host daemon that runs coding-agent CLIs (Claude Code, Codex, …) under ACP and streams their session events to desktop, web, and mobile clients. That's a lot of moving parts owned by other people's software — a CLI can misbehave, an ACP capability negotiation can fail in a way only a specific provider version triggers, a permission round-trip can hang. When something breaks, the person best placed to describe *what happened* is the user in front of the client at the time, but today they have no structured way to hand that context to a maintainer. They'd have to reproduce the problem, dig through host logs by hand, and file a report from scratch with no scaffolding — most people just won't.

Two distinct problems live under "diagnostics," and they deserve different treatment:

1. **Manual bug reports** — a user noticed something wrong and wants to tell someone, with enough context (what they expected, what happened, maybe a log excerpt) that it's actionable without a back-and-forth.
2. **Automatic crash/error telemetry** — failures nobody manually reports because the app just silently misbehaved or a background task threw. Without this, a maintainer only learns about a class of bug if a user happens to notice, care, and report it.

Both matter more for Agnes than for a typical app because Agnes's host process routinely holds real credentials (agent API keys, git credentials) and can run on a machine the user doesn't want fully exposed. Any diagnostics feature has to be designed so "help me debug this" never becomes "silently leak my host's private data."

## Current state in Agnes

There is no bug-reporting flow and no crash/error telemetry of any kind in Agnes today — no telemetry SDK is referenced anywhere in the solution's project files, and there's no code path that packages up session or host state for a report. The event-sourced session log (`SqliteEventStore` / `InMemoryEventStore`, implementing `IEventStore` in `Agnes.Host.Events`) already captures a durable, ordered history of everything that happened in a session, which is a useful ingredient for diagnostics but isn't exposed as one today.

## Proposed design

```csharp
namespace Agnes.Abstractions;

public interface IBugReportSink
{
    string Id { get; }   // "github-issue" | "custom-endpoint"
    Task<BugReportResult> SubmitAsync(BugReport report, CancellationToken ct = default);
}

public sealed record BugReport(
    string Title,
    string Summary,
    string? CurrentBehavior,
    string? ExpectedBehavior,
    byte[]? DiagnosticPayload);
```

This fits Agnes's existing plugin pattern directly: a small provider interface, a descriptor-style id, and room for multiple built-in implementations without touching core host code.

- **`GitHubIssueSink`** as the default implementation. Agnes's source already lives on GitHub and there's no existing hosted "reports" backend to send data to — standing one up purely to collect bug reports would be new infrastructure to operate for a capability GitHub already provides for free. `GitHubIssueSink` opens a prefilled issue against the project's repository (or, given a `GITHUB_TOKEN`, searches existing issues for likely duplicates first and offers to comment there instead of opening a new one — cutting down on duplicate-issue noise for the maintainer). This alone covers most of the value with zero new servers.
- **`CustomEndpointSink`** for self-hosters who want reports routed somewhere other than GitHub (an internal tracker, a webhook, etc.) — same shape as `GitHubIssueSink`, just posts `BugReport` as JSON to a configured URL with a byte cap and upload timeout so a large `DiagnosticPayload` can't hang the client or blow past a reasonable size.
- **Server diagnostics as a narrow, separate opt-in.** Attaching a snapshot of host-side logs (`BugReport.DiagnosticPayload`) is disproportionately more sensitive than the rest of the report — host logs can contain file paths, command arguments, or fragments of session content. This capability should be: off by default, restricted to the host owner (whoever holds an admin-level paired-device token, not any paired client), and rate-limited, so it can't be used to repeatedly pull data off a host by a client that merely has ordinary access. Keeping it a distinct, explicitly-invoked action — rather than folding "attach my logs" into the default report flow — makes the sensitive path opt-in by construction instead of something a user can trigger by accident.
- **Crash/error telemetry** is a smaller, independent lift: wire a crash-reporting library into the Avalonia/Uno UI heads and into `Agnes.Host` itself, so unhandled exceptions and background-task failures get recorded automatically instead of depending on a user noticing and filing a manual report. This should ship **opt-in, not opt-out** — Agnes doesn't yet have a privacy policy or terms-of-service infrastructure that would let it make privacy promises the way an app with a legal/compliance function can, so silently phoning home by default isn't appropriate at this stage of the project.
- **Whatever telemetry stance Agnes ends up with, its marketing copy must match it exactly.** A "no telemetry" or "no tracking" claim is only defensible if it's literally true — if any anonymized/opt-out analytics is ever added for product-usage insight, README and marketing copy should describe that accurately (what's collected, that it's opt-out, how to disable it) rather than continuing to claim zero telemetry. A privacy claim that doesn't match the actual dependency tree is a credibility risk the moment anyone reads the source to verify it — and "audit the code yourself" is exactly the kind of claim Agnes is likely to make, which makes an inaccurate telemetry claim sitting right next to it worse, not better.

## Acceptance criteria

- Given a user on any client, when they open "Report a bug," fill in a title/summary, and submit, then a `GitHubIssueSink` (or whichever sink is configured) successfully creates or comments on an issue containing that content.
- Given a `GITHUB_TOKEN` is configured and an existing open issue closely matches the new report's title, when a user starts filing a report, then they're shown the likely-duplicate issue and offered "comment on this instead" before a new issue is created.
- Given the configured `IBugReportSink` endpoint is unreachable or disabled, when a user submits a report, then the client falls back to opening a prefilled public GitHub issue in the browser rather than silently failing, and no private diagnostic payload is included in that fallback path.
- Given server-diagnostics attachment is disabled (the default), when any user — including the host owner — opens the bug-report flow, then there is no control offered to attach host logs.
- Given server-diagnostics attachment is enabled by the host owner, when a non-owner paired client attempts to invoke it, then the request is rejected.
- Given crash telemetry is not yet explicitly enabled by the user, when the app or host encounters an unhandled exception, then no telemetry is sent anywhere.
- Given a `DiagnosticPayload` larger than the configured byte cap, when a report is submitted, then the client rejects or truncates it locally before upload rather than sending an oversized payload and relying on the server to enforce the limit.

## Open questions

- Should Agnes run any hosted reports backend of its own, or is `GitHubIssueSink` sufficient indefinitely? Given the project's current maintenance scale, leaning toward GitHub-issue-only for now — a custom reports backend is real ongoing infrastructure to operate for very little marginal benefit relative to just using GitHub's existing issue tracker and search.
- Crash telemetry needs an explicit privacy stance decided up front — what fields get captured (stack trace only? local file paths? environment info?), and whether it's a global opt-in or something a user can scope per-host — rather than leaving that as an implementation detail to be improvised while wiring up the library.
- Rate-limit thresholds for server-diagnostics attachment should probably reuse the same throttling approach already used for the auth endpoints in `docs/deployment.md` (`Agnes:Auth:RateLimit`), rather than inventing a second rate-limiting mechanism.
