using System.Reflection;
using Agnes.App.Desktop;
using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client.Simulation;
using Agnes.Ui.Core.Onboarding;
using Dock.Model.Controls;

namespace Agnes.Screenshots;

/// <summary>
/// Renders a client plugin's custom screen inside the real desktop window — the tab strip, the dock, the
/// theme — and captures it, at the window's size and again tall enough that nothing scrolls.
/// </summary>
/// <remarks>
/// A plugin screen talks to whatever it talks to on its own (the CodeyBox screen reads the operator's
/// orchestrator from the same config file the CLI uses), so no Agnes host is involved: the window is
/// built over the simulator like the tour, and the plugin is loaded from the same directory the desktop
/// app loads it from. The screen's own view model is reached by reflection on purpose: this tool does not
/// reference plugins, and a plugin loaded through the app's plugin loader is not the type a project
/// reference would name anyway.
///
/// <code>
/// dotnet run --project tools/Agnes.Screenshots -- --screen codeybox.queue --section Dashboard --out shots
/// dotnet run --project tools/Agnes.Screenshots -- --screen codeybox.queue --section Queue --width 1683 --height 1095
/// </code>
/// </remarks>
public static class PluginScreenCapture
{
    private const int ReadyTimeoutMs = 120_000;

    public sealed record Options
    {
        public required string ScreenId { get; init; }
        public string Section { get; init; } = "Dashboard";
        public string OutDir { get; init; } = "screenshots/plugins";
        public string? PluginDirectory { get; init; }
        public string Theme { get; init; } = "Dark";
        public int Width { get; init; } = 1683;
        public int Height { get; init; } = 1095;
        public int TallHeight { get; init; } = 2600;
    }

    public static Options? TryParse(string[] args)
    {
        string? Value(string name)
        {
            var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.Ordinal));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        if (Value("--screen") is not { Length: > 0 } screen)
        {
            return null;
        }

        return new Options
        {
            ScreenId = screen,
            Section = Value("--section") ?? "Dashboard",
            OutDir = Value("--out") ?? Path.Combine(Directory.GetCurrentDirectory(), "screenshots", "plugins"),
            PluginDirectory = Value("--plugins"),
            Theme = Value("--theme") ?? "Dark",
            Width = int.TryParse(Value("--width"), out var w) ? w : 1683,
            Height = int.TryParse(Value("--height"), out var h) ? h : 1095,
            TallHeight = int.TryParse(Value("--tall"), out var t) ? t : 2600,
        };
    }

    public static void Run(Options options)
    {
        Directory.CreateDirectory(options.OutDir);
        var statePath = Path.Combine(options.OutDir, "tabs-state.json");
        var hostsPath = Path.Combine(options.OutDir, "hosts.json");
        File.Delete(statePath);
        File.Delete(hostsPath);

        var vm = new MainWindowViewModel(
            new SimulatedConnector(),
            new AvaloniaDispatcher(),
            new SessionStateStore(statePath),
            new HostRegistryStore(hostsPath),
            onboarding: new InMemoryOnboardingStore(new OnboardingState(WizardCompleted: true)),
            clientPluginDirectory: options.PluginDirectory);
        var window = new MainWindow { DataContext = vm, Width = options.Width, Height = options.Height };
        window.Show();
        MainWindowViewModel.ApplyTheme(options.Theme);
        vm.Showcase.Dismiss();
        vm.RestoreAsync();
        Program.Settle(400);

        var provider = vm.CustomScreens.FirstOrDefault(p => string.Equals(p.ScreenId, options.ScreenId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"No client plugin offers the screen '{options.ScreenId}'. Loaded: {string.Join(", ", vm.CustomScreens.Select(p => p.ScreenId))}");
        vm.OpenCustomScreen(provider);
        Program.Settle(400);

        var dock = (IDocumentDock)vm.Layout.VisibleDockables![0];
        var doc = dock.VisibleDockables!.OfType<PluginScreenDocument>().First();
        var screen = doc.ScreenViewModel;

        // Section selection and readiness, by name: the screen's view model is the plugin's own type.
        var sections = Property(screen, "Sections");
        var sectionProperty = sections?.GetType().GetProperty("Section");
        if (sections is not null && sectionProperty is not null)
        {
            sectionProperty.SetValue(sections, Enum.Parse(sectionProperty.PropertyType, options.Section, ignoreCase: true));
        }

        Program.Pump(() => IsReady(screen, sections, options.Section), ReadyTimeoutMs);
        Program.Settle(1500);

        var stem = $"{options.ScreenId}-{options.Section.ToLowerInvariant()}";
        Program.Capture(window, $"{stem}.png");

        // Everything, without a scrollbar in the way: the same window, made tall enough to hold it all.
        window.Height = options.TallHeight;
        Program.Settle(800);
        Program.Capture(window, $"{stem}-full.png");
        window.Close();
    }

    private static bool IsReady(object screen, object? sections, string section)
        => section.ToLowerInvariant() switch
        {
            "dashboard" or "overview" => Property(sections, "HasOverview") is true,
            "queue" => Property(screen, "HasBoard") is true,
            _ => true,
        };

    private static object? Property(object? target, string name)
        => target?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);
}
