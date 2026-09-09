using System.Collections.Specialized;
using Agnes.App.Mobile.Controls;
using Agnes.App.Mobile.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Agnes.App.Mobile.Views;

/// <summary>
/// The session screen's scroll behaviour, which is the part that can't be expressed in XAML.
///
/// A chat that scrolls itself is obnoxious when you're reading history, so the rule is: follow the tail
/// only while you're already at the tail. Scroll up and the view stays put, with a "Latest" pill
/// offering the way back — the same contract every messaging app on the platform uses.
/// </summary>
public partial class SessionPageView : UserControl
{
    /// <summary>How close to the bottom still counts as "at the tail" (device-independent pixels). A
    /// couple of lines of slack, so a half-rendered final message doesn't break the follow.</summary>
    private const double TailSlack = 90;

    private ScrollViewer _scroll = null!;
    private ItemsControl _transcript = null!;
    private Avalonia.Controls.Button _jump = null!;
    private DisplaySurface _screen = null!;
    private TextBox _ime = null!;
    private TextBox _composer = null!;
    private SessionPageViewModel? _page;
    private INotifyCollectionChanged? _watched;

    public SessionPageView()
    {
        AvaloniaXamlLoader.Load(this);
        _scroll = this.FindControl<ScrollViewer>("TranscriptScroll")!;
        _transcript = this.FindControl<ItemsControl>("Transcript")!;
        _jump = this.FindControl<Avalonia.Controls.Button>("JumpToLatest")!;

        _screen = this.FindControl<DisplaySurface>("Screen")!;
        _ime = this.FindControl<TextBox>("ScreenIme")!;
        _composer = this.FindControl<TextBox>("Composer")!;

        // "You are here now": what takes the away band down.
        //
        // Gestures, not ScrollChanged. The page scrolls itself to the tail on arrival, so a scroll
        // event would dismiss the band in the same frame it appeared — the band would be a flicker
        // nobody ever read. A pointer press (a tap, and the start of every drag-scroll), a wheel, or a
        // keystroke in the composer are all unambiguously a person.
        _scroll.AddHandler(PointerPressedEvent, OnUserPresent, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _scroll.AddHandler(PointerWheelChangedEvent, OnUserPresent, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _composer.AddHandler(TextInputEvent, OnUserPresent, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _jump.Click += (_, _) => ScrollToEnd();
        _scroll.ScrollChanged += (_, _) => _jump.IsVisible = !IsAtTail;

        // The IME hands over finished text, one insertion at a time. Forward it as keysyms and clear the
        // box immediately — it is a funnel, not a field, and anything left in it would be re-sent.
        _ime.AddHandler(TextInputEvent, OnImeText, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        _ime.KeyDown += OnImeKey;

        DataContextChanged += (_, _) => Bind(DataContext as SessionPageViewModel);
    }

    private bool IsAtTail
        => _scroll.Extent.Height <= _scroll.Viewport.Height
           || _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - TailSlack;

    private void Bind(SessionPageViewModel? page)
    {
        if (ReferenceEquals(_page, page))
        {
            return;
        }

        if (_page is not null)
        {
            _page.ScrollToBottomRequested -= ScrollToEnd;
            _page.ScrollToRequested -= ScrollToAnchor;
            _page.ScreenKeyboardRequested -= RaiseKeyboard;
            _page.ScreenKeyRequested -= _screen.PressKey;
        }

        Detach();
        _page = page;

        if (page is null)
        {
            return;
        }

        page.ScrollToBottomRequested += ScrollToEnd;
        page.ScrollToRequested += ScrollToAnchor;
        page.ScreenKeyboardRequested += RaiseKeyboard;
        page.ScreenKeyRequested += _screen.PressKey;

        // What the host is asked to send is a promise about what this panel can show, so it comes from
        // the panel: device-independent width times the scaling, i.e. real pixels.
        page.ScreenPixelWidth = (int)Math.Round(
            Bounds.Width > 0 ? Bounds.Width * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1) : 960);
        Watch(page);
        page.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SessionPageViewModel.Session))
            {
                Watch(page);
            }
            else if (e.PropertyName is nameof(SessionPageViewModel.Segment) && page.IsScreenSegment)
            {
                // Entering the screen always starts fit-to-view. Landing mid-zoom on a corner of someone
                // else's desktop, with the gesture to get back not yet discovered, is disorienting.
                _screen.ResetView();
            }
        };

        ScrollToEnd();
    }

    // The transcript collection is swapped when the session attaches (and again if the view filters to a
    // subagent), so the follow-the-tail subscription has to move with it.
    private void Watch(SessionPageViewModel page)
    {
        Detach();
        if (page.Session?.Items is INotifyCollectionChanged items)
        {
            _watched = items;
            items.CollectionChanged += OnItemsChanged;
        }
    }

    private void Detach()
    {
        if (_watched is not null)
        {
            _watched.CollectionChanged -= OnItemsChanged;
            _watched = null;
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!IsAtTail)
        {
            // Reading history: leave the viewport alone and let the pill offer the way back.
            Dispatcher.UIThread.Post(() => _jump.IsVisible = true, DispatcherPriority.Background);
            return;
        }

        ScrollToEnd();
    }

    private void ScrollToEnd()
        // Deferred to Background so the newly-added item has been measured; scrolling before layout
        // lands short of the true end and leaves the last line clipped.
        => Dispatcher.UIThread.Post(() =>
        {
            _scroll.ScrollToEnd();
            _jump.IsVisible = false;
        }, DispatcherPriority.Background);

    private void OnUserPresent(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => _page?.NoteUserInteraction();

    /// <summary>Brings up the soft keyboard over the screen by focusing the hidden funnel.</summary>
    private void RaiseKeyboard() => Dispatcher.UIThread.Post(() => _ime.Focus());

    private void OnImeText(object? sender, TextInputEventArgs e)
    {
        if (e.Text is { Length: > 0 } text)
        {
            _screen.TypeText(text);
        }

        _ime.Text = string.Empty;
        e.Handled = true;
    }

    private void OnImeKey(object? sender, KeyEventArgs e)
    {
        // Return and Backspace never arrive as text input, on any platform. Everything else the soft
        // keyboard produces does, and is handled above.
        var keysym = e.Key switch
        {
            Key.Return or Key.Enter => DisplayKeysyms.Return,
            Key.Back => DisplayKeysyms.BackSpace,
            Key.Escape => DisplayKeysyms.Escape,
            Key.Tab => DisplayKeysyms.Tab,
            _ => null,
        };

        if (keysym is not null)
        {
            _screen.PressKey(keysym);
            _ime.Text = string.Empty;
            e.Handled = true;
        }
    }

    /// <summary>Scrolls to a transcript item by anchor id (search hit, review jump).</summary>
    private void ScrollToAnchor(string anchorId) => Dispatcher.UIThread.Post(() =>
    {
        var index = 0;
        foreach (var item in _transcript.ItemsSource ?? System.Linq.Enumerable.Empty<object>())
        {
            if (item is Agnes.Ui.Core.Transcript.TranscriptItem transcript && transcript.AnchorId == anchorId)
            {
                if (_transcript.ContainerFromIndex(index) is Control container)
                {
                    container.BringIntoView();
                }

                return;
            }

            index++;
        }
    }, DispatcherPriority.Background);
}
