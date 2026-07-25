using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.App.Mobile.Views;
using Agnes.Client;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Agnes.App.Mobile;

// NOTE: `Application` is deliberately fully qualified in this head — the Android SDK's implicit global
// usings bring `Android.App.Application` into scope, so the bare name is ambiguous.
public partial class App : Avalonia.Application
{
    private ShellViewModel? _shell;

    /// <summary>The running shell, so the activity can route a notification tap into it.</summary>
    public ShellViewModel? Shell => _shell;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            var settings = MobileSettings.Load();
            ThemeApplier.Apply(settings.Theme);

            var dispatcher = new MobileDispatcher();
            var haptics = new AndroidHaptics(AndroidHost.Context, () => _shell?.Settings.Haptics ?? true);
            var notifier = new AndroidNotifier(
                AndroidHost.Context,
                () => _shell?.Settings ?? settings,
                () => AndroidHost.IsForeground);

            // sim:// resolves to the built-in offline demo host; everything else is a real SignalR host.
            IAgnesConnector connector = new MobileConnector();

            var model = global::Android.OS.Build.Model;
            _shell = new ShellViewModel(
                connector,
                dispatcher,
                settings,
                deviceName: string.IsNullOrWhiteSpace(model) ? "Android phone" : $"{model} (Android)",
                haptics,
                notifier,
                dictate: AndroidCapabilities.CanDictate ? AndroidCapabilities.DictateAsync : null,
                copyToClipboard: AndroidCapabilities.CopyToClipboard,
                openUrl: AndroidCapabilities.OpenUrl,
                clearNotification: notifier.Clear);

            // Android recreates the activity (and therefore the view) independently of the app object, so
            // Avalonia wants a factory rather than a single instance — `MainView` logs
            // "not fully supported on Android" and leaves a stale view behind on recreation. The view
            // model is built once and outlives any view built from it.
            if (ApplicationLifetime is IActivityApplicationLifetime activity)
            {
                activity.MainViewFactory = () => new ShellView { DataContext = _shell };
            }
            else
            {
                single.MainView = new ShellView { DataContext = _shell };
            }

            // Start, then ask for notification permission once the session list is up — in context,
            // rather than as a cold-start prompt with no explanation behind it.
            _ = _shell.StartAsync().ContinueWith(
                _ => dispatcher.Post(AndroidCapabilities.RequestNotificationPermission),
                TaskScheduler.Default);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
