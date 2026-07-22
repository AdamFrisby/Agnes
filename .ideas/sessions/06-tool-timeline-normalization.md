# Tool timeline normalization

| | |
|---|---|
| **Category** | Sessions |
| **Plugin surface** | Formalizes an existing pattern in `SessionEvent`'s tool-call kinds into an explicit per-adapter mapping contract |
| **Priority** | P1 — directly unlocks broader provider support from `../providers/01-provider-breadth-acp-catalog.md` |
| **Rough effort** | M |

## Background

Every coding-agent CLI Agnes runs describes tool calls differently at the wire level — different field names, different vocabularies for "this was a file edit" vs "this was a shell command," different levels of structure. If Agnes rendered each CLI's tool calls using that CLI's own vocabulary, every client UI (diff viewer, bash-output card, file-read card, etc.) would need to understand every provider's dialect, and a new provider would mean new rendering logic throughout the client rather than just a new adapter. Since Agnes's whole architecture is built around running many different agent CLIs behind one uniform session model, tool-call normalization — translating each provider's native vocabulary into one small, shared set of categories the UI actually renders against — is not optional polish, it's what makes "any agent CLI, one UI" true in practice rather than just in the pitch.

There's a second reason this matters now specifically: not every agent CLI Agnes will eventually support speaks a structured protocol at all. Some can only be driven through their plain terminal output, with no machine-readable tool-call events whatsoever — in that case, "normalizing" a tool call means pattern-matching it out of unstructured text rather than reading a field. A normalization contract needs to be defined precisely enough that both kinds of adapter — structured-protocol and text-pattern-matched — can implement it the same way from the UI's point of view.

## Current state in Agnes

Agnes already normalizes tool calls today — this isn't a from-scratch feature, it's formalizing something that already exists in three places. `SessionEvent.ToolCallEvent` and `ToolCallUpdateEvent` (`/work/src/Agnes.Abstractions/SessionEvent.cs`) already carry a canonical `ToolKind` enum (`Read`, `Edit`, `Delete`, `Move`, `Search`, `Execute`, `Think`, `Fetch`, `Other`), and every current adapter already maps its own provider's tool-call vocabulary onto it: `Agnes.Acp/AcpMap.ToToolKind` for ACP-native providers, `Agnes.Agents.Codex/CodexMap.ToolKindFor`, and `Agnes.Agents.Native/ClaudeCodeStreamMapper.ToKind` for the native Claude Code stream.

The actual gap is that these three mappings are each their own private `switch` statement, following the same shape by convention rather than by any shared contract. Nothing enforces that a fourth adapter maps consistently, there's no shared test surface to check a new mapping against, and there's no defined seam yet for an adapter that has no structured field to switch on at all — a provider reached only through raw terminal output, where a tool call has to be recognized from patterns in that output. That last case matters concretely for `../providers/01-provider-breadth-acp-catalog.md`, which is expected to bring in providers with exactly that shape.

## Proposed design

Turn the existing convention into an explicit, testable contract, so a new adapter — structured or not — has a clear seam to implement against instead of writing its own ad hoc switch statement:

```csharp
// Agnes.Abstractions — the existing canonical vocabulary, made an explicit mapping target
public sealed record NormalizedToolCall(ToolKind Kind, string RawToolName, string? Summary, string? DetailPayload);

/// <summary>Maps a provider's native tool-call representation onto the canonical ToolKind vocabulary.
/// Structured-protocol adapters implement this over their own message fields (as Agnes.Acp, the
/// Codex adapter, and the native Claude Code mapper already do informally); an adapter for a
/// provider with no structured protocol implements it by pattern-matching raw output instead.</summary>
public interface IToolCallNormalizer
{
    NormalizedToolCall Normalize(RawToolCall raw);
}
```

Each existing adapter's mapping (`AcpMap.ToToolKind`, `CodexMap.ToolKindFor`, `ClaudeCodeStreamMapper.ToKind`) becomes a concrete `IToolCallNormalizer` implementation instead of a private static method — a mechanical refactor, not a behavior change, since the target vocabulary (`ToolKind`) already exists and is already correct. Making the contract explicit is what buys the actual value: a shared golden-test harness can assert "given this raw tool call, this `ToolKind` comes out" per adapter, and a future adapter for a provider with no structured protocol (needed for `../providers/01-provider-breadth-acp-catalog.md`'s harder-to-reach providers) has a documented interface to implement by pattern-matching output rather than needing to reverse-engineer the convention from three existing switch statements.

Client-side rendering detail — how much of a normalized tool call's payload to show (a one-line title vs. a full expandable detail, with per-tool-kind overrides) — is a `Agnes.Ui.Core` transcript-rendering concern layered on top of `NormalizedToolCall`, and needs no host or protocol change: the host already sends enough (`Summary` and `DetailPayload`, both optional) for a client to choose how much of it to show. A raw/debug view of the original `RawToolCall` payload is worth keeping available in the client for cases where normalization loses something a user actually needed to see — it's cheap to keep the raw payload alongside the normalized one rather than discarding it at the mapping boundary.

## Acceptance criteria

- Given any of Agnes's current adapters, the tool-call kinds it reports are identical before and after this refactor — this is a contract formalization, not a behavior change, and existing rendering must not regress.
- A new `IToolCallNormalizer` implementation can be added and unit-tested against a golden set of raw-input → `ToolKind` cases without needing a live agent CLI running.
- Given a hypothetical adapter with no structured protocol (raw terminal output only), the same `IToolCallNormalizer` interface is implementable by pattern-matching text, with no changes needed to `Agnes.Abstractions` or the client-rendering path.
- A tool call whose raw payload doesn't cleanly match any specific `ToolKind` still normalizes successfully to `Other` rather than being dropped or causing an error.
- The client can still access a tool call's original, unnormalized payload (for a raw/debug view) even after normalization has happened.
- Per-tool-kind detail-level rendering preferences (e.g. summary vs. full detail) can be changed on the client without requiring any host-side change or redeploy.

## Open questions

- Is the existing fixed `ToolKind` enum future-proof enough as the shared vocabulary, or should Agnes move toward an open, string-based taxonomy with a small well-known set — avoiding a core-library change every time a new kind of tool call shows up? Worth deciding before `../providers/01-provider-breadth-acp-catalog.md` adds enough provider diversity that the enum starts feeling cramped.
