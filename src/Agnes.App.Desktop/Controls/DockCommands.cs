using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Core;

namespace Agnes.App.Desktop.Controls;

/// <summary>
/// The Dock layout actions the tab context menu offers, as commands XAML can bind.
///
/// Dock exposes them as <see cref="IFactory"/> <em>methods</em> taking an <see cref="IDockable"/>, and
/// Avalonia's method-to-command binding accepts only a parameterless method or one taking
/// <see cref="object"/> — so binding them directly compiles to nothing and the menu item silently
/// disables itself. These wrap each call in a real <see cref="ICommand"/>, which also puts the
/// "is this dockable in a state where that's possible" question in one testable place.
///
/// Static because they are pure functions of their parameter: the dockable carries its own factory, so
/// there is no per-window state to hold and nothing to dispose.
/// </summary>
public static class DockCommands
{
    /// <summary>The factory that owns a dockable, or null when it isn't docked anywhere (yet).</summary>
    private static IFactory? FactoryOf(object? dockable) => (dockable as IDockable)?.Factory;

    private static ICommand For(Action<IFactory, IDockable> action)
        => new RelayCommand<object?>(
            d => { if (d is IDockable dockable && dockable.Factory is { } factory) { action(factory, dockable); } },
            d => FactoryOf(d) is not null);

    public static ICommand Close { get; } = For((f, d) => f.CloseDockable(d));
    public static ICommand CloseOthers { get; } = For((f, d) => f.CloseOtherDockables(d));
    public static ICommand CloseAll { get; } = For((f, d) => f.CloseAllDockables(d));
    public static ICommand CloseLeft { get; } = For((f, d) => f.CloseLeftDockables(d));
    public static ICommand CloseRight { get; } = For((f, d) => f.CloseRightDockables(d));
    public static ICommand Float { get; } = For((f, d) => f.FloatDockable(d));
    public static ICommand FloatAll { get; } = For((f, d) => f.FloatAllDockables(d));
    public static ICommand NewHorizontalDock { get; } = For((f, d) => f.NewHorizontalDocumentDock(d));
    public static ICommand NewVerticalDock { get; } = For((f, d) => f.NewVerticalDocumentDock(d));
    public static ICommand TabsLeft { get; } = For((f, d) => f.SetDocumentDockTabsLayoutLeft(d));
    public static ICommand TabsTop { get; } = For((f, d) => f.SetDocumentDockTabsLayoutTop(d));
    public static ICommand TabsRight { get; } = For((f, d) => f.SetDocumentDockTabsLayoutRight(d));
    public static ICommand LayoutTabbed { get; } = For((f, d) => f.SetDocumentDockLayoutModeTabbed(d));
    public static ICommand LayoutMdi { get; } = For((f, d) => f.SetDocumentDockLayoutModeMdi(d));
}
