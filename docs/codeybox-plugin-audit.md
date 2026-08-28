# CodeyBox plugin — deep UX audit (third pass)

**Scope.** The whole plugin tab — all nine sections — weighted heavily toward the Work queue. Comprehensive depth: information
architecture, the developer's task flows, what the orchestrator can actually supply, and where graphics
earn their place. Accessibility, keyboard-only operation and narrow-viewport behaviour are **still not
tested** and remain unknown.

**Persona.** One primary: the **developer running this fleet** — the person who wants to know what the
agents are doing, why work is not landing, and whether the process itself is healthy. A delivery view
(spend, throughput) is secondary and mostly served by the same surfaces.

**Evidence.** The live orchestrator on this host: 404 work items, 4 projects, 6 agents. Read through the
HTTP API, and — for establishing *what exists* rather than what the UI may use — directly from
`~/.codeybox/state.db`. Every count below is measured.

---

## 1. What the orchestrator can actually supply

This has to come first, because most of the findings below are really statements about the gap between
what is held and what is shown. Verified live.

### Reachable over HTTP

| Endpoint | Content | Measured |
| --- | --- | --- |
| `/workitems/{id}/agent-history` | every agent run: phase (incl. 16 distinct audit gates), model, iteration, outcome, duration | 24,038 runs |
| `/workitems/{id}/timings`, `/costs` | duration and spend per phase | 174,976 / 14,421 rows |
| `/workitems/{id}/diff` | real unified diff of the work branch | present |
| `/workitems/{id}/replays`, `/dependents` | lineage | present |
| `/fleet/summary` | per project: queued, in-flight, current phase, **recentOutcomes[5]**, **monthlySpendUsd vs monthlyBudgetUsd**, threshold state | 4 projects |
| `/fleet/transition-health` | overall score, infra-failure rate, **per-stage scores** | present |
| `/workitems/agent-streams/aggregate` | tool-call distribution with counts and median durations | **66,225 tool calls** |
| `/concurrency` | global cap, per-agent caps, currently running, burn estimates | cap 3 |
| `/quota` | per-agent quota probes with billing and model | 6 agents |
| `/projects` | id, display name, **defaultAgent**, defaultBaseBranch, **auditTypes**, **auditMaxIterations** | 4 |
| `/templates` | 20+ named check templates with counts | present |
| `/suggestions` | auditor-raised follow-up work | 163 |

### NOT reachable — verified, not assumed

| Thing | Why | Evidence |
| --- | --- | --- |
| `/quota/history`, `/stats/capacity` | statistics plugin not loaded on this host | HTTP 503 with that exact reason |
| `/workitems/{id}/audit-reports` | the `audit_reports` table is empty | **0 rows**, 0 of 404 items |
| `/workitems/{id}/timeline` | reconstructed by scraping logs that roll daily | empty for every item |
| `/workitems/{id}/budget` | returns nothing on this deployment | empty |
| **Audit finding text** | lives in `work_item_audit_progress` — **1,771 rows**, carrying `blocking_findings_json`, `findings_json`, `iteration`, `max_iterations`, `scheduled_auditors_json` — and **has no HTTP endpoint at all** | table populated; no route reads `IAuditProgressStore` |

That last row is the single most consequential fact in this audit, and it is a **CodeyBox gap, not an
Agnes one**. The orchestrator records exactly what a developer wants — every finding, per iteration, per
auditor — and offers no way to read it back. Correcting my own earlier claim: the plugin was calling the
right route; the store really is empty, and the useful data is in a different table that nothing exposes.

### What that leaves for "why is it not passing"

**Less than it first appears, and this corrects an earlier draft of this audit.** The per-gate signal the
API does expose is `agent_involvement.outcome`, reachable through `/agent-history`. Its complete set of
values across 24,038 runs is:

`success`, `failure:quota`, `failure:agent`, `failure:timeout`, `failure:cancelled`,
`failure:infrastructure`, `failure:transient`, `failure:semantic-incompatible`.

**None of them means "the auditor rejected the work."** That field records whether the auditor's *run
completed*, not what it *decided*. An earlier version of this document read the per-gate failure rates as
audit rejections — "completeness fails 10.5% of the time" — and that was wrong. What those rates actually
measure is how often the agent invoked for that gate died, which is why the six LLM-driven gates show
6–10% and the deterministic ones (semgrep, gitleaks, format-check, mutation-rigor) show 0.0%: the former
call a model that can hit quota or time out, the latter run a local script.

