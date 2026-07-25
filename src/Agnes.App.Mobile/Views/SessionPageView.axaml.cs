using System.Collections.Specialized;
using Agnes.App.Mobile.ViewModels;
using Avalonia;
using Avalonia.Controls;
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
    private SessionPageViewModel? _page;
    private INotifyCollectionChanged? _watched;

    public SessionPageView()
    {
        AvaloniaXamlLoader.Load(this);
        _scroll = this.FindControl<ScrollViewer>("TranscriptScroll")!;
        _transcript = this.FindControl<ItemsControl>("Transcript")!;
        _jump = this.FindControl<Avalonia.Controls.Button>("JumpToLatest")!;

        _jump.Click += (_, _) => ScrollToEnd();
        _scroll.ScrollChanged += (_, _) => _jump.IsVisible = !IsAtTail;

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
        }

        Detach();
        _page = page;

        if (page is null)
        {
            return;
        }

        page.ScrollToBottomRequested += ScrollToEnd;
        page.ScrollToRequested += ScrollToAnchor;
        Watch(page);
        page.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SessionPageViewModel.Session))
            {
                Watch(page);
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
