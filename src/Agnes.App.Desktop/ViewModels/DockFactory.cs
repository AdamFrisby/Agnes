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
            CanCreateDocument = true,
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
        LayoutChanged?.Invoke();
    }
}
