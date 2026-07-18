using Agnes.App.Shells;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Agnes.App;

public sealed partial class MainPage : Page
{
    private const double DesktopBreakpoint = 720;
    private bool? _isDesktop;

    public MainPage() => InitializeComponent();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DataContext = App.Workspace;
        UpdateShell(ActualWidth);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateShell(e.NewSize.Width);

    /// <summary>Swaps between the genuinely distinct desktop and mobile shells by form factor.</summary>
    private void UpdateShell(double width)
    {
        var isDesktop = width >= DesktopBreakpoint;
        if (_isDesktop == isDesktop)
        {
            return;
        }

        _isDesktop = isDesktop;
        ShellHost.Content = isDesktop ? new DesktopShell() : new MobileShell();
    }
}