The verdicts do exist. In `work_item_audit_progress`:

| | |
| --- | ---: |
| Audit iterations recorded | 1,771 |
| Iterations that **blocked** | **1,559** |
| Blocking findings recorded | **7,268** |

That is the answer to "why is it not passing", it is complete, and **no HTTP endpoint reads it**. The
finding text, the auditor, the iteration and the severity are all sitting there.

So the honest position for this UI: it can show **gate run reliability** — which is genuinely useful, 512
audit-phase runs here died on provider quota alone — and it must **say that is what it is showing**. It
cannot show audit verdicts at all until CodeyBox exposes that table.

---

## 2. Findings

Severity 0–4. Ordered by severity, then by how often the developer hits them.

### F1 — Nothing anywhere shows that an item is grinding — severity 3

*Observed.* 205 of 404 items have audit iterations. The deepest reached **iteration 52 across 666 agent
runs**; four more exceeded 40 iterations. The project's configured ceiling is **25** (and is raised in
place — the data shows max_iterations of 25, 26, 27, 28 for the same items).

*Consequence.* An item on iteration 3 and an item on iteration 44 are rendered identically — a state
label and a relative timestamp. The single most actionable fact about a work item in this system, "it is
looping and burning money", is not on the screen at all. This is the answer to the user's "do we have
progress bars": **no, and there is enough data for a real one.**

*Remedy.* Iteration depth against the ceiling, in the item pane. Derivable today from `max(iteration)` in
agent-history against the project's `auditMaxIterations`.

**Not on the queue row, deliberately.** Iteration depth is not on `GET /workitems`; it exists only in
per-item history. Drawing it on 404 rows would mean 404 requests to render one list, which is a worse
defect than the one it fixes. The honest options are a bar in the pane now, or an aggregate field added
to the list endpoint on the CodeyBox side later.

### F2 — The timeline cannot summarise the gates at all — severity 3

*Observed.* The Timeline lists agent runs newest-first, flat, with the gate name as free text. For the
worst item that is **666 undifferentiated rows**. Nothing groups by iteration, nothing distinguishes the
six gates that actually fail from the seven that never do, and nothing shows whether it is the *same*
gate failing repeatedly — which is the difference between "nearly there" and "stuck in a loop".

*Consequence.* The user's stated need — "see the failing audits, get a feel for why it's not passing" —
is not met by a list that is technically complete and practically unreadable.

*Remedy.* A gate-first summary: each gate, how many times it was invoked, how many of those invocations
failed to complete, and how the last one went — **labelled as run reliability, not as verdicts**, because
the API cannot supply verdicts. Then the run detail, narrowed by default.

### F3 — The run list does not scale and does not summarise — severity 3

*Observed.* Up to 666 rows rendered flat. Of those, on a typical item the great majority are gates that
passed and will always pass.

*Consequence.* The signal (three failures) is diluted by the noise (hundreds of successes) at equal
visual weight. This is the user's "what is not really relevant" question, and the answer is: **most
individual successful gate runs.** They matter as a count, not as rows.

*Remedy.* Default to failures and the current iteration; everything else behind an explicit "Show all
runs". Counts stay visible so nothing is hidden silently.

### F4 — Creating work exposes 3 of ~20 options — severity 3

*Observed.* The form sends `projectId`, `title`, `prompt`, and optionally `agent`/`baseBranch` — but the
UI only offers the first three. `CreateWorkItemRequest` accepts **20 fields**, including `Priority`,
`DependsOn`, `AuditMaxIterations`, `AuditorProfile`, `AgentClassId`, `MinModelScore`, `ExternalId`,
`WorkTimeoutMinutes`, `RequiredCapabilities`, `IsRefactor`, a `Check` (check-and-act) block, and per-item
`Knobs`.

*Consequence.* Anything beyond the simplest item has to be created elsewhere and then adjusted in the UI,
or not at all. Priority in particular is editable only after creation, in the detail pane.

*Remedy.* Keep the three-field fast path exactly as it is — it is genuinely quick — and put the rest
behind one "More options" disclosure, with selectors rather than free text.

### F5 — Agents and branches are typed, not chosen — severity 2

*Observed.* The project **is** a proper `ComboBox` (this was got right). The agent is not offered at all,
though `/projects` supplies `defaultAgent` and the queue itself proves exactly six agents exist. The
Detail pane asks for a free-text "argument" that must be an attachment id, or a
`target/iteration/auditor` triple, or a stream filename — with no indication of which, and no list.

