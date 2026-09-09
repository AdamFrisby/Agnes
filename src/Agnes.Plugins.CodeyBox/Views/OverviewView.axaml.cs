using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Agnes.Plugins.CodeyBox.Views;

/// <summary>
/// The three bands of the overview. Its DataContext is an <see cref="Overview"/> and nothing else.
/// </summary>
/// <remarks>
/// Keeping the record as the whole DataContext is what makes this screen renderable — and screenshot-able
/// — from a hand-built <see cref="Overview"/> with no view model, no client and no host anywhere near it.
/// The price is that the two things a row can <em>do</em> live outside that record, so they arrive as
/// properties on this control instead of being reached for up the tree: the parent binds them once,
/// every row inside binds <c>$parent[views:OverviewView]</c>, and a row template stays a function of one
/// <see cref="ItemTrace"/>.
/// </remarks>
public partial class OverviewView : UserControl
{
    public static readonly StyledProperty<ICommand?> OpenItemCommandProperty =
        AvaloniaProperty.Register<OverviewView, ICommand?>(nameof(OpenItemCommand));

    /// <summary>Opens the item a row names. Parameter: the row's <see cref="ItemTrace"/>.</summary>
    public ICommand? OpenItemCommand
    {
        get => GetValue(OpenItemCommandProperty);
        set => SetValue(OpenItemCommandProperty, value);
    }

    public static readonly StyledProperty<ICommand?> ExtendCeilingCommandProperty =
        AvaloniaProperty.Register<OverviewView, ICommand?>(nameof(ExtendCeilingCommand));

    /// <summary>Raises the audit iteration cap for a row that is converging into it. Parameter: the
    /// row's <see cref="ItemTrace"/>.</summary>
    public ICommand? ExtendCeilingCommand
    {
        get => GetValue(ExtendCeilingCommandProperty);
        set => SetValue(ExtendCeilingCommandProperty, value);
    }

    public OverviewView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
