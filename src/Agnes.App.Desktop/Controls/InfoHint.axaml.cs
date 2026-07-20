using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Agnes.App.Desktop.Controls;

/// <summary>
/// A reusable ⓘ affordance: click to reveal a short explanation of a setting and, when relevant, a
/// highlighted security note. Used on the Settings tab and on the per-session options. Content is set
/// as literal strings (Description / Security), so the inner DataContext is pinned to the control.
/// </summary>
public partial class InfoHint : UserControl
{
    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<InfoHint, string?>(nameof(Description));

    public static readonly StyledProperty<string?> SecurityProperty =
        AvaloniaProperty.Register<InfoHint, string?>(nameof(Security));

    public InfoHint()
    {
        AvaloniaXamlLoader.Load(this);
        // Pin the flyout's bindings to this control (the flyout popup wouldn't otherwise see the
        // control's own properties — it would inherit the outer view-model DataContext).
        if (Content is StyledElement content)
        {
            content.DataContext = this;
        }
    }

    /// <summary>What the setting does and its impact.</summary>
    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>An optional security note, shown as a highlighted callout.</summary>
    public string? Security
    {
        get => GetValue(SecurityProperty);
        set => SetValue(SecurityProperty, value);
    }
}
