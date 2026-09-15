using Agnes.Abstractions;
using Agnes.Protocol;
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

        /// <summary>A real host to load the pages from (with <see cref="Token"/> and <see cref="Fingerprint"/>),
        /// so the pages show what an operator actually sees — real projects, devices, sandboxes — instead of
        /// the simulator's nothing. Null renders over the simulator.</summary>
        public string? HostUrl { get; init; }
        public string? Token { get; init; }
        public string? Fingerprint { get; init; }
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
            HostUrl = Value("--host"),
            Token = Value("--token"),
            Fingerprint = Value("--fingerprint"),
        };
    }

    /// <summary>Every settings category, in the order the sidebar lists them — what <c>--settings all</c> walks.</summary>
    private static readonly string[] AllCategories =
    [
        "appearance", "cache", "keymap", "github", "devices", "sandboxes", "mcp", "localmodels",
        "projects", "plugins", "bugreport", "prompts", "profiles", "collaborators",
    ];

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

        var live = options.HostUrl is { Length: > 0 };
        var hostStore = new HostRegistryStore(hostsPath);
        if (live)
        {
            hostStore.Save([new KnownHost("Live host", options.HostUrl!, options.Token ?? throw new ArgumentException("--host needs --token"), options.Fingerprint)]);
        }

        var vm = new MainWindowViewModel(
            live ? new RoutingConnector(Path.Combine(Directory.GetCurrentDirectory(), "recordings")) : new SimulatedConnector(),
            new AvaloniaDispatcher(),
            new SessionStateStore(statePath),
            hostStore,
            onboarding: new InMemoryOnboardingStore(new OnboardingState(WizardCompleted: true)),
            eventCache: cache);
        var window = new MainWindow { DataContext = vm, Width = options.Width, Height = options.Height };
        window.Show();
        MainWindowViewModel.ApplyTheme(options.Theme);
        vm.Showcase.Dismiss();
        vm.RestoreAsync();
        Program.Settle(300);
        Console.WriteLine("window up");

        if (live)
        {
            // Connect the new-session tab to the host: the settings pages take "the host" from any connected
            // tab, so this is what makes them load real data rather than "open a session on a host first".
            var doc = Program.LastTab(vm) ?? throw new InvalidOperationException("The restored window has no tab.");
            Program.Pump(() => doc.Hosts is { Count: > 0 });
            var choice = doc.Hosts!.FirstOrDefault(h => string.Equals(h.Url, options.HostUrl, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"The host picker never offered {options.HostUrl}.");
            choice.Select.Execute(null);
            Program.Pump(() => doc.Agents is { Count: > 0 }, 60_000);
            Console.WriteLine($"host: {doc.HostName}");
        }

        vm.OpenSettingsCommand.Execute(null);
        var categories = string.Equals(options.Category, "all", StringComparison.OrdinalIgnoreCase) ? AllCategories : [options.Category];
        foreach (var category in categories)
        {
            vm.SettingsCategory = category;
            if (!live && string.Equals(category, "projects", StringComparison.OrdinalIgnoreCase))
            {
                SeedProject(vm);
            }
            // A live page is a round trip or three to the host; give it time to land before the shot.
            Program.Settle(live ? 3500 : 1200);
            Program.Capture(window, $"settings-{category.ToLowerInvariant()}.png");
            Console.WriteLine($"captured {category}");
        }
        window.Close();
        cache.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>The simulator has no projects, so the editor would be blank: one project with a device
    /// list and a host inventory, the way the page looks on a real host with a phone plugged in.</summary>
    private static void SeedProject(MainWindowViewModel vm)
    {
        var image = new SandboxImageDto("images:ubuntu/24.04/cloud", "agnes-baseline", true, ["git", "ripgrep"], [], [], []);
        vm.Projects.Add(new ProjectDto("p-default", "Default", "", image, [], null, new ProjectDefaultsDto()));
        var project = new ProjectDto(
            "p-agnes", "Agnes", "github.com/AdamFrisby/Agnes", image,
            [new McpServerInfo("m1", "playwright", "sandbox", true, "stdio", "npx", ["-y", "@playwright/mcp@latest"], new Dictionary<string, string>(), null, null)],
            null, new ProjectDefaultsDto(), null,
            SandboxDiskGiB: 40,
            UsbDevices: [new UsbDeviceDto("0e8d", "201c", null, "MediaTek Inc. Lenovo Tab M9")]);
        vm.Projects.Add(project);
        vm.Projects.Add(new ProjectDto("p-dawn2", "Dawn2", "github.com/AdamFrisby/Dawn2", image, [], null, new ProjectDefaultsDto()));
        vm.SelectProjectCommand.Execute(project);
        // One edit in flight, so the shot shows the "Unsaved changes" tag the editor's header carries.
        vm.ProjDiskGiB = "48";
        vm.HostUsbDevices.Add(new HostUsbDeviceDto("0e8d", "201c", "MediaTek Inc. Lenovo Tab M9", null, 7, 6, ["Vendor Specific Class"]));
        vm.HostUsbDevices.Add(new HostUsbDeviceDto("0403", "6001", "FTDI FT232 Serial (UART)", "FT1234", 3, 4, ["Vendor Specific Class"]));
        vm.HostUsbDevices.Add(new HostUsbDeviceDto("1b1c", "1b08", "Corsair K95W Gaming Keyboard", null, 1, 14, ["Human Interface Device"]));
        vm.HostUsbStatus = "3 devices on Local (sandboxed).";
        vm.ProjectsStatus = "3 projects on Local (sandboxed).";
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
