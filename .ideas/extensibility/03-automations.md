# Automations ("cron for agents")

| | |
|---|---|
| **Category** | Extensibility |
| **Plugin surface** | New `IAutomationTrigger`; extends the existing `ScheduledTaskManager`/`ScheduledRunner` (see `../00-plugin-architecture.md`) |
| **Priority** | P1 — Agnes already has a simpler version of this; growing it to full parity is comparatively cheap and high-value |
| **Rough effort** | M |
| **Depends on** | `../security/01-end-to-end-encryption.md` (for at-rest protection of stored templates, if adopted) |

## Background

A lot of the value of a coding agent isn't in one interactive conversation — it's in things that should just happen on a schedule without a human kicking them off: check open PRs every morning and flag anything stale, re-run a lint sweep nightly, poll an issue tracker for anything newly assigned and triage it. Agnes already treats "a session running unattended" as a first-class idea; automations are the natural extension of that into "a session that starts itself, on a schedule, and reports back what happened" — the point isn't a new capability so much as removing the requirement that a human be present to press "go."

The two things that make this genuinely tricky, beyond just "run this on a timer," are: (1) reliability — a scheduled run must execute exactly once per due time even when multiple hosts could plausibly run it, and a host crashing mid-run shouldn't mean the run silently vanishes; and (2) not surprising the user — pausing an automation, checking when it last ran and what happened, and deleting it cleanly all need to be first-class, visible actions, since an automation that's silently still running (or silently stopped running) after the user thought otherwise is a real trust problem for a feature explicitly designed to act without supervision.

## Current state in Agnes

Agnes already has a working `ScheduledTaskManager`/`ScheduledRunner` with a server-side "inbox" of completed runs — this is a real head start, not a green-field feature. What it's missing relative to a fully-grown version of the same idea:

- Cron-expression scheduling (today's model is interval-only: "every N minutes").
- Explicit multi-host assignment with priority and lease-based claiming (today's model is effectively single-host-implicit, since Agnes doesn't yet have a way to say "this schedule may run on any of these hosts").
- Pause/resume as first-class, persisted states.
- A "target an existing session" mode, rather than always spinning up a new session per run.
- A run-lifecycle state machine with retry and lease semantics, so two hosts can't double-execute the same due run and a crashed run doesn't just disappear.

## Proposed design

Grow the existing subsystem rather than replace it — the inbox, the task manager, and the runner all stay; this adds scheduling flexibility, multi-host awareness, and lifecycle rigor on top:

```csharp
// Agnes.Abstractions — new trigger plugin point; interval scheduling already effectively exists
public interface IAutomationTrigger
{
    string Kind { get; }   // "interval" | "cron" | "webhook"
    DateTimeOffset ComputeNextRunAt(AutomationSchedule schedule, DateTimeOffset from);
}

public sealed class CronAutomationTrigger : IAutomationTrigger { /* wraps a standard .NET cron-expression library */ }
public sealed class WebhookAutomationTrigger : IAutomationTrigger { /* an external POST arms the next run instead of a clock */ }
```

Design notes:

- **Cron via a standard, well-tested expression library, not a hand-rolled parser.** Cron expression syntax has enough edge cases (day-of-month vs. day-of-week interaction, DST transitions, timezone handling) that a hand-rolled implementation is a durable source of subtle scheduling bugs; wrapping an existing, widely-used .NET cron library behind `IAutomationTrigger` keeps that complexity contained and out of Agnes's own code.
- **Run lifecycle should be daemon-pull with a lease, not server-push, because Agnes already has multiple hosts that could plausibly serve one schedule.** `docs/architecture.md` already describes one client paired to dozens of agents across multiple hosts; once a schedule can be assigned to more than one candidate host, something has to prevent two hosts from both picking up the same due run. A `queued → claimed → running → succeeded|failed|cancelled|expired` state machine, where claiming a run acquires a time-limited lease, gives a clean, provable answer: a host that crashes mid-run simply lets its lease expire, and the run becomes claimable again rather than being lost — no coordination protocol needed beyond "check the lease before claiming, and honor an expiry."
- **`ScheduledTaskManager` gains a `TargetKind`** (`NewSession` / `ExistingSession`) alongside its existing task definition, and a `HostAssignment` list (`{HostId, Enabled, Priority}`) so a schedule can be explicitly bound to one or more candidate hosts with a preference order — a natural fit for Agnes's existing multi-host model, not a new concept bolted on.
- **Template storage**: if a stored automation template needs to be encrypted at rest (e.g. because it may embed sensitive prompt content or credentials), that storage should go through whatever standard, boring at-rest encryption primitive `../security/01-end-to-end-encryption.md` establishes for other stored content (BCL primitives such as `AesGcm`, not a bespoke scheme) — automations should not invent their own encryption approach independent of that decision. If nothing in that doc's threat model actually requires templates to be encrypted at rest beyond what the existing SQLite event store already protects, this can be scoped down to "store like any other session-adjacent content" rather than adding encryption machinery automations don't need.
- **Pause/Resume/Run-now/Delete** map onto small, additive methods on the existing `IAgnesHub` scheduled-task surface.

## Acceptance criteria

- Given a cron-expression schedule (e.g. "9am weekdays, America/New_York"), when the scheduled time arrives in that timezone, then a run is created at the correct wall-clock moment, including correctly across a daylight-saving-time transition.
- Given a schedule assigned to two hosts, when both hosts are online and the run becomes due, then exactly one host successfully claims and executes the run — verified by an automated test asserting no double-claim occurs under concurrent claim attempts.
- Given a host claims a run and then crashes (or is killed) before completing it, when the claim's lease expires, then the run becomes claimable again by another eligible host rather than remaining stuck in `claimed`/`running` forever.
- Given an automation targeting an existing session, when the schedule fires, then a message is queued into that already-running session rather than a new session being started.
- Given a paused automation, when its scheduled time arrives, then no run is created; resuming it re-enables future runs on the existing schedule (it does not require re-creating the automation).
- Given the existing interval-only scheduling behavior, after cron support is added, then existing interval-based automations continue to run on their original schedule unmodified — no regression for automations created before this change.
- Given "Run now" is invoked on an automation, then a run is created and executed immediately regardless of its next scheduled time, and the automation's regular schedule is unaffected by that out-of-band run.

## Open questions

- Whether Agnes's current `ScheduledTaskManager` already has any latent multi-host support versus being implicitly single-host — worth confirming against the actual implementation before scoping the `HostAssignment` work, since the real gap may be smaller (or differently shaped) than it appears from the architecture description alone.
- Webhook-triggered automations (an external POST arms the next run) are a reasonable stretch goal but are speculative beyond the core scheduling need — worth keeping as an optional trigger kind behind the same `IAutomationTrigger` interface rather than core scope for the first version.
- Retry policy on `failed` runs (automatic retry with backoff vs. surfacing the failure and waiting for the next scheduled occurrence) needs a decision — leaning toward no automatic retry initially, since a failed unattended run retrying repeatedly without visibility could compound a problem (e.g. repeatedly opening PRs) rather than just reporting it.
