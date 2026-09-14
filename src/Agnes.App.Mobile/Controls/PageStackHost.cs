using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Agnes.App.Mobile.Controls;

/// <summary>
/// Hosts the navigation stack's pages, keeping every page on the stack attached and hidden behind the
/// one on top, so popping back to a page is a visibility flip rather than a rebuild.
/// </summary>
/// <remarks>
/// <para>A <c>ContentControl</c> bound to the current page throws the outgoing page's view away on every
/// push and builds a new one on every pop. On a phone that is the common gesture — open a session,
/// push a sub-page, come back — and a session page is the most expensive view the app has to build
/// (the desktop's equivalent measured 0.5–1 s to attach). The stack is bounded by how deep a person
/// navigates, a handful at most, so every page on it stays built.</para>
/// <para>A page that leaves the stack leaves here, view and all; hidden pages are neither measured nor
/// rendered. Sheets are not pages and are hosted separately — they overlay the page, which stays put.</para>
/// </remarks>
public sealed class PageStackHost : Panel
{
    public static readonly StyledProperty<IEnumerable?> PagesProperty =
        AvaloniaProperty.Register<PageStackHost, IEnumerable?>(nameof(Pages));

    public static readonly StyledProperty<object?> CurrentProperty =
        AvaloniaProperty.Register<PageStackHost, object?>(nameof(Current));

    private readonly Dictionary<object, Control> _views = new(ReferenceEqualityComparer.Instance);
    private INotifyCollectionChanged? _observed;

    static PageStackHost()
    {
        PagesProperty.Changed.AddClassHandler<PageStackHost>((host, e) => host.Observe(e.NewValue as IEnumerable));
        CurrentProperty.Changed.AddClassHandler<PageStackHost>((host, _) => host.Show());
    }

    /// <summary>The navigation stack, bottom first. A page removed from it is dropped here.</summary>
    public IEnumerable? Pages
    {
        get => GetValue(PagesProperty);
        set => SetValue(PagesProperty, value);
    }

    /// <summary>The page on top — the one shown. Null shows nothing.</summary>
    public object? Current
    {
        get => GetValue(CurrentProperty);
        set => SetValue(CurrentProperty, value);
    }

    /// <summary>The pages whose views are currently built, for tests.</summary>
    public IReadOnlyCollection<object> Built => _views.Keys;

    private void Observe(IEnumerable? pages)
    {
        if (_observed is not null)
        {
            _observed.CollectionChanged -= OnPagesChanged;
        }

        _observed = pages as INotifyCollectionChanged;
        if (_observed is not null)
        {
            _observed.CollectionChanged += OnPagesChanged;
        }

        Reconcile();
        Show();
    }

    private void OnPagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Reconcile();
        Show();
    }

    private void Reconcile()
    {
        var present = new HashSet<object>(Pages?.Cast<object>() ?? [], ReferenceEqualityComparer.Instance);
        foreach (var gone in _views.Keys.Where(p => !present.Contains(p)).ToList())
        {
            if (_views.Remove(gone, out var view))
            {
                Children.Remove(view);
            }
        }
    }

    private void Show()
    {
        var current = Current;
        // A page that has already left the stack is not built again: a pop removes the page before the
        // shell moves Current to the one beneath, and that instant must not resurrect the popped view.
        var onStack = current is not null && (Pages is null || Pages.Cast<object>().Contains(current));
        if (current is not null && onStack && !_views.ContainsKey(current))
        {
            // Only the page on top is built; a page under it was built when it was on top.
            if (this.FindDataTemplate(current)?.Build(current) is Control view)
            {
                view.DataContext = current;
                _views[current] = view;
                Children.Add(view);
            }
        }

        foreach (var (page, view) in _views)
        {
            view.IsVisible = onStack && ReferenceEquals(page, current);
        }
    }
}