*Consequence.* The user's question — "do they have to know cryptic IDs?" — is: for the project, no; for
the agent, there is no choice at all; for the detail arguments, yes, and worse, the *format* is undocumented
in the UI.

*Remedy.* Agent and auditor-profile selectors populated from live data; the raw-argument box replaced by
pickers driven by what the item actually has.

### F6 — There are no graphics anywhere, and several would carry real information — severity 2

*Observed.* The tab is entirely text and chips. Meanwhile the API supplies: per-project spend against an
explicit budget; a five-slot recent-outcome history per project; per-stage pipeline health scores; a
tool-call distribution over 66,225 calls; and the gate table above.

*Consequence.* Quantities that are naturally compared — spend against budget, gate against gate, phase
against phase — are rendered as isolated numbers that the reader has to compare mentally.

*Remedy, and the discipline for it.* A graphic earns its place only where the shape is the message:
- **Budget bar** per project — a proportion against a limit is the textbook bar.
- **Gate reliability bars** — sixteen gates ranked by how often their run failed. The shape says "the
  model-driven gates are the fragile ones" in a glance. It does **not** say anything about verdicts, and
  the panel states so.
- **Phase spend bars** — where the time and money went on this item.
- **Outcome sparkline** — the five recent outcomes per project, already an ordered sequence.
- **Iteration progress** — against the ceiling.
Explicitly **not** built: anything from `/quota/history` or `/stats/capacity`, which are 503 on this host.
A chart drawn from absent data would be a lie that looks like a feature.

### F7 — Diagnostics is twenty JSON blobs in one scroll — severity 2

*Observed.* Carried over from the previous audit, still true. Sixty raw bindings in one section.

*Consequence.* It is a debug dump wearing the costume of a screen. It is genuinely useful — it is the
only place several endpoints appear at all — but nothing is ranked, and the two or three that matter
(quota, concurrency, availability) are level with seventeen that do not.

*Remedy.* Promote the few with a real shape; leave the rest as raw, collapsed, and clearly labelled as
raw.

### F8 — The diff is fetched and never shown as a diff — severity 1

*Observed.* `/workitems/{id}/diff` returns a real unified diff. The plugin retrieves it into the Detail
pane as plain text.

*Consequence.* The developer's most familiar artefact is the least legible thing on the screen.

*Remedy.* Colour additions and deletions. Cheap, and it is the one place in this tab where a developer
already knows exactly what they are looking at.

---

## 3. Strengths to preserve

- The project **is** a selector, and it carries the project's default agent as a hint.
- Empty states are explicit everywhere and never blame the reader.
- Irreversible actions are armed and confirmed against a named item.
- The queue's filter/sort/group chips are a good, consistent, learnable pattern — the right thing to
  extend rather than replace.
- The history is sourced from the database rather than the admin UI's log scraping.

## 4. To-do, prioritised

**P0 — the developer's blocked-work loop**
1. Audit-gate run summary per item, honestly labelled. *(F2)*
2. Iteration progress against the project ceiling, in the item pane. *(F1)*
3. Collapse the run list to failures + current iteration, with "Show all runs" and honest counts. *(F3)*

**P1 — process health**
4. ~~Gate failure-rate bars across the queue~~ — **dropped.** It would have charted agent-run
   failures under a label implying audit outcomes. Not worth building on a signal that cannot answer the
   question it appears to answer. *(F2, F6)*
5. Phase spend/duration bars on the item. *(F6)*

**P2 — getting work in**
6. "More options" on create: priority, agent, base branch, depends-on, audit ceiling, auditor profile,
   external id, refactor. *(F4)*
7. Agent and auditor-profile selectors from live data. *(F5)*

**P3 — the rest of the tab**
8. Budget bars and outcome sparklines in Fleet. *(F6)* — done
9. Colour the diff. *(F8)* — done
10. Rank Diagnostics; collapse the raw remainder. *(F7, S4)* — done
11. Counts in the navigation rail, distinguishing "empty" from "off". *(S1)* — done
12. Filter, sort and search the suggestion backlog; show the rationale. *(S2)* — done

**Still open**
13. Arm `Dismiss` on a suggestion the way the queue arms `Cancel`. *(S3)*
14. Supervision, Releases and Testing have no content on this host, so their layouts are unproven. They
    are structurally reviewed above and nothing more; that is a live-verification gap, not a pass.

