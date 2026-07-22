# Standalone multi-agent IDE shell (scope note, not a build recommendation)

| | |
|---|---|
| **Category** | Platform |
| **Plugin surface** | N/A — this is a product-scope question, not a design-ready feature |
| **Priority** | P3 — included for completeness; not recommended to pursue without an explicit product-direction decision first |
| **Rough effort** | XL |

## Background

It's worth documenting an idea that shows up naturally once a product like Agnes exists, precisely so it can be *considered and consciously deferred* rather than silently drifted into: building a full, standalone code-editing desktop application — a real IDE, with its own file tree, rich text/code editing, integrated terminal panes, and multi-pane layout — where "connect to a remote Agnes host and drive a coding agent" is just *one panel or plugin* among several, alongside things like direct local editing, direct (non-Agnes) API calls to a model provider, or other tool integrations.

This is a fundamentally different product than "a remote interface to coding-agent CLIs." It's the difference between being the best way to check on and steer an agent session from anywhere, versus competing directly with full-featured code editors on their own turf (file editing, syntax highlighting, git integration, extension ecosystems) — a much larger, more contested, and more expensive space to build in. The temptation to drift toward it is real and worth naming explicitly: as more session-adjacent features accumulate (a file browser, an in-app text editor, an embedded terminal, git operations, review comments), each individually reasonable addition nudges the product incrementally closer to "IDE," and it's easy to arrive at that scope by accretion without ever deciding to.

## Current state in Agnes

Agnes today is architecturally aligned with "remote interface," not "IDE": the host runs agent CLIs, clients render normalized session events, and every client-side feature in this backlog (file browser, embedded terminal, git panel) is scoped to *supporting a session*, not to being a general-purpose editor usable independent of one.

## Why this doc doesn't propose a design

Unlike every other file in this backlog, this one isn't offering an interface sketch or acceptance criteria for something to build — a standalone IDE is large enough, and different enough in kind from the rest of Agnes, that sketching an implementation here would imply a scope decision that hasn't actually been made. The right output of this doc is a clearly named option and the tradeoff, not a spec.

**The case against building it (default recommendation):** it's a different product with different competitors, a much larger surface to design, build, and maintain, and it would compete for the same engineering time as everything else in this backlog that stays within Agnes's current, more focused identity. A small team is generally better served doing one thing extremely well than splitting attention across two large products.

**The case for keeping the option open:** if session-adjacent client features keep growing richer (as several docs in this backlog propose — file browser, in-app editor, embedded terminal, git panel, review comments), the *marginal* cost of eventually packaging that same functionality as a standalone, agent-integrated editor shrinks over time, since most of the hard parts would already exist as session-scoped features. Nothing in this backlog needs to be built differently *today* to keep that option available later — it falls out naturally if a decision is ever made to pursue it.

## Recommendation

Don't build this. Revisit only if there's a clear, explicit product decision to expand Agnes's scope from "remote interface to coding-agent CLIs" to "code editor," made deliberately rather than arrived at by incrementally adding one more session-adjacent feature at a time. If and when that decision is made, most of the building blocks (file browser, embedded terminal, git integration) will already exist from the rest of this backlog and can likely be repackaged rather than rebuilt from scratch.

## Open questions

- Is there a middle ground worth naming — e.g. exposing Agnes's session-viewing capability as an extension/plugin *for* an existing third-party editor, rather than building a new standalone one — that gets some of the "meet developers where they already work" benefit without the cost of building and maintaining an entire IDE? Worth a much smaller, separate exploration if there's real user demand, rather than folding it into this doc's larger question.
