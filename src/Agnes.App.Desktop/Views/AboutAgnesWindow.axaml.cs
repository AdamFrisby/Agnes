using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Agnes.App.Desktop.Views;

public partial class AboutAgnesWindow : Window
{
    public AboutAgnesWindow() => AvaloniaXamlLoader.Load(this);

    private async void OnLearnMore(object? sender, RoutedEventArgs e)
        => await Launcher.LaunchUriAsync(DesktopBranding.RepositoryUri).ConfigureAwait(true);
}
