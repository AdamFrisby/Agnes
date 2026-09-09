using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Agnes.Plugins.CodeyBox.Views;

/// <summary>
/// The work composer. Its DataContext is the composer view model; the only state it keeps of its own is
/// which inference chip is currently open, and whether the rare options are showing.
/// </summary>
/// <remarks>
/// That state is here rather than on the view model deliberately. Which box a person happens to have
/// open is not a fact about the work being created — it does not survive being sent anywhere, another
/// head would express it differently, and putting it on the view model would mean the queue's own tests
/// asserting on it. It is exactly the same call the queue view already makes about a scroll position.
/// </remarks>
public partial class ComposerView : UserControl
{
    /// <summary>The chip whose editor is open ("project", "agent", …), or null for none.</summary>
    public static readonly StyledProperty<string?> OpenFieldProperty =
        AvaloniaProperty.Register<ComposerView, string?>(nameof(OpenField));

    /// <summary>Whether the rare fields — auditor profile, tracker id, refactor — are showing.</summary>
    public static readonly StyledProperty<bool> OptionsOpenProperty =
        AvaloniaProperty.Register<ComposerView, bool>(nameof(OptionsOpen));

    public string? OpenField
    {
        get => GetValue(OpenFieldProperty);
        set => SetValue(OpenFieldProperty, value);
    }

    public bool OptionsOpen
    {
        get => GetValue(OptionsOpenProperty);
        set => SetValue(OptionsOpenProperty, value);
    }

    /// <summary>Opens a chip's editor, or closes it if it was already the open one.</summary>
    public ICommand ShowFieldCommand { get; }

    /// <summary>Shows or hides the advanced fields.</summary>
    public ICommand ToggleOptionsCommand { get; }

    public ComposerView()
    {
        ShowFieldCommand = new Act(field =>
        {
            var name = field as string;
            OpenField = string.Equals(OpenField, name, StringComparison.Ordinal) ? null : name;
        });

        ToggleOptionsCommand = new Act(_ => OptionsOpen = !OptionsOpen);

        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// A command over a plain action. Small enough not to be worth a dependency, and view state has no
    /// business travelling through the view model's command infrastructure to get back here.
    /// </summary>
    private sealed class Act(Action<object?> run) : ICommand
    {
        private EventHandler? _unused;

        // Never raised: both of these are available for as long as the composer is on screen.
        public event EventHandler? CanExecuteChanged
        {
            add => _unused += value;
            remove => _unused -= value;
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => run(parameter);
    }
}
