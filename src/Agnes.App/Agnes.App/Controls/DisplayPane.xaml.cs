using System.ComponentModel;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Agnes.App.Controls;

/// <summary>
/// The browser's view of a graphical session: the guest's screen, the mouse and keyboard mapped onto it,
/// and who currently holds them.
///
/// Two things shape the implementation. The frames are JPEG, and a browser cannot use
/// <c>BitmapDecoder</c> — so each frame is fed to a <see cref="BitmapImage"/> over an in-memory stream,
/// which is the one decode path that works on WebAssembly. And a tile is a rectangle of the picture, not
/// a whole one, so rather than compositing (which would need pixel access we also don't have on WASM)
/// each tile is drawn as its own positioned <see cref="Image"/> over the last Full frame, and the stack
/// is cleared the moment a Full arrives.
/// </summary>
public sealed partial class DisplayPane : UserControl
{
    /// <summary>How many tiles may stack before we stop drawing more and wait for the next Full frame.
    /// A busy screen tiles continuously, and an unbounded stack of images is a memory leak with a
    /// pleasant name.</summary>
    private const int MaxTiles = 80;

    public static readonly DependencyProperty DisplayProperty = DependencyProperty.Register(
        nameof(Display), typeof(DisplayViewModel), typeof(DisplayPane),
        new PropertyMetadata(null, OnDisplayChanged));

    private readonly List<Image> _tiles = [];
    private DisplayViewModel? _bound;
    private int _guestWidth;
    private int _guestHeight;
    private bool _decoding;
    private bool _active;

