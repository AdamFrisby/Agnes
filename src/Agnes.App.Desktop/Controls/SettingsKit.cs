using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using FluentIcons.Common;

namespace Agnes.App.Desktop.Controls;

/// <summary>
/// The top of every settings page: what the page is (title), what it is for in one sentence (purpose),
/// where its settings live (scope — "This device" or the host's name), and the page's actions on the
/// right. One shape for fourteen pages, so a person learns it once. Templated in
/// <c>Views/Settings/SettingsKit.axaml</c>.
/// </summary>
public class SettingsPageHeader : TemplatedControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SettingsPageHeader, string?>(nameof(Title));

    public static readonly StyledProperty<string?> PurposeProperty =
        AvaloniaProperty.Register<SettingsPageHeader, string?>(nameof(Purpose));

    public static readonly StyledProperty<string?> ScopeProperty =
        AvaloniaProperty.Register<SettingsPageHeader, string?>(nameof(Scope));

    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<SettingsPageHeader, object?>(nameof(Actions));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Purpose
    {
        get => GetValue(PurposeProperty);
        set => SetValue(PurposeProperty, value);
    }

    /// <summary>Where the settings live: "This device", or the connected host's name.</summary>
    public string? Scope
    {
        get => GetValue(ScopeProperty);
        set => SetValue(ScopeProperty, value);
    }

    /// <summary>The page's actions, laid out on the right: the primary one first, an icon-only Refresh last.</summary>
    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }
}

/// <summary>
/// One section of a settings page as a card: a title, an optional hint (the <see cref="InfoHint"/>
/// popover, with its security callout), an optional one-line description, actions on the right, and
/// the content. Sections are how a long page is read at a glance — a person scans the titles, then
/// opens the one they came for.
/// </summary>
public class SettingsSection : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<SettingsSection, string?>(nameof(Title));

    public static readonly StyledProperty<string?> DescriptionProperty =
        AvaloniaProperty.Register<SettingsSection, string?>(nameof(Description));

    public static readonly StyledProperty<string?> HintProperty =
        AvaloniaProperty.Register<SettingsSection, string?>(nameof(Hint));

    public static readonly StyledProperty<string?> SecurityProperty =
        AvaloniaProperty.Register<SettingsSection, string?>(nameof(Security));

    public static readonly StyledProperty<object?> ActionsProperty =
        AvaloniaProperty.Register<SettingsSection, object?>(nameof(Actions));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>One sentence under the title, always visible — for what a person must know before acting.</summary>
    public string? Description
    {
        get => GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>The longer explanation, behind the info glyph — for what a person may want to know.</summary>
    public string? Hint
    {
        get => GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    /// <summary>The security callout inside the hint popover.</summary>
    public string? Security
    {
        get => GetValue(SecurityProperty);
        set => SetValue(SecurityProperty, value);
    }

    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }
}

/// <summary>
/// What a list shows when it has nothing: an icon, a title that names the thing, a sentence that says
/// how one gets here, and the first action. A page that opens to "0 item(s)." tells a person nothing;
/// this tells them what the page is for and what to do next.
/// </summary>
public class EmptyState : TemplatedControl
{
    public static readonly StyledProperty<Symbol> SymbolProperty =
        AvaloniaProperty.Register<EmptyState, Symbol>(nameof(Symbol), Symbol.Info);

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<EmptyState, string?>(nameof(Title));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<EmptyState, string?>(nameof(Text));

    public static readonly StyledProperty<object?> ActionProperty =
        AvaloniaProperty.Register<EmptyState, object?>(nameof(Action));

    public Symbol Symbol
    {
        get => GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public object? Action
    {
        get => GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }
}
