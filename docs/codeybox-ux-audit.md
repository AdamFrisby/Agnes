# CodeyBox tab — UX audit

Method: `super-ux-audit`. Scope: the nine panels of the CodeyBox plugin tab. Depth: standard —
information architecture, hierarchy, and the queue journey; excludes accessibility conformance testing
and responsive behaviour, which need a device matrix this audit did not have.

## Evidence and limitations

**Observed** — the rendered UI, the view source, and the live orchestrator on this host.
**Inferred** — task frequency, from what the live instance actually contains.
**Unknown** — there is no user research, analytics, or support data for this tab. No completion rates,
task times, or error frequencies are claimed anywhere below, because none exist.

Live instance, at audit time:

| surface | volume |
| --- | --- |
| work items | 404 (10 queued, 0 running, queue paused) |
| suggestions | 162 |
| projects | 4 |
| releases · plugins · e2e runs · test cases | 0 |
| supervision | disabled |

## Users and top tasks

One operator supervising an autonomous fleet, checking in rather than sitting in the tool. Ranked by
frequency and consequence, *inferred from the volumes above*:

1. See what is running, queued, or stuck — highest frequency.
2. Unblock an agent: answer a question, retry a failure — highest consequence, time-sensitive.
3. Watch a running agent's output.
4. Triage suggestions — 162 outstanding, so periodic and high-volume.
5. Queue new work.
6. Check fleet capacity and spend.
7. Releases, testing, setup, diagnostics — rare, and empty on this instance.

## Findings

### F1 — Navigation and actions are the same control (severity 3)

**Observed.** The header holds twelve identical buttons: nine destinations, then *Pause queue*, *New*,
*Refresh*. Nothing marks which section is current — a search for a selected/checked state on those
buttons returns zero.

Violates Nielsen 1 (visibility of system status) and 4 (consistency), Norman's signifiers, and the
current-location requirement in IA. *Pause queue* stops the fleet; *Fleet* changes a view; they are
rendered identically and adjacent.

**Recommendation.** Move destinations into a persistent left rail carrying a selected state; leave only
actions in the header, and make the current section's name the pane's title.

### F2 — Nine destinations, flat, with no weighting by frequency (severity 3)

**Observed/inferred.** Queue (404 items) sits beside Setup (empty, and used at most once) at equal
prominence. Prominence should track importance; here it tracks nothing.

**Recommendation.** Group the rail: **Work** (Queue, Suggestions), **Fleet** (Fleet, Supervision),
**Admin** (Releases, Projects, Testing, Setup, Diagnostics). Everyday work then sits above
administration rather than beside it.

### F3 — Irreversible actions are styled as routine and fire immediately (severity 4)

**Observed.** *Cancel*, *Abandon*, *Delete test case*, *Delete attachment*, *Dispose sandbox* and
*Abandon release* all execute on a single click, with the same appearance as *Retry* and *Promote*. A
search for any confirmation step returns zero.

*Abandon* sits at the end of a row of eight near-identical buttons, one position from *Uncancel*.
Violates Nielsen 5 (error prevention) and 3 (user control), and the irreversibility rule in the severity
model. This is the only severity-4 finding: it risks losing work rather than merely wasting time.

**Recommendation.** Separate destructive actions visually, and require a second, explicitly-labelled
confirmation naming both the action and the item.

### F4 — The item action row is an undifferentiated bank of eight (severity 3)

**Observed.** Retry, Promote, Cancel, Replay, Resume, Recover, Uncancel, Abandon — equal weight, no
grouping, and shown regardless of the item's state. *Uncancel* appears for an item that was never
cancelled.

**Recommendation.** Promote the two ordinary actions, collapse the rest behind an overflow, and separate
the destructive pair.

### F5 — The row-detail overlay cannot be closed and does not say what it belongs to (severity 2)

**Observed.** Opening a release, project or suggestion raises a 220px panel over the bottom of the pane
with no close control and no title. Violates Nielsen 3: there is no marked exit.

### F6 — Diagnostics is twenty JSON blobs in one scroll (severity 2)

**Observed.** Every diagnostic surface concatenated into a single text block. Nothing is scannable and
nothing is more important than anything else.

### F7 — Expert escape hatches sit at top level (severity 2)

**Observed.** Testing and Setup are paste-JSON panels, correct for their purpose, but presented as peers
of Queue. They belong in the deferred band.

## Strengths to preserve

- **Questions above the transcript.** An agent blocked on a person is genuinely P0, and it is placed as
  P0. Do not move it into a section.
- **Unconfigured is a first-class state**, not an error — correct for a machine with no CodeyBox.
- **"Unavailable on this instance"** rather than a failure, for surfaces the orchestrator has switched
  off. Honest about what is absent.
- **Tail-then-follow** for agent output: the pane is never blank for a long-running item.

## Hierarchy model applied

| band | content |
| --- | --- |
| P0 | current section; open questions; queue state (paused/running); item status |
| P1 | the work-item list; selected item's output; primary actions (Retry, Promote) |
| P2 | secondary lifecycle actions; fleet and suggestion detail; item detail pane |
| P3 | testing, setup, diagnostics, raw JSON entry |

## Not audited

Accessibility conformance (needs keyboard and screen-reader passes on a real display), responsive
behaviour (the tab is desktop-only today), and any claim about task success — no usability testing has
been run, and none is implied here.
