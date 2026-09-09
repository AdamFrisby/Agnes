using Agnes.Host.Display;
using Agnes.Sandbox;

namespace Agnes.Host.Mcp;

/// <summary>
/// The real <see cref="IAgnesDisplayBackend"/>: every call goes through the session's one
/// <see cref="DisplayBroker"/>, which is the same object a watching person is served from. That is what makes
/// "what the agent sees" and "what the user sees" the same picture rather than two captures that agree most
/// of the time.
/// </summary>
public sealed class BrokerDisplayBackend : IAgnesDisplayBackend
{
    private readonly DisplayBrokerRegistry _brokers;

    public BrokerDisplayBackend(DisplayBrokerRegistry brokers, DisplayOptions options)
    {
        _brokers = brokers;
        Options = options;
    }

    public DisplayOptions Options { get; }

    public bool HasDisplay(string sessionId) => _brokers.HasDisplay(sessionId);

    public async Task<GraphicalDisplay?> GeometryAsync(string sessionId, CancellationToken cancellationToken = default)
        => HasDisplay(sessionId) ? (await BrokerAsync(sessionId, cancellationToken).ConfigureAwait(false)).Display : null;

    public async Task<DisplayShot> ScreenshotAsync(string sessionId, int? maxWidth, CancellationToken cancellationToken = default)
    {
        var broker = await BrokerAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var jpeg = await broker.ScreenshotJpegAsync(maxWidth, Options.JpegQuality, cancellationToken).ConfigureAwait(false);
        var (width, height) = DisplayJpeg.Fit(broker.Geometry.Width, broker.Geometry.Height, maxWidth);
        return new DisplayShot(jpeg, width, height, broker.Geometry.Width, broker.Geometry.Height);
    }

    public async Task<DisplayContactSheet> ContactSheetAsync(
        string sessionId, int count, TimeSpan span, int? maxWidth, CancellationToken cancellationToken = default)
    {
        var broker = await BrokerAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var frames = await broker.BurstJpegAsync(count, span, maxWidth, Options.JpegQuality, cancellationToken).ConfigureAwait(false);
        var sheet = DisplayJpeg.ContactSheet(frames, Options.JpegQuality);
        return new DisplayContactSheet(
            sheet.Jpeg, sheet.Columns, sheet.Rows, frames.Count, sheet.CellWidth, sheet.CellHeight,
            (int)span.TotalMilliseconds, broker.Geometry.Width, broker.Geometry.Height);
    }

    public async Task InjectAsync(string sessionId, IReadOnlyList<DisplayInput> inputs, CancellationToken cancellationToken = default)
    {
        var broker = await BrokerAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await broker.InjectAgentAsync(inputs, cancellationToken).ConfigureAwait(false);
    }

    public async Task TypeAsync(string sessionId, string text, CancellationToken cancellationToken = default)
    {
        // Expanded first, so text that cannot be typed is refused before a capture connection is opened for
        // it; charged by characters, because the guest sees key events but the budget counts keystrokes.
        var inputs = TypedText.ToInputs(text);
        var broker = await BrokerAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await broker.InjectAgentTypedAsync(inputs, text.Length, cancellationToken).ConfigureAwait(false);
    }

    public async Task PressChordAsync(string sessionId, KeyChord chord, int count, CancellationToken cancellationToken = default)
    {
        var broker = await BrokerAsync(sessionId, cancellationToken).ConfigureAwait(false);
        broker.Arbiter.CheckAgentChord(chord);
        await broker.InjectAgentAsync(chord.ToInputs(count), cancellationToken).ConfigureAwait(false);
    }

    public async Task HoldKeyAsync(string sessionId, KeyChord key, TimeSpan hold, CancellationToken cancellationToken = default)
    {
        var broker = await BrokerAsync(sessionId, cancellationToken).ConfigureAwait(false);
        broker.Arbiter.CheckAgentChord(key);
        await broker.InjectAgentHeldAsync(
            [new KeyPress(key.Key, Down: true)], hold, [new KeyPress(key.Key, Down: false)], cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int X, int Y)> PointerPositionAsync(string sessionId, CancellationToken cancellationToken = default)
        => (await BrokerAsync(sessionId, cancellationToken).ConfigureAwait(false)).PointerPosition;

    // An agent tool call is a consumer like any other, so it opens the broker if it is the first one — an
    // autonomous session with nobody watching still gets a screen.
    private Task<DisplayBroker> BrokerAsync(string sessionId, CancellationToken cancellationToken)
        => _brokers.GetOrCreateAsync(sessionId, cancellationToken);
}