**Out of scope, and why:** anything needing `/quota/history` or `/stats/capacity` (503 here), and
**anything to do with audit verdicts** — 7,268 blocking findings are recorded and none are reachable over
HTTP. That is the single highest-value change available to this UI and it has to happen in CodeyBox
first: one endpoint over `IAuditProgressStore` would unlock it.

---

## 5. The other eight sections

The queue is where the work is, but it is one of nine destinations. Audited here after the fact, because
the first pass of this document covered the queue and then excluded the rest under "not audited" — which
is most of the tab.

### The state of each, measured

| Section | On this instance | Verdict |
| --- | --- | --- |
| Queue | 404 items | The bulk of this audit |
| **Suggestions** | **162 open** — 13 important, 104 notable, 45 minor; 6 categories; 30 tiny-effort | Richest under-exploited surface |
| Fleet | 4 projects, budgets and recent outcomes | Real content |
| Projects | 4, with agent/branch/audit config | Real content |
| **Supervision** | `enabled: false`, 0 sessions | **Feature off at the orchestrator** |
| **Releases** | **0 releases**, 20+ templates | **Half empty** |
| **Testing** | **0 test cases, 0 e2e runs** | **Entirely empty** |
| Setup / Diagnostics | 0 workers, 0 plugins, some 404s | Partly empty |

### S1 — The rail sells nine equal destinations, four with nothing behind them — severity 3

*Observed.* Supervision is switched off, Testing has no test cases and no e2e runs, Releases has no
releases, and the worker and plugin lists are empty. All nine rail entries look identical.

*Consequence.* The operator pays a click and a network round trip to discover "nothing here", and pays it
again next week because there was nothing to remember it by. Hick's law in the wrong direction: nine
choices, four of which cannot repay the attention.

*Remedy.* A count in the rail, populated as each section loads. "empty" and "off" are kept distinct —
one may fill tomorrow, the other needs an orchestrator setting changed.

### S2 — 162 suggestions with no filter, sort, or search — severity 3

*Observed.* The backlog rendered as one flat list of 162 rows: title, then `severity · category · age` in
grey, then three buttons. Every one is `open` and all belong to one project, so the only distinctions that
exist are severity (13 / 104 / 45), category (6 values) and effort (30 tiny … 4 large) — **none of which
were filterable, sortable or searchable**. The `rationale` — up to 1,462 characters explaining why the
auditor raised it — was not shown at all, nor were the referenced files.

*Consequence.* This is the "future work" surface, and finding the 13 important items among 162, or the 30
cheap ones, meant reading all of them. The single most useful field was invisible.

*Remedy.* The queue's own chips — Important / Quick wins / All, sort by severity / effort / newest, a
category picker built from the data, and a search that reaches the rationale and the file list. Severity
becomes a mark rather than a word among words. Deliberately the *same* vocabulary as the queue: the
operator learns it once in the section they use most, and it should keep working elsewhere.

### S3 — Dismiss was unguarded while the queue arms every destructive action — severity 2

*Observed.* `Cancel` and `Abandon` on a work item arm a named confirmation. `Dismiss` on a suggestion
fired immediately.

*Consequence.* An inconsistent safety model is worse than either a strict or a loose one, because the
operator cannot form a rule. Dismiss now at least carries the destructive styling; arming it is a smaller
follow-up.

### S4 — Diagnostics: twenty JSON blobs, flagged twice, carried twice — severity 2

*Observed.* Sixty raw bindings in one scroll, unranked.

*Consequence.* The section is opened for one question — "why is nothing dispatching" — and its answer sits
in the eleventh blob. 512 audit-phase runs on this instance died on provider quota; the quota probe
reports per-agent headroom (`availablePct`, one agent at 55%) and it was buried.

*Remedy.* Promote dispatch capacity and per-agent quota headroom, lowest first, as bars. Everything else
stays raw, labelled raw, and collapsed. Probes that reported no number are omitted rather than drawn at
0%, which would read as exhausted rather than unmeasured.

---

## 6. Not audited

Accessibility (contrast, screen-reader semantics, focus order), keyboard-only operation, behaviour below
~900px, and localisation — across every section.

**Unproven rather than unexamined:** Supervision, Releases and Testing hold no data on this host, so their
layouts have never rendered with content. Their information architecture is assessed above; their
behaviour under real content is not, and no claim is made about it.
