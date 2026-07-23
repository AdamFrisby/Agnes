using System.Text;
using Agnes.Abstractions;
using Agnes.Client;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>Where the terminal panel is docked in a session's workspace (platform/03). Persisted per session
/// id, so returning to a session restores where the user left its terminal.</summary>
public enum TerminalDockLocation
{
    Bottom,
    Sidebar,
    Details,
}

/// <summary>
/// The framework-agnostic view model behind the embedded terminal panel (platform/03). It is a pure
/// projection over the session's event stream plus a thin command surface over <see cref="IAgnesHost"/>:
/// <list type="bullet">
/// <item>It subscribes to the session's <see cref="TerminalOutputEvent"/>s and exposes the decoded output in
/// order (the same interleaved log every other client sees — no bespoke channel), raising
/// <see cref="OutputAppended"/> per chunk so a renderer can feed a terminal control incrementally.</item>
/// <item>User keystrokes/paste go out via <see cref="SendInputAsync"/> → <c>WriteTerminal</c>; a size change
/// via <see cref="ResizeAsync"/> → <c>ResizeTerminal</c>.</item>
/// <item>It tracks the active terminal id — learned from <see cref="OpenAsync"/> or adopted from the first
/// output event, so a terminal opened by another client (or the login flow) is picked up too.</item>
/// </list>
/// Moving the panel (<see cref="DockLocation"/>) or hiding it (<see cref="IsVisible"/>) never touches the
/// terminal id or the stream, so the live PTY and its scrollback persist across a dock move unchanged. All
/// Avalonia/control glue lives in the desktop view; this holds the testable logic.
/// </summary>
public sealed class TerminalPanelViewModel : ObservableObject
{
    private readonly IAgnesHost _host;
    private readonly SessionView _session;
    private readonly IUiDispatcher _dispatcher;
    private readonly StringBuilder _output = new();

    private string? _terminalId;
    private TerminalDockLocation _dockLocation = TerminalDockLocation.Bottom;
    private bool _isVisible;
    private int _columns = 120;
    private int _rows = 30;

    public TerminalPanelViewModel(IAgnesHost host, SessionView session, IUiDispatcher? dispatcher = null)
    {
        _host = host;
        _session = session;
        _dispatcher = dispatcher ?? ImmediateDispatcher.Instance;

        // Replay whatever terminal output the snapshot already carried (scrollback restore), then follow live.
        foreach (var @event in session.Events)
        {
            Apply(@event);
        }

        session.EventAppended += OnEvent;
    }

    /// <summary>The session this terminal belongs to (its dock/scrollback state keys off this id).</summary>
    public string SessionId => _session.SessionId;

    /// <summary>The active fallback terminal id, or null before one is opened/adopted.</summary>
    public string? ActiveTerminalId => _terminalId;

    /// <summary>Whether a terminal is currently attached (drives the "open a terminal" affordance).</summary>
    public bool HasTerminal => _terminalId is not null;

    /// <summary>The decoded terminal output so far, in order (a renderer typically feeds
    /// <see cref="OutputAppended"/> incrementally instead of re-reading this whole buffer).</summary>
    public string Output => _output.ToString();

    /// <summary>Raised (outside any lock) with each newly decoded output chunk, in order.</summary>
    public event Action<string>? OutputAppended;

    /// <summary>Where the panel is docked. Purely presentational: changing it does not reconnect the terminal
    /// or lose scrollback (the live session persists across the move).</summary>
    public TerminalDockLocation DockLocation
    {
        get => _dockLocation;
        set => SetProperty(ref _dockLocation, value);
    }

    /// <summary>Whether the panel is shown. On phones the shell hides it when the host can't back a terminal;
    /// toggling it does not tear down the terminal.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    /// <summary>Terminal width in columns (last requested).</summary>
    public int Columns
    {
        get => _columns;
        private set => SetProperty(ref _columns, value);
    }

    /// <summary>Terminal height in rows (last requested).</summary>
    public int Rows
    {
        get => _rows;
        private set => SetProperty(ref _rows, value);
    }

    /// <summary>Opens a CLI-fallback terminal on the host and adopts its id as the active terminal. Output
    /// then arrives as <see cref="TerminalOutputEvent"/>s on the session stream (rendered incrementally).</summary>
    public async Task OpenAsync(string? command = null, IReadOnlyList<string>? arguments = null, string? workingDirectory = null)
    {
        var id = await _host.OpenTerminalAsync(SessionId, command, arguments, workingDirectory, _columns, _rows).ConfigureAwait(false);
        SetActiveTerminal(id);
        IsVisible = true;
    }

    /// <summary>Sends raw input bytes (a keystroke sequence or paste) to the active terminal. No-op with no
    /// active terminal.</summary>
    public Task SendInputAsync(byte[] data)
        => _terminalId is { } id ? _host.WriteTerminalAsync(SessionId, id, data) : Task.CompletedTask;

    /// <summary>Sends UTF-8 text to the active terminal (convenience over <see cref="SendInputAsync"/>).</summary>
    public Task SendTextAsync(string text) => SendInputAsync(Encoding.UTF8.GetBytes(text));

    /// <summary>Reports a new terminal size to the host and remembers it. No-op with no active terminal (the
    /// size is still remembered so a subsequent <see cref="OpenAsync"/> uses it).</summary>
    public async Task ResizeAsync(int columns, int rows)
    {
        Columns = columns;
        Rows = rows;
        if (_terminalId is { } id)
        {
            await _host.ResizeTerminalAsync(SessionId, id, columns, rows).ConfigureAwait(false);
        }
    }

    private void OnEvent(SessionEvent @event) => _dispatcher.Post(() => Apply(@event));

    private void Apply(SessionEvent @event)
    {
        if (@event is not TerminalOutputEvent output)
        {
            return;
        }

        // Adopt the terminal id from the stream, so a terminal opened elsewhere (another client, the login
        // flow) is picked up without a local OpenAsync.
        if (_terminalId is null)
        {
            SetActiveTerminal(output.TerminalId);
        }

        if (!string.Equals(output.TerminalId, _terminalId, StringComparison.Ordinal))
        {
            return; // output for a different terminal in the same session — not this panel's.
        }

        _output.Append(output.Data);
        OnPropertyChanged(nameof(Output));
        OutputAppended?.Invoke(output.Data);
    }

    private void SetActiveTerminal(string id)
    {
        if (string.Equals(_terminalId, id, StringComparison.Ordinal))
        {
            return;
        }

        _terminalId = id;
        OnPropertyChanged(nameof(ActiveTerminalId));
        OnPropertyChanged(nameof(HasTerminal));
    }
}
