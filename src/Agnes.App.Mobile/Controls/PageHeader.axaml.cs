using Agnes.App.Mobile.ViewModels;
using Agnes.App.Mobile.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace Agnes.App.Mobile.Controls;

/// <summary>
/// The app bar every pushed page wears. Back routes through the shell's single navigation handler, so
/// the button and the system gesture do exactly the same thing.
/// </summary>
public partial class PageHeader : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<PageHeader, string?>(nameof(Title));

    public static readonly StyledProperty<string?> SubtitleProperty =
        AvaloniaProperty.Register<PageHeader, string?>(nameof(Subtitle));

    public static readonly StyledProperty<object?> ActionProperty =
        AvaloniaProperty.Register<PageHeader, object?>(nameof(Action));

    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly ContentPresenter _action;

    public PageHeader()
    {
        AvaloniaXamlLoader.Load(this);
        _title = this.FindControl<TextBlock>("TitleText")!;
        _subtitle = this.FindControl<TextBlock>("SubtitleText")!;
        _action = this.FindControl<ContentPresenter>("ActionSlot")!;

        this.FindControl<Button>("BackButton")!.Click += (_, _) =>
            (this.FindAncestorOfType<ShellView>()?.DataContext as ShellViewModel)?.GoBack();
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Subtitle
    {
        get => GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>Optional trailing content (an action button).</summary>
    public object? Action
    {
        get => GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty)
        {
            _title.Text = Title;
        }
        else if (change.Property == SubtitleProperty)
        {
            _subtitle.Text = Subtitle;
            _subtitle.IsVisible = !string.IsNullOrWhiteSpace(Subtitle);
        }
        else if (change.Property == ActionProperty)
        {
            _action.Content = Action;
        }
    }
}
