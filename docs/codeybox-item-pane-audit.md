# CodeyBox item pane — UX audit (second pass)

**Scope.** Focused audit of one surface: the right-hand item pane of the Work queue section
(`Views/CodeyBoxQueueView.axaml`, the `Grid` at `Grid.Column="2"`). Adjacent surfaces — the queue list,
the section rail, Fleet/Supervision/Releases/Projects — are **not** in scope and are not assessed here.
Accessibility conformance, keyboard-only operation and responsive behaviour at narrow widths were **not
tested**; they remain unknown.

**Personas.** Two, as directed: a **software engineer** working the queue, and a **product/delivery
manager** watching it. They ask different first questions of the same object, which is what makes this
pane hard: the engineer asks *what is it doing and why did it stop*; the manager asks *is it moving, what
has it cost, and did it ship*.

**Evidence.** Live orchestrator on this host, read through the API: **404 work items** across 3 projects
and 6 agents. Every claim below labelled *Observed* is counted from that data, not estimated.

| Signal | Count | Source |
| --- | ---: | --- |
| Work items | 404 | `GET /workitems` |
| States | Done 322 · Cancelled 50 · Failed 22 · Queued 10 | Observed |
| Agents | claude 205 · codex 127 · opencode 26 · antigravity 24 · cursor 18 · gemini 4 | Observed |
| Items carrying a prompt | 404 / 404 | Observed |
| Prompt length (chars) | min 4 · **median 2,726** · max 10,207 | Observed |
| Items carrying `lastError` | 67 | Observed |
| …of those, state = Cancelled | 45 | Observed |
| Cancellations attributed to an operator | 6 / 50 | Observed |
| `lastError` length (chars) | min 17 · median 17 · max 493 | Observed |
| `failureKind` populated | 22 (other 15 · infrastructure 5 · quota 2) | Observed |
| `quotaRetryAttempts` populated | 57 (range 1–3) | Observed |
| `upstreamPushAttempts` populated | 298 | Observed |
| Items with a merged PR URL | 67 | Observed |
| Items with an external tracker id | 52 | Observed |
| Items with dependencies | 82 | Observed |
| Highest single-item cost | $73.50 | Observed |
| `fallbackHistory` populated | **0** | Observed |
| `auditIterations` populated | **0** | Observed |
| `finalAuditBlockingFindings` populated | **0** | Observed |

The three zeros matter: they are fields the schema offers and this instance never fills, so a pane built
around them would look complete and show nothing. They are modelled but not made structural.

---

## Findings

Severity 0–4 (4 = blocks the task, 0 = cosmetic). Ordered by severity.

### F1 — The task text buries the live output — severity 3

*Observed.* The prompt is rendered in full, inline, in the same scroll region and **above** `Agent
output`. The median prompt is 2,726 characters and the longest is 10,207.

*Consequence.* For a typical item the operator scrolls past roughly 2.7 KB of static text to reach the
only thing on the pane that changes. Both personas' first question — "what is it doing **now**" — is
placed below the pane's least time-sensitive content. This is a regression introduced by the previous
pass, which correctly identified that the task was missing and then gave it more room than any other
element.

*Remedy.* Collapse the task to a short preview with an explicit expander; the live output starts at the
top of the scroll region.

### F2 — The pane cannot answer "why is nothing happening" — severity 3

*Observed.* 57 items carry `quotaRetryAttempts` (1–3), 2 carry a concrete `quotaResetAt`, 298 carry
`upstreamPushAttempts`, and `failureKind` is `quota` on 2. **None of these reach the pane.**

*Consequence.* An item waiting for a provider quota window to reopen is displayed identically to one that
is genuinely wedged. Both personas then take the same wrong action — the engineer retries something that
was already going to retry itself, the manager escalates a non-problem. This is a missing *state*, not a
missing *field*: waiting-and-will-resume is a distinct condition from stopped.

*Remedy.* A waiting line that states the attempt count and, where the orchestrator knows it, the time the
work resumes.

### F3 — "Failure" is announced in the danger hue for items that did not fail — severity 2

*Observed.* The failure box is shown whenever `lastError` is non-empty. 67 items qualify; **45 of them
are Cancelled, not Failed**. Separately, only 6 of the 50 cancellations were attributed to an operator.

