# Request: expose stored audit verdicts over HTTP

## What I need

A read-only endpoint that returns the audit **verdicts** already persisted in
`work_item_audit_progress` for a work item. Right now that table is the only place the orchestrator
records *why* work failed its audits, and nothing in the HTTP API reads it.

Suggested shape, mirroring the existing `AuditReportEndpoints`:

```
GET /workitems/{id}/audit-progress                 # every attempt, newest first
GET /workitems/{id}/audit-progress/{attemptId}     # one attempt
```

**Do not key the attempt on its timestamp.** An earlier draft of this request proposed
`?attempt=<ISO-8601>`, taken from the store's `DateTimeOffset? workAttemptStartedAt` parameter. That is
wrong, and section 1 below explains why — the timestamp is not obtainable by a client and is not even
recoverable server-side for historical attempts. The endpoint should enumerate the attempts it holds and
hand back an **opaque, server-assigned id** per attempt.

Returning, per work attempt and iteration:

- `iteration`, `maxIterations`, `status` (`in_progress` | `incomplete` | `complete`)
- `blockingFindings`, `nonBlockingFindings` counts
- `scheduledAuditors`, `completedAuditors`
- `workBranchTip`
- the findings themselves — `auditorName`, `severity`, `title`, `description`, `location`

`AuditProgressRecord` and `AuditProgressFinding` in
`src/CodeyBox.Orchestrator/IAuditProgressStore.cs` already carry exactly this, so the DTOs should be a
straight projection. `AuditSeverity` is an enum (`Info` | `Warning` | `Error`) — please serialise it as a
string, as `audit-reports` does, so clients don't depend on ordinal values.

## Why — this is not a hypothetical gap

Measured against the live instance on this host (`~/.codeybox/state.db`, 404 work items):

| | |
| --- | ---: |
| `work_item_audit_progress` rows | **1,771** |
| work items covered | 180 |
| iterations that **blocked** | **1,559** |
| blocking findings recorded, with title/description/location | **7,268** |
| `audit_reports` rows | **0** |

The endpoint that *does* exist — `GET /workitems/{id}/audit-progress`'s neighbour,
`GET /workitems/{id}/audit-reports` — reads `IAuditReportStore`, and that table is empty for **all 404
items** on this deployment. So the API currently reports "no audit findings" for every work item ever
run, while the orchestrator holds 7,268 of them.

Top blocking auditors across the instance:

| Auditor | Blocking findings |
| --- | ---: |
| `csharp:test-pass` | 1,663 |
| `tests:meaningfulness-review` | 1,657 |
| `architecture:llm-review` | 659 |
| `quality:llm-review` | 646 |
| `completeness:llm-review` | 642 |
| `cheating:llm-review` | 581 |
| `security:llm-review` | 353 |

The findings are specific and immediately actionable, e.g.
`[architecture:llm-review] Durable session handle exposes runtime state — src/CodeyBox.Core/AgentSessions.cs:39`.

## What consumers are doing without it

The Agnes CodeyBox plugin currently shows *gate run reliability* — how often each auditor's run completed
— derived from `agent_involvement.outcome` via `/workitems/{id}/agent-history`. That field's full value
set is `success`, `failure:quota`, `failure:agent`, `failure:timeout`, `failure:cancelled`,
`failure:infrastructure`, `failure:transient`, `failure:semantic-incompatible`. **None of them means "the
auditor rejected the work."** It is a proxy, it is labelled as one in the UI, and it is measurably
misleading if read as a verdict: by run-completion the LLM gates look like the fragile ones, whereas by
actual verdicts `csharp:test-pass` and `tests:meaningfulness-review` block roughly 2.5× more than any LLM
reviewer.

## Two implementation details worth knowing

1. **The work-attempt key is a timestamp used as an identity, and it strands history.** This is the one
   part of this request that is really a bug report rather than a feature ask.

   `GetAuditProgressAsync` filters on `work_attempt_started_at = $attempt`, and callers obtain that value
   from `ResolveCurrentWorkAttemptStartedAtAsync`, which reads `work_item_iterations` where
   `iteration == AuditProgressIterationNumbers.WorkPhase` (= 1) and takes `DispatchedAt`.

   But `work_item_iterations` has primary key `(work_item_id, iteration)`. There is **at most one row per
   item with iteration 1** — verified: zero items in this database have more than one. So that resolver
   returns exactly one value per work item, and its `OrderByDescending(i => i.DispatchedAt)` is dead code
   over a single row.

   Meanwhile `work_item_audit_progress` holds **220 distinct (item, attempt) partitions**, and 35 items
   have between 2 and 4 attempts each. Those extra partitions were written when the iteration-1 row
   carried a *different* `dispatched_at`; the row has since been upserted. Measured:

   | | |
   | --- | ---: |
   | attempt partitions on multi-attempt items | 75 |
   | reachable via `ResolveCurrentWorkAttemptStartedAtAsync` | **35** |
   | **unreachable — no way to derive the key** | **40** |

   So **40 attempt partitions of real audit history are currently orphaned**: the data is in the table,
   and neither a client nor the orchestrator itself can name them. Any endpoint that expects a caller to
   supply the timestamp inherits that, and a caller cannot supply it in any case — it is not exposed
   anywhere in the API.

   Two things follow, and the first is enough to unblock me:

   - **Now:** have the endpoint read the distinct attempt keys out of the table itself and return every
     attempt, newest first, each with an **opaque id** the client passes back for the single-attempt
     route. That needs an "enumerate attempts" (or "get all progress") method on
     `IAuditProgressStore` — the current interface cannot express it. Group attempt → iteration →
     findings, the way `audit-reports` groups target → iteration → auditors.
   - **Properly:** give a work attempt a real identifier. A monotonic attempt number on the work item, or
     a generated id minted when a work attempt starts and carried on the `work_item_audit_progress` row
     alongside the timestamp, would make attempts nameable, stop history being orphaned by an upsert, and
     let `PurgeAuditProgressAsync` target an attempt precisely instead of by timestamp equality. I would
     not block the endpoint on this, but the endpoint's contract should be an opaque id from day one so
     the fix is invisible to clients when it lands.