    public DisplayPane()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
        Unloaded += (_, _) => Detach();
    }

    public DisplayViewModel? Display
    {
        get => (DisplayViewModel?)GetValue(DisplayProperty);
        set => SetValue(DisplayProperty, value);
    }

    private static void OnDisplayChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        => ((DisplayPane)sender).Rebind(e.NewValue as DisplayViewModel);

    private void Rebind(DisplayViewModel? display)
    {
        Detach();
        _bound = display;
        if (display is null)
        {
            // Today this is also what a session with no graphical sandbox looks like, and what every
            // session looks like until SessionViewModel grows a Display. Say which, rather than sitting
            // on "Connecting…" forever.
            EmptyState.Text = "This session has no screen. Start one with a graphical sandbox to watch it here.";
            return;
        }

        display.FrameArrived += OnFrame;
        display.PropertyChanged += OnDisplayPropertyChanged;
        UpdateChrome();
        if (_active)
        {
            _ = ConnectAsync(display);
        }
    }

    /// <summary>
    /// Opens or closes the stream. Driven by the toggle rather than by binding, because the pane exists
    /// in the tree whether or not anyone asked for it, and a hidden pane quietly streaming JPEG is the
    /// most expensive way to render nothing.
    /// </summary>
    public void SetActive(bool active)
    {
        _active = active;
        if (_bound is not { } display)
        {
            return;
        }

        if (active)
        {
            EmptyState.Text = "Connecting to the screen…";
            _ = ConnectAsync(display);
        }
        else
        {
            display.DisconnectCommand.Execute(null);
        }
    }

    private void Detach()
    {
        if (_bound is null)
        {
            return;
        }

        _bound.FrameArrived -= OnFrame;
        _bound.PropertyChanged -= OnDisplayPropertyChanged;
        _bound.DisconnectCommand.Execute(null);
        _bound = null;
    }

    private async Task ConnectAsync(DisplayViewModel display)
    {
        await display.ConnectCommand.ExecuteAsync(null);
        if (!display.IsConnected)
        {
            // The browser terminates TLS itself, so a host authenticated by a pinned self-signed
            // certificate is unreachable from a tab however good the pin is — the same reason the hub
            // does not connect to one from here. Say so, because "couldn't connect" invites an hour of
            // looking at the host's logs.
            EmptyState.Text =
                "Couldn't open the screen. " + display.Status + "\n\n"
                + "The browser client can only reach hosts with a certificate the browser already "
                + "trusts. A host paired by fingerprint (the usual self-signed setup) works from the "
                + "desktop and phone clients, but not from a tab.";
            return;
        }

        // A browser window is wide and a person is looking at it: full width, and the frame rate a
        // desktop stream is worth.
        await display.SetQualityAsync(1280, 20, 70);
    }

    private void OnDisplayPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DisplayViewModel.Holder) or nameof(DisplayViewModel.Status)
            or nameof(DisplayViewModel.IsConnected))
        {
            DispatcherQueue.TryEnqueue(UpdateChrome);
        }
    }

    private void UpdateChrome()
    {
        var display = _bound;
        DriverLabel.Text = display?.DriverLabel ?? "Not connected";
        StatusLabel.Text = display?.Status ?? string.Empty;
        TakeButton.Visibility = display is { IsUserDriving: false } ? Visibility.Visible : Visibility.Collapsed;
        ReleaseButton.Visibility = display is { IsUserDriving: true } ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnTake(object sender, RoutedEventArgs e)
    {
        _bound?.TakeControlCommand.Execute(null);
        Focus(FocusState.Programmatic);
    }

    private void OnRelease(object sender, RoutedEventArgs e) => _bound?.ReleaseControlCommand.Execute(null);

    // ---- frames ----

    private void OnFrame(DisplayFrame frame)
    {
        if (!frame.IsImage || _decoding)
        {
            // Dropping a frame while the previous one is still decoding is the correct back-pressure:
            // the next Full repaints everything anyway, and queueing would grow without bound.
            return;
        }

        _decoding = true;
        _ = DrawAsync(frame);
    }

    private async Task DrawAsync(DisplayFrame frame)
    {
        try
        {
            var header = frame.Header;
            var width = header.DisplayWidth > 0 ? header.DisplayWidth : _guestWidth;
            var height = header.DisplayHeight > 0 ? header.DisplayHeight : _guestHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var image = await DecodeAsync(frame.Payload);
            if (image is null)
            {
                return;
            }

            if (header.Kind == DisplayFrameKind.Full)
            {
                Resize(width, height);
                ClearTiles();
                FullFrame.Source = image;
                Frame.Visibility = Visibility.Visible;
                EmptyState.Visibility = Visibility.Collapsed;
                return;
            }

            if (FullFrame.Source is null || _tiles.Count >= MaxTiles)
            {
                // A tile before any Full frame is a fragment of a picture we don't have.
                return;
            }

            var tile = new Image
            {
                Source = image,
                Width = header.Width,
                Height = header.Height,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill,
            };
            Canvas.SetLeft(tile, header.X);
            Canvas.SetTop(tile, header.Y);
            Screen.Children.Add(tile);
            _tiles.Add(tile);
        }
        catch (Exception)
        {
            // A corrupt frame is a dropped frame; the stream keeps coming.
        }
        finally
        {
            _decoding = false;
        }
    }

    /// <summary>
    /// JPEG bytes to something a browser can show. <c>BitmapDecoder</c> is not available on WebAssembly,
    /// so this goes through <see cref="BitmapImage.SetSourceAsync"/>, which hands the bytes to the
    /// browser's own image decoder — the same one an &lt;img&gt; tag uses.
    /// </summary>
    private static async Task<BitmapImage?> DecodeAsync(ReadOnlyMemory<byte> payload)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(payload.ToArray());
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        return image;
    }

    private void Resize(int width, int height)
    {
        if (_guestWidth == width && _guestHeight == height)
        {
            return;
        }

        _guestWidth = width;
        _guestHeight = height;
        Screen.Width = width;
        Screen.Height = height;
        FullFrame.Width = width;
        FullFrame.Height = height;
    }

    private void ClearTiles()
    {
        foreach (var tile in _tiles)
        {
            Screen.Children.Remove(tile);
        }

        _tiles.Clear();
    }

    // ---- mouse ----

    private bool Driving => _bound is { IsUserDriving: true };

    /// <summary>A pointer position on the Canvas is already a guest pixel — the Viewbox did the scaling —
    /// so this only has to clamp it to the display.</summary>
    private (int X, int Y) Guest(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Screen).Position;
        return (
            Math.Clamp((int)Math.Round(point.X), 0, Math.Max(0, _guestWidth - 1)),
            Math.Clamp((int)Math.Round(point.Y), 0, Math.Max(0, _guestHeight - 1)));
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!Driving)
        {
            return;
        }

        var (x, y) = Guest(e);
        _ = _bound!.PointerMoveAsync(x, y);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Focus first so the keyboard follows the mouse into the pane, whether or not we are driving.
        Focus(FocusState.Pointer);
        if (!Driving)
        {
            return;
        }

        var (x, y) = Guest(e);
        var properties = e.GetCurrentPoint(Screen).Properties;
        _ = SendClickAsync(x, y, ButtonOf(properties), down: true);
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!Driving)
        {
            return;
        }

        var (x, y) = Guest(e);
        var properties = e.GetCurrentPoint(Screen).Properties;
        _ = SendClickAsync(x, y, ButtonOf(properties), down: false);
        e.Handled = true;
    }

    /// <summary>Which button, in the channel's numbering: 0 left, 1 middle, 2 right. WinUI reports the
    /// state after the transition, so a release reports every button as up — hence
    /// <c>PointerUpdateKind</c> rather than the Is*Pressed flags.</summary>
    private static int ButtonOf(Microsoft.UI.Input.PointerPointProperties properties) => properties.PointerUpdateKind switch
    {
        Microsoft.UI.Input.PointerUpdateKind.RightButtonPressed or Microsoft.UI.Input.PointerUpdateKind.RightButtonReleased => 2,
        Microsoft.UI.Input.PointerUpdateKind.MiddleButtonPressed or Microsoft.UI.Input.PointerUpdateKind.MiddleButtonReleased => 1,
        _ => 0,
    };

    private async Task SendClickAsync(int x, int y, int button, bool down)
    {
        // Move first: the guest's idea of where the pointer is comes from move events, and a click at a
        // position it hasn't been told about lands wherever it last was.
        await _bound!.PointerMoveAsync(x, y);
        await _bound.PointerButtonAsync(button, down);
    }

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (!Driving)
        {
            return;
        }

        var (x, y) = Guest(e);
        var delta = e.GetCurrentPoint(Screen).Properties.MouseWheelDelta;
        if (delta != 0)
        {
            // One notch per event; the browser already reports in 120ths of a notch.
            _ = _bound!.ScrollAsync(x, y, 0, -Math.Sign(delta));
            e.Handled = true;
        }
    }

    // ---- keyboard ----

    private void OnKeyDown(object sender, KeyRoutedEventArgs e) => SendKey(e, down: true);

    private void OnKeyUp(object sender, KeyRoutedEventArgs e) => SendKey(e, down: false);

    private void SendKey(KeyRoutedEventArgs e, bool down)
    {
        if (!Driving || DisplayKeys.Map(e.Key) is not { } keysym)
        {
            return;
        }

        _ = _bound!.KeyAsync(keysym, down);

        // Tab would otherwise walk the browser's focus out of the pane mid-session, and the arrows would
        // scroll the transcript behind it.
        e.Handled = true;
    }
}
