using System.Linq;
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

        var autopilot = DemoAutopilot.GetConfig();
        if (autopilot is not null)
        {
            _ = RunAutopilotAsync(autopilot);
        }
    }

    /// <summary>Drives the real UI unattended for demos/screenshots (off unless configured).</summary>
    private static async Task RunAutopilotAsync(AutopilotConfig config)
    {
        var workspace = App.Workspace;
        if (workspace is null)
        {
            return;
        }

        workspace.HostUrl = config.Url;
        workspace.Token = config.Token;
        workspace.WorkingDirectory = config.Cwd;
        workspace.ConnectCommand.Execute(null);

        for (var i = 0; i < 60 && workspace.Agents.Count == 0; i++)
        {
            await Task.Delay(500);
        }

        var agent = workspace.Agents.FirstOrDefault(a => a.AdapterId == config.Agent)
                    ?? workspace.Agents.FirstOrDefault();
        if (agent is null)
        {
            return;
        }

        await workspace.OpenSessionAsync(agent);
        for (var i = 0; i < 40 && workspace.ActiveSession is null; i++)
        {
            await Task.Delay(250);
        }

        if (!string.IsNullOrEmpty(config.Prompt) && workspace.ActiveSession is { } session)
        {
            session.PromptText = config.Prompt;
            session.SendCommand.Execute(null);
        }
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
