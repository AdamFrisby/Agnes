using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// A session's display as every head shows it: the latest frame, who is driving, and the input a view
/// maps into guest pixels. Framework-agnostic — a head decodes the JPEG with whatever it draws with and
/// paints it into an image; this holds bytes and state only.
/// </summary>
/// <remarks>
/// Coordinates handed in are already GUEST pixels: the view owns the fit transform (letterbox, zoom,
/// a phone's trackpad model) because only it knows its own size. Frames never touch the event log or
/// the transcript; the agent's screenshots appear there as tool-call cards on their own.
/// </remarks>
public sealed partial class DisplayViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IAgnesHost _host;
    private readonly IUiDispatcher _dispatcher;
    private IDisplayChannel? _channel;
    private CancellationTokenSource? _pump;

    public DisplayViewModel(IAgnesHost host, string sessionId, IUiDispatcher dispatcher)
    {
        _host = host;
        SessionId = sessionId;
        _dispatcher = dispatcher;
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnected);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsConnected);
        TakeControlCommand = new AsyncRelayCommand(() => SendAsync(new DisplayControlRequest(true)), () => IsConnected && !IsUserDriving);
        ReleaseControlCommand = new AsyncRelayCommand(() => SendAsync(new DisplayControlRequest(false)), () => IsConnected && IsUserDriving);
    }

    public string SessionId { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand), nameof(TakeControlCommand), nameof(ReleaseControlCommand))]
    private bool _isConnected;

    /// <summary>Geometry and driver as of the last Info/Control message; null until connected.</summary>
    [ObservableProperty]
    private DisplayInfo? _info;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUserDriving), nameof(IsAgentDriving), nameof(DriverLabel))]
    [NotifyCanExecuteChangedFor(nameof(TakeControlCommand), nameof(ReleaseControlCommand))]
    private DisplayControlHolder _holder;

    /// <summary>The device holding control when a person does; null otherwise.</summary>
    [ObservableProperty]
    private string? _holderDeviceId;

    /// <summary>The most recent image frame. A view repaints the region the header names; a Full frame
    /// replaces the whole picture.</summary>
    [ObservableProperty]
    private DisplayFrame? _latestFrame;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Raised on the UI thread for every image frame, in order. Views that keep a bitmap
    /// subscribe here rather than diffing <see cref="LatestFrame"/>.</summary>
    public event Action<DisplayFrame>? FrameArrived;

    public bool IsUserDriving => Holder == DisplayControlHolder.User;
    public bool IsAgentDriving => Holder == DisplayControlHolder.Agent;
    public int Width => Info?.Width ?? 0;
    public int Height => Info?.Height ?? 0;

    public string DriverLabel => Holder switch
    {
        DisplayControlHolder.Agent => "Agent is driving",
        DisplayControlHolder.User => "You have control",
        _ => "Nobody is driving",
    };

    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand DisconnectCommand { get; }
    public IAsyncRelayCommand TakeControlCommand { get; }
    public IAsyncRelayCommand ReleaseControlCommand { get; }

    // ---- input, in guest pixels ----

    public Task PointerMoveAsync(int x, int y) => SendAsync(new DisplayPointerMove(x, y));
    public Task PointerButtonAsync(int button, bool down) => SendAsync(new DisplayPointerButton(button, down));
    public Task ScrollAsync(int x, int y, int dx, int dy) => SendAsync(new DisplayPointerScroll(x, y, dx, dy));
    public Task KeyAsync(string key, bool down) => SendAsync(new DisplayKey(key, down));

    /// <summary>Tells the host what this view can use, so a phone is never sent a desktop's frames.</summary>
    public Task SetQualityAsync(int maxWidth, int maxFps, int jpegQuality = 75) => SendAsync(new DisplayQuality(maxWidth, maxFps, jpegQuality));

    private async Task ConnectAsync()
    {
        if (IsConnected)
        {
            return;
        }

        try
        {
            _channel = await _host.OpenDisplayAsync(SessionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = $"Couldn't open the display — {ex.Message}");
            return;
        }

        _pump = new CancellationTokenSource();
        _dispatcher.Post(() => { IsConnected = true; Status = string.Empty; });
        _ = PumpAsync(_channel, _pump.Token);
    }

    private async Task PumpAsync(IDisplayChannel channel, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in channel.Frames.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (frame.Header.Kind)
                {
                    case DisplayFrameKind.Info:
                        var info = System.Text.Json.JsonSerializer.Deserialize<DisplayInfo>(frame.Payload.Span, DisplayWire.Json);
                        if (info is not null)
                        {
                            _dispatcher.Post(() => { Info = info; Holder = info.Holder; HolderDeviceId = info.HolderDeviceId; OnPropertyChanged(nameof(Width)); OnPropertyChanged(nameof(Height)); });
                        }

                        break;
                    case DisplayFrameKind.Control:
                        var notice = System.Text.Json.JsonSerializer.Deserialize<DisplayControlNotice>(frame.Payload.Span, DisplayWire.Json);
                        if (notice is not null)
                        {
                            _dispatcher.Post(() => { Holder = notice.Holder; HolderDeviceId = notice.DeviceId; });
                        }

                        break;
                    default:
                        _dispatcher.Post(() => { LatestFrame = frame; FrameArrived?.Invoke(frame); });
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // disconnecting
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => Status = $"Display stream ended — {ex.Message}");
        }
        finally
        {
            _dispatcher.Post(() => IsConnected = false);
        }
    }

    private async Task SendAsync(DisplayClientMessage message)
    {
        if (_channel is { } channel && IsConnected)
        {
            try
            {
                await channel.SendAsync(message).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _dispatcher.Post(() => Status = $"Input not delivered — {ex.Message}");
            }
        }
    }

    private async Task DisconnectAsync()
    {
        _pump?.Cancel();
        if (_channel is { } channel)
        {
            _channel = null;
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        _dispatcher.Post(() => IsConnected = false);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _pump?.Dispose();
    }
}
