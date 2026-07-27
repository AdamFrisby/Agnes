using System;
using System.Linq;
using Avalonia;

namespace Agnes.App.Desktop;

internal static class Program
{
    /// <summary>
    /// The claim on being the one running Agnes, held for the process's lifetime. Also how a second launch
    /// reaches us: it sends whatever it was opened with here and exits, so clicking a pairing link pairs in
    /// the window you already have open.
    /// </summary>
    internal static SingleInstance? Instance { get; private set; }

    /// <summary>An <c>agnes://</c> link this launch was started with, handled once when the app comes up.</summary>
    internal static string? LaunchLink { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        LaunchLink = args.FirstOrDefault(UriScheme.IsSchemeArgument);

        // One window per machine. Agnes reaches as many hosts as you like from a single window, so a second
        // copy has nothing to add and plenty to break — it would compete for the same saved tabs and split
        // your sessions in two. If one is already running, hand it what we were opened with and stop here.
        Instance = SingleInstance.TryClaim("agnes-desktop", LaunchLink ?? SingleInstance.ActivateOnly);
        if (Instance is null)
        {
            return;
        }

        // Make agnes:// links clickable from a browser, a QR scan or a terminal — re-pointed at this
        // executable each launch, in case it moved. Best-effort: a machine that refuses it still runs fine.
        UriScheme.Register();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // NOTE: Inter is deliberately not registered (`.WithInterFont()`): the bundled
            // Avalonia.Fonts.Inter has broken weight matching on Avalonia 12.1 — its "Inter" family
            // renders weight 400 (Normal) with a bold face (400 and 700 look identical), so all body
            // text came out bold. The app's type is the embedded Multitudal set instead (Manrope /
            // Space Grotesk / JetBrains Mono, see Themes/Tokens.axaml), which weighs correctly.
            .LogToTrace();
}
