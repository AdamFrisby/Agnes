using Agnes.Plugins.CodeyBox.Views;
using Agnes.Ui.Core.Plugins;
using Avalonia.Threading;

namespace Agnes.Plugins.CodeyBox;

/// <summary>
/// The plugin's entry point: a CodeyBox tab, and the view that renders it.
/// </summary>
/// <remarks>
/// Discovered by name — the desktop head scans each DLL under <c>%APPDATA%/Agnes/client-plugins</c> for
/// <see cref="IClientPluginModule"/> and instantiates it — so nothing in Agnes references this assembly.
/// It registers two halves of one feature: <see cref="ICustomScreenProvider"/> says a CodeyBox screen
/// exists, and <see cref="IViewFactory"/> says how to draw it.
/// </remarks>
public sealed class CodeyBoxModule : IClientPluginModule
{
    public void Register(ClientPluginCollector collector)
    {
        collector.AddCustomScreen(new CodeyBoxScreenProvider());
        collector.AddViewFactory<CodeyBoxQueueViewModel>(vm => new CodeyBoxQueueView { DataContext = vm });
    }
}

/// <summary>The CodeyBox screen, as offered to whatever lists the tabs a user can open.</summary>
public sealed class CodeyBoxScreenProvider : ICustomScreenProvider
{
    public string ScreenId => "codeybox.queue";

    public string Title => "CodeyBox";

    public string? Icon => null;

    /// <summary>
    /// Builds the screen. Options are resolved per screen rather than once at registration, so a key
    /// added after the app started is picked up by reopening the tab instead of restarting.
    /// </summary>
    public object CreateViewModel()
    {
        var options = CodeyBoxOptions.Resolve();
        var client = new CodeyBoxClient(options);
        return new CodeyBoxQueueViewModel(client, ToUiThread, options.IsConfigured);
    }

    /// <summary>
    /// Marshals to the UI thread. The view model is deliberately ignorant of Avalonia — it takes this as a
    /// delegate — so the same view model could back a different head, and so it can be tested without a
    /// dispatcher at all.
    /// </summary>
    private static Task ToUiThread(Action action)
        => Dispatcher.UIThread.CheckAccess()
            ? RunInline(action)
            : Dispatcher.UIThread.InvokeAsync(action).GetTask();

    private static Task RunInline(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
