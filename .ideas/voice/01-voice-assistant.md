# Voice assistant

| | |
|---|---|
| **Category** | Voice |
| **Plugin surface** | New `IVoiceProvider` (see `../00-plugin-architecture.md`) |
| **Priority** | P2 — high novelty, high effort, not foundational for anything else in this backlog |
| **Rough effort** | XL |

## Background

Agnes lets someone drive a coding agent from a phone or desktop while away from a keyboard — but "away from a keyboard" still currently means typing on a phone screen. Voice control closes that gap: being able to speak a prompt, hear the agent's response summarized, and answer a permission prompt out loud is meaningfully different from typing on mobile, especially for the situations remote access to a coding agent is actually useful for — checking on a long-running task while doing something else, or responding quickly to a permission request without stopping what you're doing to type. This is also the one place in Agnes's design where raw code content could leave the device to a third-party API by design (an external speech provider), so it needs a privacy posture that's conservative by default, not just functional.

## Current state in Agnes

No voice feature exists, and no speech-to-text/text-to-speech dependency of any kind is in the codebase today. There is a latent hook for audio *input to the agent itself*: `AgentCapabilities.PromptAudio` (`Agnes.Abstractions/Agent.cs`) is a capability flag meant to indicate an agent accepts audio content in prompts — but nothing sets it to `true` today, and `ContentBlock` (`Agnes.Abstractions/Content.cs`) has no audio variant (only `TextContent`, `ImageContent`, `ResourceLinkContent`, `DiffContent`). That flag describes a different, narrower thing than this feature anyway: an agent natively consuming raw audio is not the same as a voice *interface* to Agnes, which needs to convert speech to actions before anything reaches an agent. This doc doesn't require adding an `AudioContent` block — voice input is transcribed to text before it ever becomes part of a session.

Agnes's client library already exposes exactly the actions a voice interface needs to drive: `IAgnesHost.PromptAsync`, `SetModeAsync`, and `RespondPermissionAsync` (`Agnes.Client`), the same calls any UI client makes. This matters because it means voice control doesn't need any new server-side capability to *act* — only a new capability to turn speech into a call to something that already exists.

## Proposed design

```csharp
namespace Agnes.Abstractions;

public interface IVoiceProvider
{
    string Id { get; }   // "device" | "openai-compatible" | "realtime-cloud" | "local-neural"

    Task<ISpeechToTextSession> StartListeningAsync(VoiceOptions options, CancellationToken ct = default);
    Task SpeakAsync(string text, VoiceOptions options, CancellationToken ct = default);
}

public interface ISpeechToTextSession : IAsyncDisposable
{
    IAsyncEnumerable<string> Transcripts { get; }   // streamed partial/final transcripts
}
```

### Separate "hearing/speaking" from "deciding what to do"

`IVoiceProvider` is deliberately narrow: it converts speech to text and text to speech, nothing more. The harder question — what should actually happen when the user says "tell it to also add tests" — is a different concern and shouldn't live inside a voice-transport plugin, for the same reason `IAgentAdapter` doesn't decide what a coding agent should do: mixing "how do I get audio in and out" with "what does this input mean" would make every new voice provider re-implement intent handling, and would make the intent logic untestable without a real microphone.

Model the decision-making part as a **hidden Agnes session**: a lightweight controller session whose job is to map a transcript onto calls against the *target* session (the real coding-agent session the user is actually controlling) using the same `IAgnesHost` client calls any other client would use — `PromptAsync` to relay a spoken instruction, `RespondPermissionAsync` to answer a permission prompt by voice, `SetModeAsync` to switch modes. This keeps voice from needing any new server-side authority: from the host's point of view, a voice-driven action looks identical to a human tapping a button, which means the existing permission and access-control logic (including anything built in `../collaboration/02-session-sharing-and-public-links.md`) applies to it automatically, with nothing voice-specific to keep in sync.

### Privacy defaults

Whatever gets sent to an external voice provider is, by construction, leaving the device — that's true of managed cloud speech APIs in a way it isn't for the rest of Agnes, where a self-hosted deployment keeps everything on infrastructure the user controls. The safe default is to summarize session activity for the voice controller's context rather than forwarding it verbatim, and specifically to exclude raw tool-call arguments and file contents/paths from that summary unless the user explicitly opts in per voice provider. This should be a hard default enforced at the point where session context is assembled for the controller, not a setting a provider could quietly ignore — the failure mode (source code or file paths silently sent to a third-party API because a provider didn't check a flag) is exactly the kind of privacy regression that's hard to notice until it's already happened.