2. **Descriptions need a two-tier fetch — please build this in from the start.** It is not that the
   payloads are uniformly large; it is that they have a brutal tail, and a list view cannot pay for it.
   Measured over 5,771 findings on this instance:

   | | chars |
   | --- | ---: |
   | median description | **465** |
   | p90 | 796 |
   | max | **80,496** |

   By auditor, the weight is concentrated in the tool-output gates rather than the reviewers:

   | Auditor | n | median | max |
   | --- | ---: | ---: | ---: |
   | `process:required-build` | 20 | 8,871 | 10,095 |
   | `csharp:build-WaE` | 18 | 4,586 | 5,537 |
   | `csharp:format-check` | 34 | 1,874 | **80,496** |
   | `csharp:test-pass` | 325 | 581 | 4,036 |
   | `security:llm-review` | 478 | 577 | 1,790 |
   | `architecture:llm-review` | 843 | 543 | 1,711 |

   (`csharp:format-check` embeds the entire .NET first-run banner — telemetry notice, HTTPS cert message,
   "Write your first app" — ahead of the actual `dotnet format` output. Worth trimming at source too, but
   that is a separate fix and does not remove the need for the tiering.)

   So the useful contract is **truncate by default, fetch in full on demand**, which is exactly what
   `audit-reports` already does with `raw_output` and its `/{target}/{iteration}/{auditor}/raw` companion.
   Mirroring that precedent:

   - The list response returns each finding with `auditorName`, `severity`, `title`, `location`, a
     **truncated** `description`, and — importantly — `descriptionLength` plus `descriptionTruncated`, so
     a client can render "show the rest" only where there *is* a rest, rather than offering it on every
     finding and discovering on click that there was nothing more.
   - A truncation threshold around **800 characters** inlines roughly 90% of findings with no second
     request at all, while capping a pathological item at a few KB instead of ~95 KB. Please make it a
     query parameter (`?maxDescription=`) rather than a constant, with `0` meaning "omit descriptions
     entirely" for a counts-only list view.
   - A per-finding fetch for the full text, e.g.
     `GET /workitems/{id}/audit-progress/{attempt}/{iteration}/{findingId}/description` returning
     `text/plain`, same as the existing `/raw`.

   **The finding ids already exist and are usable for that addressing.**
   `blocking_finding_ids_json` holds ids of the form `f-052d2f9f`, positionally aligned with
   `blocking_findings_json`. One caveat found while checking: in **2 of 400** sampled rows the two arrays
   are *different lengths*, so the alignment is not guaranteed and addressing by array index would be
   subtly wrong on those rows. Please put the id **on the finding object itself** in the response rather
   than leaving clients to zip two parallel arrays — and ideally on `AuditProgressFinding` in the domain
   model, which would remove the failure mode at source.

## What the UI will do with it

Stated so the contract is shaped by a real consumer rather than guessed at:

- The item's Timeline pane lists iterations, and under each, the findings that blocked it — auditor,
  severity, title, location. That view wants **titles and locations only**; at ~500 bytes of description
  per finding it would otherwise pull megabytes to render a summary.
- A finding expands in place to show its description. Where `descriptionTruncated` is false the text is
  already present and expansion costs nothing; where it is true the client fetches the full text at that
  moment and caches it for the session.
- Nothing is ever silently cut: a truncated description shows how much is being withheld
  (`descriptionLength`), the same way the run list already reports "22 of 670 runs".

That is why the flags matter as much as the truncation. A client that cannot tell "short description"
from "truncated description" must either offer a pointless expander on every row or issue a request per
finding to find out — and with 7,268 findings recorded here, the second is not an option.

## Scope

Read-only for the endpoint itself. A new read method on `IAuditProgressStore` is required (see 1).
No write path and no retention change — `work_item_audit_progress` is
explicitly control-plane state and exempt from the diagnostic report retention sweep, which is precisely
why it still has the history that `audit_reports` has lost.

Please also add it to `docs/reference/` alongside the other work-item endpoints, and note in the
`audit-reports` docs that the two are different stores with different retention, since the names are
confusingly close.
