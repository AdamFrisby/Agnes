using Agnes.Abstractions;
using Agnes.App.Desktop;
using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client;
using Agnes.Client.Cache;
using Agnes.Client.Simulation;
using Agnes.Ui.Core.Onboarding;

namespace Agnes.Screenshots;

/// <summary>
/// Renders one Settings category inside the real window and captures it — the way to look at a settings
/// page without driving a desktop.
/// </summary>
/// <remarks>
/// The window is built over the simulator like the tour. Pages that read this device's own state are
/// given a stand-in: the Local cache page gets a throwaway cache seeded with two sessions, so the frame
/// shows the page with something in it rather than its empty state.
///
/// <code>
/// dotnet run --project tools/Agnes.Screenshots -- --settings cache --out shots
/// </code>
/// </remarks>
public static class SettingsCapture
{
    public sealed record Options
    {
        public required string Category { get; init; }
        public string OutDir { get; init; } = "screenshots/settings";
        public string Theme { get; init; } = "Dark";
        public int Width { get; init; } = 1400;
        public int Height { get; init; } = 900;
    }

    public static Options? TryParse(string[] args)
    {
        string? Value(string name)
        {
            var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.Ordinal));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        if (Value("--settings") is not { Length: > 0 } category)
        {
            return null;
        }

        return new Options
        {
            Category = category,
            OutDir = Value("--out") ?? Path.Combine(Directory.GetCurrentDirectory(), "screenshots", "settings"),
            Theme = Value("--theme") ?? "Dark",
            Width = int.TryParse(Value("--width"), out var w) ? w : 1400,
            Height = int.TryParse(Value("--height"), out var h) ? h : 900,
        };
    }

    public static void Run(Options options)
    {
        Directory.CreateDirectory(options.OutDir);
        var statePath = Path.Combine(options.OutDir, "tabs-state.json");
        var hostsPath = Path.Combine(options.OutDir, "hosts.json");
        File.Delete(statePath);
        File.Delete(hostsPath);

        var cachePath = Path.Combine(options.OutDir, "seed-cache", "events.db");
        if (File.Exists(cachePath))
        {
            File.Delete(cachePath);
        }
        var cache = SqliteSessionEventCache.Open(cachePath);
        Seed(cache).GetAwaiter().GetResult();
        Console.WriteLine("seeded cache");

        var vm = new MainWindowViewModel(
            new SimulatedConnector(),
            new AvaloniaDispatcher(),
            new SessionStateStore(statePath),
            new HostRegistryStore(hostsPath),
            onboarding: new InMemoryOnboardingStore(new OnboardingState(WizardCompleted: true)),
            eventCache: cache);
        var window = new MainWindow { DataContext = vm, Width = options.Width, Height = options.Height };
        window.Show();
        MainWindowViewModel.ApplyTheme(options.Theme);
        vm.Showcase.Dismiss();
        vm.RestoreAsync();
        Program.Settle(300);
        Console.WriteLine("window up");

        vm.OpenSettingsCommand.Execute(null);
        vm.SettingsCategory = options.Category;
        Program.Settle(1200);
        Console.WriteLine("settings open");
        Program.Capture(window, $"settings-{options.Category.ToLowerInvariant()}.png");
        Console.WriteLine("captured");
        window.Close();
        cache.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static async Task Seed(SqliteSessionEventCache cache)
    {
        var t0 = new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero);
        List<SessionEvent> Log(int count, string text) => Enumerable.Range(1, count)
            .Select(i => (SessionEvent)new MessageChunkEvent(MessageRole.Assistant, new TextContent($"{text} {i}: " + new string('x', 400)))
            {
                Sequence = i,
                Timestamp = t0.AddSeconds(i),
            })
            .ToList();
        // ConfigureAwait(false): this runs on the headless UI thread, which is blocked waiting for it.
        await cache.WriteAsync("https://10.0.0.188:5081", "51850c9f929b4c7783a5c13bc74c51e8", 0, Log(9000, "dawn2")).ConfigureAwait(false);
        await cache.WriteAsync("https://10.0.0.188:5081", "530c96bdf213486faf0a7b0682d5abf4", 2400, Log(3000, "extra").Where(e => e.Sequence > 2400).ToList()).ConfigureAwait(false);
    }
}
