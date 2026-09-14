using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.App.Mobile.Views;
using Agnes.Client;
using Avalonia.Controls;
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

    public override void Initialize()
    {
        StartupTrace.Mark("app.initialize.start");
        AvaloniaXamlLoader.Load(this);
        StartupTrace.Mark("app.initialize.done (App.axaml: FluentTheme + 5 dictionaries)");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            StartupTrace.Mark("framework.initialized");
            var settings = MobileSettings.Load();
            StartupTrace.Mark("settings.loaded");
            ThemeApplier.Apply(settings.Theme);
            StartupTrace.Mark("theme.applied");

            var dispatcher = new MobileDispatcher();
            var haptics = new AndroidHaptics(AndroidHost.Context, () => _shell?.Settings.Haptics ?? true);
            var notifier = new AndroidNotifier(
                AndroidHost.Context,
                () => _shell?.Settings ?? settings,
                () => AndroidHost.IsForeground);

            // sim:// resolves to the built-in offline demo host; everything else is a real SignalR host.
            IAgnesConnector connector = new MobileConnector();

            // Completed when the shell's first frame is actually on screen. The shell waits on it before
            // the expensive half of its restore — see ShellViewModel.StartAsync.
            var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var model = global::Android.OS.Build.Model;
            StartupTrace.Mark("shell.vm.ctor.start");
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
                clearNotification: notifier.Clear,
                // What this device does with a file an agent sends it. Built here, once, with the
                // application context — everything above the shell just asks the handler what it can do.
                receivedFiles: new AndroidReceivedFileHandler(AndroidHost.Context),
                // Only the graphical-session screen asks, and only to pick a quality tier.
                isMeteredNetwork: () => AndroidCapabilities.IsMetered,
                // CodeyBox is plain http on the operator's own LAN, and this app bans cleartext for
                // everything. See AndroidCodeyBoxTransport for the one exception and why it is stated
                // here rather than in the network-security config.
                codeyBoxClient: AndroidCodeyBoxTransport.Create,
                firstFrame: firstFrame.Task);
            StartupTrace.Mark("shell.vm.ctor.done");
            StartupTrace.Watch(_shell);

            // A gate nothing ever opens is a session list that never goes live, so it opens on its own if
            // no frame arrives — an activity that was destroyed before it drew, say. The worst this can
            // cost is the delay itself; the worst getting it wrong would cost is a dead app.
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(
                _ => firstFrame.TrySetResult(), TaskScheduler.Default);

            // Android recreates the activity (and therefore the view) independently of the app object, so
            // Avalonia wants a factory rather than a single instance — `MainView` logs
            // "not fully supported on Android" and leaves a stale view behind on recreation. The view
            // model is built once and outlives any view built from it.
            if (ApplicationLifetime is IActivityApplicationLifetime activity)
            {
                activity.MainViewFactory = () =>
                {
                    StartupTrace.Mark("shell.view.ctor.start");
                    var view = new ShellView { DataContext = _shell };
                    StartupTrace.Mark("shell.view.ctor.done");
                    SignalFirstFrame(view, firstFrame);
                    return view;
                };
            }
            else
            {
                var view = new ShellView { DataContext = _shell };
                single.MainView = view;
                SignalFirstFrame(view, firstFrame);
            }

            // Start, then ask for notification permission once the session list is up — in context,
            // rather than as a cold-start prompt with no explanation behind it.
            StartupTrace.Mark("shell.startAsync.begin");
            _ = _shell.StartAsync().ContinueWith(
                _ => dispatcher.Post(AndroidCapabilities.RequestNotificationPermission),
                TaskScheduler.Default);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Opens <paramref name="firstFrame"/> when the view has actually been composited.
    ///
    /// <c>RequestAnimationFrame</c> rather than attach or layout: attach happens before anything is
    /// drawn, and Android's own "Displayed" line can be the splash window. This is the frame the person
    /// holding the phone sees.
    /// </summary>
    private static void SignalFirstFrame(Control view, TaskCompletionSource firstFrame)
    {
        view.AttachedToVisualTree += (_, _) =>
        {
            StartupTrace.MarkOnce("shell.view.attached");
            if (TopLevel.GetTopLevel(view) is { } top)
            {
                top.RequestAnimationFrame(_ =>
                {
                    StartupTrace.MarkOnce("first.frame (composited)");
                    firstFrame.TrySetResult();
                });
            }
            else
            {
                firstFrame.TrySetResult();
            }
        };
        view.LayoutUpdated += (_, _) => StartupTrace.MarkOnce("shell.view.firstLayout");
    }
}
