using System;
using System.Collections.Generic;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

namespace Agnes.App.Desktop.ViewModels;

/// <summary>Builds a single tabbed document area (browser-style) and notifies on tab changes.</summary>
public sealed class DockFactory : Factory
{
    public IDocumentDock? DocumentDock { get; private set; }

    /// <summary>Supplies a new tab for the document dock's "+" button.</summary>
    public Func<IDockable?>? NewDocumentFactory { get; set; }

    /// <summary>Raised when tabs are added/removed/closed (to persist state).</summary>
    public Action? LayoutChanged { get; set; }

    public override IRootDock CreateLayout()
    {
        var documentDock = new DocumentDock
        {
            Id = "Documents",
            Title = "Documents",
            IsCollapsable = false,
            CanCreateDocument = false, // the top bar's "New tab" is the single add affordance (no duplicate + on the strip).
            VisibleDockables = CreateList<IDockable>(),
            DocumentFactory = () => NewDocumentFactory?.Invoke(),
        };

        var root = CreateRootDock();
        root.Id = "Root";
        root.Title = "Root";
        root.IsCollapsable = false;
        root.VisibleDockables = CreateList<IDockable>(documentDock);
        root.ActiveDockable = documentDock;

        DocumentDock = documentDock;
        return root;
    }

    public override void InitLayout(IDockable layout)
    {
        // Enable floating tabs into their own OS windows (drag a tab out, or "Open in new window").
        HostWindowLocator = new Dictionary<string, Func<IHostWindow?>>
        {
            [nameof(IDockWindow)] = () => new HostWindow(),
        };
        DefaultHostWindowLocator = () => new HostWindow();
        base.InitLayout(layout);
    }

    public override void OnDockableAdded(IDockable? dockable)
    {
        base.OnDockableAdded(dockable);
        LayoutChanged?.Invoke();
    }

    public override void OnDockableClosed(IDockable? dockable)
    {
        base.OnDockableClosed(dockable);

        // Explicit stop-on-close: when a session tab is CLOSED (not floated/detached — that routes through
        // OnDockableRemoved), end its agent and shut its sandbox VM down (kept for resume). Fire-and-forget
        // so the UI close isn't blocked.
        if (dockable is SessionDocument { Host: { } host, Descriptor.SessionId: { } sessionId })
        {
            _ = host.StopSessionAsync(sessionId);
        }

        // Drop the closed document's cached view from the shared dock recycler so it doesn't leak.
        if (dockable is not null
            && Avalonia.Application.Current?.Resources.TryGetResource("DockRecycler", null, out var r) == true
            && r is PerItemControlRecycling recycler)
        {
            recycler.Forget(dockable);
        }

        LayoutChanged?.Invoke();
    }
}
