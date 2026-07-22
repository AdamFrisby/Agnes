# Local memory search over transcripts

| | |
|---|---|
| **Category** | Operations |
| **Plugin surface** | New `IMemoryIndexProvider` (see `../00-plugin-architecture.md`) |
| **Priority** | P2 |
| **Rough effort** | L |

## Background

Every Agnes host already accumulates a durable, ordered log of everything that happened in every session it has ever run — every message, tool call, diff, and plan. Over weeks of use, that log becomes a genuinely useful memory: "what was that fix I made to the auth flow last month," "which session was I in when I decided against using library X." Today there is no way to search across it. A user (or an agent acting on their behalf) can only recall past work by scrolling back through session history one session at a time, which stops being practical once there are more than a handful of sessions.

This matters more for an agent-facing product than a purely human-facing one: coding agents are frequently asked recall-style questions ("have we handled this error before?", "what did we decide about the schema last time?") and, absent a real search tool, an agent will guess from its own training data or the immediate context window rather than actually checking the project's history — producing confident-sounding answers that may have nothing to do with what actually happened in this codebase. Giving agents (and humans) a real search tool over the session log turns "I think we probably did X" into "here's the session where we did X."

## Current state in Agnes

Agnes's `SqliteEventStore` (implementing `IEventStore` in `Agnes.Host.Events`) already stores exactly the corpus this feature would index: every `SessionEvent`, per session, per host, durably and in order. The gap is purely the search/indexing layer on top — there is no full-text or semantic index over that data today, and no way for a client or an agent to query "what did we say about X."

## Proposed design

```csharp
namespace Agnes.Abstractions;

public interface IMemoryIndexProvider
{
    string Id { get; }   // "text-only" | "embeddings"
    Task IndexAsync(SessionEvent evt, CancellationToken ct = default);
    Task<IReadOnlyList<MemorySearchResult>> SearchAsync(string query, MemorySearchOptions options, CancellationToken ct = default);
}

public sealed record MemorySearchResult(string SessionId, long Sequence, string Snippet, double Score);
```

- **Host-local, per-host index — not synced across hosts or centralized on a server.** Agnes's architecture already has each host own the canonical event log for the sessions it runs; a per-host index is a natural extension of that ownership boundary rather than a new one. It also avoids a genuinely hard problem for free: a cross-host or server-side index would mean either shipping session content off the machine that produced it, or building a sync protocol for index data — neither is justified until there's a real use case for searching across hosts, and per-host search already delivers the core value (recall within the work happening on this machine).
- **`TextOnlyMemoryIndexProvider`** ships first: a full-text index built on SQLite's own FTS5 extension. Agnes already depends on `Microsoft.Data.Sqlite` for the event store, so FTS5 is an in-family addition — no new database engine, no new runtime dependency, and it reuses infrastructure the host already manages (backup, file location, lifecycle). This is the cheapest way to deliver "search my session history" and should be the v1 scope on its own.
- **`EmbeddingsMemoryIndexProvider`** is a materially bigger lift, worth treating as a separate milestone rather than bundling into v1: it needs a local embedding-model runtime and a vector index. ONNX Runtime is the natural choice for the model runtime — it's Microsoft-first-party, fits Agnes's existing preference for reputable, low-supply-chain-risk dependencies (see `docs/architecture.md`'s dependency stance), and doesn't require shelling out to an external Python or Ollama process. For the vector index itself, a brute-force cosine-similarity scan over locally-stored vectors is plausible at the scale of a single host's transcripts (thousands to low tens of thousands of chunks) — reaching for a dedicated vector database would be solving a scale problem Agnes doesn't have yet.
- **Backfill on enable**: when a user turns indexing on, they should be able to choose how much history to index immediately — new events only, a recent window (e.g. last 30 days), or the full history — rather than forcing an all-or-nothing choice. Indexing an entire multi-year event log synchronously on first enable would be slow and surprising; giving the user a scoped starting point keeps the initial cost bounded and predictable.
- **Agent-usable via the existing MCP-forwarding path**, not a new tool-injection mechanism: `IMemoryIndexProvider.SearchAsync` should be wrapped as an MCP tool and exposed through the same forwarding path `../extensibility/01-mcp-management.md` already provides for other tools, rather than inventing a second, parallel way for agents to call host capabilities. Agents should be explicitly prompted (in their system context) to use the search tool for recall-style questions and to say plainly when nothing relevant is found, rather than filling the gap with a plausible-sounding guess.
- **Delete-on-disable**: turning the feature off should offer to wipe the local index and any downloaded model files, not just stop updating them — a stale, orphaned index sitting on disk is both a minor privacy liability and dead weight for no benefit once the user has opted out.

## Acceptance criteria

- Given text-only indexing is enabled, when a user searches for a term that appears verbatim in a past session's messages or tool output, then the search returns that session and sequence number with a relevant snippet.
- Given a user enables memory search for the first time, when they choose a backfill scope (new-only / last 30 days / all history), then only events within that scope are indexed, and indexing does not block the UI or the host from handling other work while it runs.
- Given an agent is prompted to use the memory-search tool for a recall-style question, when it calls the tool and gets zero results, then the agent's response reflects that nothing was found rather than fabricating an answer from general knowledge.
- Given embeddings mode is not enabled (text-only only), when a user searches, then results are still returned via FTS5 text matching — embeddings mode is additive, not required for baseline functionality.
- Given a user disables memory search and chooses "delete index," when the operation completes, then the on-disk index files and any downloaded embedding model files are removed.
- Given two different hosts have both indexed similar content, when a client connected to one host searches, then results only ever come from that host's own index — no cross-host result leakage.
- Given the embeddings model has not yet been downloaded, when embeddings mode is first enabled, then the model download happens explicitly (with visible progress/consent) rather than silently in the background on first search.

## Open questions

- Text-only search via SQLite FTS5 is a clear, low-risk v1. Embeddings mode is a meaningfully bigger investment — model packaging across the Avalonia desktop head and the several Uno heads is a real question (similar in shape to the packaging concern raised in `../voice/01-voice-assistant.md` for local neural voice models) — worth scoping as a separate, later milestone rather than bundling both modes into one deliverable.
- Should relevance ranking in embeddings mode blend vector similarity with the existing FTS5 text score, or fully replace it? Blending is likely safer (falls back gracefully to purely-textual relevance when the embedding signal is weak) but needs empirical tuning once there's a real corpus to test against.
- Default embedding model choice (size/quality/latency tradeoff) is an open call — worth offering at least one "balanced default" plus a way to bring a custom local or remote OpenAI-compatible embedding endpoint, rather than hardcoding a single model forever.
