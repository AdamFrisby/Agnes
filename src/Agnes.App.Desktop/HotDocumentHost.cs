using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Dock.Avalonia.Controls;
using Dock.Model.Core;

namespace Agnes.App.Desktop;

/// <summary>
/// The document dock's content host: keeps the views of the most recently activated documents attached
/// to the visual tree and hidden, up to <see cref="Capacity"/>, so switching among them is a visibility
/// flip rather than a rebuild.
/// </summary>
/// <remarks>
/// <para>Dock's stock template swaps one presenter's content on every activation, which detaches the
/// outgoing view and re-attaches the incoming one; Avalonia then re-applies styles and re-evaluates
/// inherited properties and bindings across the whole subtree. Measured on a real session tab that was
/// 1.2–1.9 s per switch. Dock also ships a cached template that keeps <i>every</i> document attached,
/// which is right for ten tabs and wrong for two hundred: hidden views still hold their controls. This
/// host is the bounded version — three tiers, by recency of activation:</para>
/// <list type="bullet">
/// <item><b>Hot</b> — the last <see cref="Capacity"/> activated documents. Attached, hidden unless active.
/// A switch among them costs a layout pass of the one shown.</item>
/// <item><b>Warm</b> — everything else that has ever been shown. Detached here, but the built view is kept
/// by the <see cref="Recycling"/> cache, so activating one pays the re-attach once and promotes it.</item>
/// <item><b>Cold</b> — a document the window has hibernated (see the view model): the recycler has
/// forgotten its view and it rebuilds from scratch when next shown.</item>
/// </list>
/// <para>Hidden children are neither measured nor rendered; they receive their bindings' updates, which
/// for a virtualising transcript is a collection change and nothing more.</para>
/// </remarks>
public sealed class HotDocumentHost : Panel
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<HotDocumentHost, IEnumerable?>(nameof(ItemsSource));

    public static readonly StyledProperty<IDockable?> ActiveProperty =
        AvaloniaProperty.Register<HotDocumentHost, IDockable?>(nameof(Active));

    public static readonly StyledProperty<int> CapacityProperty =
        AvaloniaProperty.Register<HotDocumentHost, int>(nameof(Capacity), 8);

    public static readonly StyledProperty<PerItemControlRecycling?> RecyclingProperty =
        AvaloniaProperty.Register<HotDocumentHost, PerItemControlRecycling?>(nameof(Recycling));

    // Hot documents, least recently activated first. Each maps to exactly one child of this panel.
    private readonly List<IDockable> _hot = [];
    private readonly Dictionary<IDockable, DockableControl> _children = new(ReferenceEqualityComparer.Instance);
    private INotifyCollectionChanged? _observed;

    static HotDocumentHost()
    {
        ActiveProperty.Changed.AddClassHandler<HotDocumentHost>((host, _) => host.Show(host.Active));
        ItemsSourceProperty.Changed.AddClassHandler<HotDocumentHost>((host, e) => host.Observe(e.NewValue as IEnumerable));
        CapacityProperty.Changed.AddClassHandler<HotDocumentHost>((host, _) => host.Trim());
    }

    /// <summary>The dock's visible documents; a document that leaves the list leaves this host.</summary>
    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>The document shown. Setting it builds or reveals its view and hides the others.</summary>
    public IDockable? Active
    {
        get => GetValue(ActiveProperty);
        set => SetValue(ActiveProperty, value);
    }

    /// <summary>How many document views stay attached. The active one always counts as one of them.</summary>
    public int Capacity
    {
        get => GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    /// <summary>The per-document view cache shared with the rest of the dock, so a document evicted from
    /// here keeps its built view (warm) rather than rebuilding it (cold).</summary>
    public PerItemControlRecycling? Recycling
    {
        get => GetValue(RecyclingProperty);
        set => SetValue(RecyclingProperty, value);
    }

    /// <summary>The documents currently attached, least recently activated first — for tests and the odd
    /// diagnostic, not for the view.</summary>
    public IReadOnlyList<IDockable> Hot => _hot;

    private void Observe(IEnumerable? items)
    {
        if (_observed is not null)
        {
            _observed.CollectionChanged -= OnItemsChanged;
        }

        _observed = items as INotifyCollectionChanged;
        if (_observed is not null)
        {
            _observed.CollectionChanged += OnItemsChanged;
        }

        Reconcile();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Reconcile();

    /// <summary>Drops any hosted document that is no longer in the dock — closed, or floated elsewhere.</summary>
    private void Reconcile()
    {
        var present = new HashSet<IDockable>(ItemsSource?.OfType<IDockable>() ?? [], ReferenceEqualityComparer.Instance);
        foreach (var gone in _hot.Where(d => !present.Contains(d)).ToList())
        {
            Evict(gone);
        }
    }

    private void Show(IDockable? active)
    {
        if (active is not null && ItemsSource?.OfType<IDockable>().Contains(active) != false)
        {
            if (!_children.TryGetValue(active, out var child))
            {
                child = Materialize(active);
                if (child is null)
                {
                    return;
                }
                _children[active] = child;
                Children.Add(child);
            }

            _hot.Remove(active);
            _hot.Add(active);
        }

        foreach (var (dockable, child) in _children)
        {
            child.IsVisible = ReferenceEquals(dockable, active);
        }

        Trim();
    }

    private DockableControl? Materialize(IDockable dockable)
    {
        // The recycler is the one place a document's view is built, so a view that was hot, then warm,
        // comes back as the same control with its scroll position and composer text intact.
        var view = Recycling?.Build(dockable, null, this) as Control
            ?? this.FindDataTemplate(dockable)?.Build(dockable) as Control;
        if (view is null)
        {
            return null;
        }

        view.DataContext = dockable;
        // The same wrapper Dock's own templates use, so the dock still learns each document's visible
        // bounds (floating placement, drop targets) from the view it is actually showing.
        var wrapper = new DockableControl
        {
            TrackingMode = TrackingMode.Visible,
            DataContext = dockable,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
        };
        wrapper.Children.Add(view);
        return wrapper;
    }

    private void Trim()
    {
        var capacity = System.Math.Max(1, Capacity);
        while (_hot.Count > capacity)
        {
            // Least recently activated goes first; the active document is always last, so it never goes.
            Evict(_hot[0]);
        }
    }

    private void Evict(IDockable dockable)
    {
        _hot.Remove(dockable);
        if (_children.Remove(dockable, out var child))
        {
            Children.Remove(child);
            // Unwrap so the cached view is not left parented to a dead wrapper; the recycler keeps the
            // view itself, which is the whole point of warm over cold.
            child.Children.Clear();
        }
    }
}
