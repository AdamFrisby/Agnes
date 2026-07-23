namespace Agnes.Abstractions;

/// <summary>
/// A client-side voice capability: converts speech to text and text to speech, and nothing more. It is
/// deliberately narrow — no intent logic lives here, for the same reason an <see cref="IAgentAdapter"/>
/// doesn't decide what a coding agent should do. Mixing "how do I get audio in and out" with "what does
/// this input mean" would make every new provider re-implement intent handling and make that logic
/// untestable without a real microphone. Deciding what a transcript means is the voice controller's job
/// (in <c>Agnes.Ui.Core</c>), which drives the same <c>IAgnesHost</c> calls a human tapping a button would.
/// </summary>
/// <remarks>
/// This is a client-side plugin-point interface. No real provider ships in core yet: the concrete
/// <c>Device</c> (OS speech), <c>OpenAiCompatible</c> (HTTP transcription/synthesis), <c>RealtimeCloud</c>
/// (managed streaming) and <c>LocalNeural</c> (bundled neural runtimes) shapes from
/// <c>.ideas/voice/01-voice-assistant.md</c> are deliberately deferred — device/cloud engines are a
/// separate integration/packaging effort. The plugin surface and controller are proven with a fake
/// provider in tests instead.
/// </remarks>
public interface IVoiceProvider
{
    /// <summary>Stable id, e.g. <c>device</c> | <c>openai-compatible</c> | <c>realtime-cloud</c> | <c>local-neural</c>.</summary>
    string Id { get; }

    /// <summary>Begins a listening session; the returned session streams partial/final transcripts.</summary>
    Task<ISpeechToTextSession> StartListeningAsync(VoiceOptions options, CancellationToken ct = default);

    /// <summary>Speaks <paramref name="text"/> aloud (agent turn summaries, error signals).</summary>
    Task SpeakAsync(string text, VoiceOptions options, CancellationToken ct = default);
}

/// <summary>A live listening session. Yields transcripts as they are recognized; disposing stops listening.</summary>
public interface ISpeechToTextSession : IAsyncDisposable
{
    /// <summary>Streamed partial/final transcripts, in order. Completes when listening ends.</summary>
    IAsyncEnumerable<string> Transcripts { get; }
}

/// <summary>
/// Per-provider voice configuration. Immutable so it can be shared freely across the controller,
/// summarizer, and provider without a covert mutable channel.
/// </summary>
/// <param name="ProviderId">Which registered <see cref="IVoiceProvider"/> to use.</param>
/// <param name="ForwardRawContext">
/// Privacy opt-in. When false (the default), the controller's context is summarized with raw tool-call
/// arguments and file contents/paths <b>structurally excluded</b> before it can reach a provider — see
/// <c>VoiceContextSummarizer</c>. Only when a user explicitly sets this true per provider may that raw
/// material be forwarded to an external speech API.
/// </param>
/// <param name="Language">BCP-47 language tag for recognition/synthesis, or null for the provider default.</param>
/// <param name="Voice">Provider-specific synthesis voice id, or null for the provider default.</param>
public sealed record VoiceOptions(
    string ProviderId,
    bool ForwardRawContext = false,
    string? Language = null,
    string? Voice = null);