*Consequence.* Two different facts wear one label. A cancelled item shows a red box headed "Failure",
which overstates it; and the genuinely useful distinction — *you* cancelled this vs *the orchestrator*
gave up on it — is not drawn at all, though the data to draw it is present. Under the house rule of one
meaning per hue, spending the danger colour on 45 non-failures devalues it on the 22 real ones.

*Remedy.* Title the box from the item's state and cancellation source; reserve the danger hue for actual
failure and use a neutral surface for a cancellation.

### F4 — Provenance is one undifferentiated grey run-on — severity 2

*Observed.* Id, project, agent, priority, cost, branch and PR are concatenated into a single 11px faint
line under the title.

*Consequence.* Every fact has identical visual weight, so both personas linear-scan the same string for
different substrings. The manager's number (cost — up to $73.50 on one item) and the engineer's (branch)
are as prominent as the id, which neither of them wants first. Norman's mapping principle: distinct kinds
of information presented identically force the reader to do the sorting the interface should have done.

*Remedy.* A compact labelled fact row, with the money and the shipped-PR given weight.

### F5 — Item actions and view switches are rendered identically — severity 2

*Observed.* One `WrapPanel` holds Retry, Promote, More…, a separator, then Output, Timeline, Detail — all
plain buttons of the same size and weight.

*Consequence.* Two different control classes are conflated. Three of these *change the item*; three
*change what you are looking at* and change nothing. A control that mutates state and a control that
switches a tab should not be indistinguishable. Compounding it, no single action is dominant, though the
correct next action is usually determined by state (Retry for a failed item, Promote for a queued one).

*Remedy.* Separate the view switch into a segmented control; emphasise the state-appropriate action.

### F6 — The current view is not indicated — severity 2

*Observed.* Output / Timeline / Detail are plain buttons with no pressed or selected state.

*Consequence.* The operator cannot tell which of the three views they are in except by inferring it from
the content. This is also an **internal inconsistency**: the queue filters directly to the left of this
pane, in the same view, already use filled chips to show the active slice. The pane teaches one
convention and then breaks it.

*Remedy.* Reuse the existing chip treatment so the active view is filled.

### F7 — The pane is a dead end — severity 2

*Observed.* 67 items carry `mergedPrUrl` and 52 carry an external tracker id. The PR is rendered as a
**number in a text run**; the tracker id is not rendered at all.

*Consequence.* The manager's closing question is "did it ship, and where is it" — the pane holds the
answer and offers no way to follow it. Every route out of this pane is manual re-entry somewhere else.

*Remedy.* Make the PR and the tracker id open.

### F8 — The failure box says what broke but not where — severity 1

*Observed.* `lastError` has a median length of **17 characters** (e.g. *"Incus inventory entries must
contain a JSON object property named 'config'."*). Which phase or audit gate it happened in lives in the
Timeline view, one click away.

*Consequence.* Minor, and partly by design — the Timeline is where the run-by-run answer belongs, and it
is now one click rather than the three it used to be. But the failing phase is a single string and would
convert the box from "what" to "what and where" at no cost.

*Remedy.* Name the phase of the last failed run in the failure box.

---

## Strengths to preserve

Not everything here needs changing, and these are the parts that were got right:

- **Empty states are explicit and non-blaming.** "Nothing yet.", "Select a work item to see what it is
  doing." Neither leaves the operator wondering whether the pane is broken.
- **Irreversible actions are held apart and confirmed against the named item.** Cancel and Abandon sit
  behind `More…`, wear the destructive class, and arm rather than fire.
- **The dependency box** answers the single commonest reason an item is sitting still, for the 82 items
  where it applies, without the operator asking.
- **The Timeline is sourced from the database, not scraped logs** — the admin UI's approach returns
  nothing at all for every item on this host.

## Not audited

Accessibility (contrast ratios, screen-reader semantics, focus order), keyboard-only operation,
behaviour below ~900px pane width, and localisation. These are unknowns, not passes.

## Validation

Expert inspection predicts problems; it does not confirm them. The claims above that would most benefit
from observation rather than inspection: whether the collapsed task preview (F1) is long enough to be
useful, and whether the waiting state (F2) actually stops the unnecessary retry.
