using Agnes.App.Desktop.Keymaps;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Agnes.App.Desktop.Views;

public partial class SettingsTabView : UserControl
{
    public SettingsTabView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnCopyKeymapJson(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: KeymapCommandRow { JsonRule: { } json } }
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(json);
            e.Handled = true;
        }
    }
}