### Provider scope: ship the cheap, broadly-useful ones first

Four provider shapes cover the realistic range of how someone might want speech handled, and they don't need to ship together:

- **`Device`** — the operating system's own built-in speech recognition/synthesis. No API key, no network dependency, weakest quality, but it validates the entire plumbing (hidden session, controller, action-mapping) at close to zero integration cost. Worth building first for exactly that reason — it de-risks the rest of the feature before investing in anything else.
- **`OpenAiCompatible`** — a provider that speaks a common HTTP transcription/synthesis API shape, letting it work against many self-hosted or third-party endpoints without Agnes needing a bespoke integration per vendor. High leverage for relatively low, one-time integration cost.
- **`RealtimeCloud`** — a managed, low-latency streaming voice API, for users who want the best responsiveness and are fine with a hosted third-party dependency and its cost/quota implications.
- **`LocalNeural`** — fully local speech models (no cloud dependency at all, strongest privacy story). This is real, separate effort: it means bundling neural-network runtimes into a .NET application shipped across three genuinely different UI targets (desktop, mobile, browser/WASM), which is a materially different packaging problem than adding an HTTP client. Treat this as a later milestone gated on real demand, and spike the packaging question (model size, per-platform runtime availability, WASM feasibility at all) before committing to it as a scoped deliverable — it is easy to underestimate.

## Acceptance criteria

- **AC1 — A spoken prompt reaches the target session.** Given a listening voice session and a target coding session, when the user speaks an instruction, then the transcribed text is delivered to the target session via the same path a typed prompt would use (`PromptAsync`), and the agent's response is available to be spoken back.
- **AC2 — A permission prompt can be answered by voice.** Given the target session raises a permission request, when the user speaks a recognized response (e.g. "yes, allow it"), then `RespondPermissionAsync` is called with the corresponding option — verified for both an affirmative and a negative response.
- **AC3 — Privacy defaults hold without configuration.** Given no voice-provider-specific privacy setting has been changed, when session context is summarized for the voice controller, then raw tool-call arguments and file paths are absent from what is sent to the configured `IVoiceProvider` — verified by inspecting the actual payload sent to the provider, not just the intended summary.
- **AC4 — Access control isn't bypassed by voice.** Given a collaborator has view-only access to a session (per `../collaboration/02-session-sharing-and-public-links.md`, if that feature has landed) or no access at all, when they attempt a voice-driven prompt or permission response against that session, then the underlying `IAgnesHost` call is rejected exactly as it would be for a non-voice client — voice introduces no new authority.
- **AC5 (edge case) — Unrecognized or ambiguous speech doesn't silently act.** Given a transcript the controller cannot map to a known action with reasonable confidence, when that transcript is processed, then no action is taken against the target session and the user is given some signal that the input wasn't understood, rather than the controller guessing and sending an unintended prompt or permission response.
- **AC6 (non-regression) — Voice is fully optional.** An Agnes deployment with no `IVoiceProvider` configured starts and operates identically to today, with voice-related UI hidden rather than shown-but-broken (per the capability-negotiation model in `../00-plugin-architecture.md`).
- **AC7 — Switching providers doesn't change controller behavior.** The same spoken instruction, run through `Device` and through `OpenAiCompatible` (or any two configured providers) with equivalent transcription output, produces the same action against the target session — confirming the controller logic is genuinely provider-independent.

## Open questions

- Is this worth building before the connectivity/security/provider foundations (relay, secure channel, richer provider support) are solid? It depends on none of them architecturally, but it competes for the same engineering time and is the single most novel, highest-effort item in this backlog — a genuine candidate for deliberate deferral rather than default sequencing.
- How much conversational memory should the hidden controller session retain between utterances (e.g. resolving "do that" to a prior instruction) versus treating each transcript as a fresh, independent instruction? Affects both UX quality and how much context accumulates and needs privacy filtering.
- Should `LocalNeural` be scoped as a real v1 candidate at all, or explicitly deferred until there's a concrete spike answering the cross-platform packaging question? Committing to it early without that spike risks a large, hard-to-estimate effort sink.
- What confidence threshold or confirmation pattern should gate voice-driven permission approvals specifically, given a misheard "yes" on a destructive tool call has real consequences on the host machine?
