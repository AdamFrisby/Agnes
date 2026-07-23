using Agnes.Abstractions;
using Agnes.Client;

namespace Agnes.Ui.Core.Voice;

/// <summary>
/// The "hidden controller" for voice: it hears transcripts from an <see cref="IVoiceProvider"/>, decides
/// what each one means with the pure <see cref="VoiceIntentMapper"/>, and drives the <b>same</b>
/// <see cref="IAgnesHost"/> calls any UI client makes against a target session — <c>PromptAsync</c>,
/// <c>RespondPermissionAsync</c>, <c>SetModeAsync</c>. It holds no server-side authority: a voice-driven
/// action is indistinguishable from a human tapping a button, so existing permission/access-control applies
/// automatically (AC4). Agent turn completions are spoken back via <c>SpeakAsync</c>, summarized through the
/// privacy-filtering <see cref="VoiceContextSummarizer"/> (AC1/AC3).
/// </summary>
/// <remarks>
/// All collaborators are injected (host, provider, summarizer), so the controller is fully testable with a
/// fake provider and a recording host — no microphone or speaker required.
/// </remarks>
public sealed class VoiceControllerViewModel : IAsyncDisposable
{
    private readonly IAgnesHost _host;
    private readonly IVoiceProvider _provider;
    private readonly VoiceContextSummarizer _summarizer;
    private readonly VoiceOptions _options;
    private readonly string _targetSessionId;
    private readonly object _gate = new();

    private SessionView? _view;
    private ISpeechToTextSession? _session;
    private VoicePermissionState? _openPermission;
    private string? _currentModeId;
    private IReadOnlyList<SessionMode> _modes = [];

    public VoiceControllerViewModel(
        IAgnesHost host,
        IVoiceProvider provider,
        VoiceContextSummarizer summarizer,
        string targetSessionId,
        VoiceOptions options)
    {
        _host = host;
        _provider = provider;
        _summarizer = summarizer;
        _targetSessionId = targetSessionId;
        _options = options;
    }

    /// <summary>Raised for a transcript the controller could not map to any action (AC5): the caller can flash
    /// a "didn't catch that" hint. No action is taken against the target session.</summary>
    public event Action<string>? NotUnderstood;

    /// <summary>Raised after each mapped action is dispatched (for UI feedback/tests).</summary>
    public event Action<VoiceAction>? ActionDispatched;

    /// <summary>The most recent turn-completion read-back, if any is in flight — awaitable for deterministic tests.</summary>
    public Task LastReadbackCompleted { get; private set; } = Task.CompletedTask;

    /// <summary>Subscribes to the target session and initializes controller state from its snapshot. Must be
    /// called before <see cref="ListenAsync"/>.</summary>
    public async Task AttachAsync()
    {
        var view = await _host.SubscribeAsync(_targetSessionId).ConfigureAwait(false);
        lock (_gate)
        {
            _view = view;
            _modes = view.Info?.Modes ?? [];
            _currentModeId = view.Info?.CurrentModeId;
            foreach (var evt in view.Events)
            {
                ApplyStateLocked(evt);
            }
        }

        view.EventAppended += OnEventAppended;
    }

    /// <summary>Starts listening and processes transcripts until the stream completes or is cancelled. Each
    /// transcript is mapped and dispatched in order.</summary>
    public async Task ListenAsync(CancellationToken ct = default)
    {
        if (_view is null)
        {
            await AttachAsync().ConfigureAwait(false);
        }

        var session = await _provider.StartListeningAsync(_options, ct).ConfigureAwait(false);
        lock (_gate) { _session = session; }

        await foreach (var transcript in session.Transcripts.WithCancellation(ct).ConfigureAwait(false))
        {
            await ProcessTranscriptAsync(transcript).ConfigureAwait(false);
        }
    }

    /// <summary>Maps one transcript and dispatches the resulting action against the target session. Exposed so
    /// the mapping-to-host path is directly testable without a listening loop.</summary>
    public async Task<VoiceAction> ProcessTranscriptAsync(string transcript)
    {
        VoiceSessionState state;
        lock (_gate)
        {
            state = new VoiceSessionState(_openPermission, _modes, _currentModeId);
        }

        var action = VoiceIntentMapper.Map(transcript, state);
        switch (action.Kind)
        {
            case VoiceActionKind.Prompt:
                await _host.PromptAsync(_targetSessionId, [new TextContent(action.Text!)]).ConfigureAwait(false);
                break;

            case VoiceActionKind.RespondPermission:
                // Answer via the exact same call a human tapping the permission card would make.
                await _host.RespondPermissionAsync(_targetSessionId, state.OpenPermission!.RequestId, action.OptionId!)
                    .ConfigureAwait(false);
                break;

            case VoiceActionKind.SetMode:
                await _host.SetModeAsync(_targetSessionId, action.ModeId!).ConfigureAwait(false);
                break;

            case VoiceActionKind.None:
            default:
                NotUnderstood?.Invoke(transcript);
                return action;
        }

        ActionDispatched?.Invoke(action);
        return action;
    }

    private void OnEventAppended(SessionEvent evt)
    {
        lock (_gate) { ApplyStateLocked(evt); }

        // Speak a summary when a turn completes. Kept as an observe-only reaction; a failure here must never
        // affect the session, so it is isolated on its own task and swallowed with a note below.
        if (evt is TurnEndedEvent)
        {
            LastReadbackCompleted = SpeakTurnSummaryAsync();
        }
    }

    private async Task SpeakTurnSummaryAsync()
    {
        try
        {
            IReadOnlyList<SessionEvent> events;
            lock (_gate) { events = _view?.Events ?? []; }

            // The ONLY text handed to the provider comes from the privacy-filtering summarizer — a provider
            // can never receive raw file contents/paths when ForwardRawContext is off.
            var context = _summarizer.Summarize(events, _options);
            await _provider.SpeakAsync(context.SpokenSummary, _options).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Read-back is best-effort: a speech failure must not disturb the session (observer isolation).
        }
    }

    private void ApplyStateLocked(SessionEvent evt)
    {
        switch (evt)
        {
            case PermissionRequestedEvent pr:
                _openPermission = new VoicePermissionState(pr.RequestId, pr.Options);
                break;
            case PermissionResolvedEvent rr when _openPermission?.RequestId == rr.RequestId:
                _openPermission = null;
                break;
            case ModeChangedEvent mc:
                _currentModeId = mc.ModeId;
                break;
            default:
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_view is not null)
        {
            _view.EventAppended -= OnEventAppended;
        }

        var session = _session;
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }
}
