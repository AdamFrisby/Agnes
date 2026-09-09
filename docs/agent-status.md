# The agent's one-line status

A person running one agent reads its transcript. A person running twelve reads none of them. What they
actually want from each is a sentence — *"the migration fails on the unique index; I'm rewriting the
backfill to run in batches, which is step 3 of the plan"* — and they want it without opening anything.

Three ways to get that sentence are worse than this one:

- **Recaps from the CLI.** Claude Code's are historical: they tell you what happened, after it happened, in
  the shape of a summary rather than a status. By the time one exists, the interesting moment has passed.
- **A second, cheap model tailing the first.** It is always late (it reads what the agent already wrote), it
  costs tokens on every session continuously, and it is guessing at intent it cannot see.
- **Inferring from tool calls.** "Ran `pytest`" is not a status. It doesn't say what was found, what is
  being done about it, or how that fits the plan.

The agent already knows the sentence. So it says it: rarely, in one line, through a tool it already has.

## The tool

`report_status` is part of the `agnes` MCP server every session is offered (see
[deployment.md](deployment.md) § *Agnes's own MCP tools*). Its description, as the model reads it:

> Report your one-line status: what you found, what you are doing now, and how it fits the plan. One or two
> sentences, up to 240 characters; the first line only. Call it when you start a new piece of work, when you
> hit a problem, and about every few minutes during long work — not every step.

It takes `status`, and — only when called with a paired **device** token rather than a session token — a
`sessionId`. As with `send_user_file` and the goal tools, an agent's token *is* its identity: a `sessionId`
argument from an agent is ignored, not trusted, so an agent cannot write a status into somebody else's
session.

## The standing nudge

A tool description says "you may". A status that only arrives when asked for is worthless, so Agnes also
says "you are expected to", once, in `AgentStatusNudge.Text`:

> Keep the person informed without their having to read you: every few minutes of work, or when your plan
> changes or you hit a problem, call the agnes report_status tool with one or two sentences under 240
> characters — what you found, what you are doing now, and how it fits the plan.

That one constant reaches a model by two routes, and by no third one:

| Route | Reaches | How |
|---|---|---|
| MCP `ServerInstructions` | every adapter that is handed the `agnes` server | The MCP initialize handshake. Clients (Claude Code, Copilot, Codex, OpenCode) put a server's instructions in the model's context. Set in `AgnesMcpEndpoints.ConfigureServer`. |
| System-prompt append | `claude-code` (ACP), and any future adapter with a `SystemPromptArguments` hook | `AgentSessionOptions.SystemPrompt`, composed by `SessionManager.ComposeSystemPrompt` alongside the prompt library's own additions. |

The system-prompt copy is **gated on the session actually receiving the `agnes` server**. Telling a model to
call a tool it does not have is worse than saying nothing: it tries, fails, and spends a turn deciding what
to do about the failure. `pi` and `antigravity` ship no MCP client at all, so they are never nudged and never
get the tool — no per-adapter mechanism was invented for them, because there is nothing for one to reach.

## What Agnes keeps

`SessionManager.ReportStatusAsync` normalises before it records:

1. **First line only.** A model that answers with a bulleted plan meant its first line as the headline.
2. **Whitespace collapsed**, ends trimmed.
3. **Clipped** to `Agnes:Status:MaxChars` (default 240) at the last word boundary that fits, with an ellipsis
   inside the budget.
4. **Empty is refused** — an empty report is not a status.

**Truncation is never silent.** The tool's return value tells the agent exactly what happened, so the *next*
report is shorter rather than being cut again:

| What happened | What the agent reads back |
|---|---|
| Nothing was taken away | `Noted.` |
| Clipped | `Noted the first 240 characters: "…". Keep future reports to one or two sentences under 240 characters.` |
| Multi-line | `Kept the first line; reports are a single line.` |

The limit is stated in the tool description, in the `status` parameter description, and in the nudge, all
from one constant (`StatusOptions.DefaultMaxChars` / `DefaultMaxCharsText`, asserted equal by a test) — a
model should be able to read the budget off the tool rather than discover it by being clipped. The
acknowledgement quotes the host's *configured* limit, which may differ from that default.

## The rate limit: replace, don't drop

At most one status per `Agnes:Status:MinIntervalSeconds` (default 20) is written to the log. A report
arriving inside that window does **not** fail and is **not** discarded: it *replaces* whatever was pending,
and the pending line is written the moment the window closes.

```
t=0s   "starting the migration"      → written
t=1s   "migration half done"         → held (replaces nothing yet)
t=2s   "migration failed on index"   → held (replaces the t=1s line)
t=20s                                → "migration failed on index" written
```

So the latest line always lands — it is the only one anybody reads — while a chatty agent that reports on
every tool call is coalesced instead of burying its own transcript. The agent is acknowledged immediately
either way; the window is the host's business, not something a model should reason about.

## Where it lives, and who sees it

A status is an `AgentStatusEvent` on the session log, appended on exactly the path every other session fact
takes: persisted, broadcast to every subscribed client, and dispatched on the event spine. That is what makes
it survive a reconnect, replay for a client that opens the session tomorrow, and sit in the transcript next
to the tool calls it describes. It works for a **dormant** session too (the inline persist + broadcast +
spine branch), so nothing is woken to write one line.

Plugins get the usual two hooks:

- `BeforeStatusReportedEvent` — cancelable, with a settable `Status`. An interceptor may **rewrite** it (a
  redactor; a house style) or **veto** it, in which case the reason travels back to the agent as the tool's
  error text. Dispatched for *every* report, including ones the window will coalesce away, so a redactor
  always sees what the agent actually wrote.
- `AgentStatusEvent` itself, observed on the spine after the fact.

`SessionSummary.LatestStatus` / `LatestStatusAt` carry the newest line into the session catalogue, so a
client listing twenty sessions can show what each agent is doing without opening any of them. The host keeps
it cached per session and backfills once from the log for a session restored from the catalogue — a listing
never re-scans.

## Configuration

| Key | Default | Purpose |
|---|---|---|
| `Agnes:Status:MaxChars` | 240 | Longest status line kept. A non-positive value falls back to the default. |
| `Agnes:Status:MinIntervalSeconds` | 20 | Coalescing window. `0` turns coalescing off entirely (every report is written). |

## How clients show it

The line is meant for glances, not for reading: a session row in a list, a tab, a phone's session list, the
header of an open session. It is a *fact with a timestamp*, not a live indicator — an hour-old status on an
idle session is honest, and a client that shows the age alongside is more honest still. It is not a
replacement for `SessionRunState` (working / idle / dormant) or for the approvals count, which say whether
the agent is moving and whether it is blocked; the status says what it is moving *on*.
