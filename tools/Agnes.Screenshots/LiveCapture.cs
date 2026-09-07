using Agnes.App.Desktop;
using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Ui.Core.Onboarding;
using Avalonia.Controls;

namespace Agnes.Screenshots;

/// <summary>
/// The same desktop window as <see cref="Program"/>, pointed at a <b>real</b> Agnes host instead of the
/// simulator: the real <c>SignalRConnector</c>, a real device token, a real graphical sandbox, and a real
/// screen arriving over the display channel.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists at all, given the simulated <c>04sc-screen-panel</c> shot already shows a Screen panel:
/// the simulator paints a synthetic desktop into the same view models, so that shot passes on a build where
/// the display channel does not work. It cannot tell a decoded guest framebuffer from a drawing. This mode
/// can, because everything between the Avalonia view and the VM's virtio-gpu is the shipping code path —
/// pairing token, pinned TLS, SignalR, the WebSocket, the JPEG.
/// </para>
/// <para>
/// It is deliberately a *mode of the screenshot tool* rather than a test. It needs a host, an Incus daemon
/// and minutes of VM boot, so it can never run in CI; what it produces is pictures for a human to look at,
/// which is exactly what this tool is for.
/// </para>
/// </remarks>
public static class LiveCapture
{
    /// <summary>Opening a graphical session boots a VM: minutes, not seconds, on a busy machine.</summary>
    private const int OpenTimeoutMs = 600_000;

    /// <summary>First pixels lag "the session is open" — Xorg and the session script start after the guest
    /// reports ready, and the capture waits for a scanout that has not happened yet.</summary>
    private const int FirstFrameTimeoutMs = 180_000;

    public sealed record Options
    {
        public required string HostUrl { get; init; }
        public required string Token { get; init; }
        public string? Fingerprint { get; init; }
        public string OutDir { get; init; } = "screenshots/live";

        /// <summary>Join this session instead of opening one. Mutually exclusive with <see cref="AdapterId"/>.</summary>
        public string? SessionId { get; init; }

        public string AdapterId { get; init; } = "opencode";
        public string? WorkingDirectory { get; init; }
        public string Theme { get; init; } = "Dark";

        /// <summary>Stop the session on the host once the shots are taken. Off by default — a run that joined
        /// somebody else's session must not close it — and when on it does what closing the tab does: the
        /// agent stops and the sandbox VM is shut down, kept for resume.</summary>
        public bool Stop { get; init; }

        /// <summary>Window size. Bigger than the simulated tour's default: the Screen panel is docked beside
        /// the transcript, so a 1280x800 guest in a 1180-wide window is squeezed into a third of it.</summary>
        public int Width { get; init; } = 1700;
        public int Height { get; init; } = 1000;
    }

    /// <summary>Parses the live-mode arguments, or null when <c>--host</c> was not given.</summary>
    public static Options? TryParse(string[] args)
    {
        string? Value(string name)
        {
            var i = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.Ordinal));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        if (Value("--host") is not { Length: > 0 } host)
        {
            return null;
        }

        return new Options
        {
            HostUrl = host,
            Token = Value("--token") ?? throw new ArgumentException("--host also needs --token (a paired device token)."),
            Fingerprint = Value("--fingerprint"),
            OutDir = Value("--out") ?? Path.Combine(Directory.GetCurrentDirectory(), "screenshots", "live"),
            SessionId = Value("--session"),
            AdapterId = Value("--agent") ?? "opencode",
            WorkingDirectory = Value("--cwd"),
            Theme = Value("--theme") ?? "Dark",
            Stop = args.Contains("--stop", StringComparer.Ordinal),
            Width = int.TryParse(Value("--width"), out var w) ? w : 1700,
            Height = int.TryParse(Value("--height"), out var h) ? h : 1000,
        };
    }

    public static void Run(Options options)
    {
        Directory.CreateDirectory(options.OutDir);

        // The tab and host stores live beside the shots, not in the user's real profile: this harness must
        // not adopt (or disturb) the hosts and tabs of a desktop app the same person actually uses.
        var statePath = Path.Combine(options.OutDir, "tabs-state.json");
        var hostsPath = Path.Combine(options.OutDir, "hosts.json");
        File.Delete(statePath);
        var hostStore = new HostRegistryStore(hostsPath);
        hostStore.Save([new KnownHost("Live host", options.HostUrl, options.Token, options.Fingerprint)]);

        var vm = new MainWindowViewModel(
            new RoutingConnector(Path.Combine(Directory.GetCurrentDirectory(), "recordings")),
            new AvaloniaDispatcher(),
            new SessionStateStore(statePath),
            hostStore,
            onboarding: new InMemoryOnboardingStore(new OnboardingState(WizardCompleted: true)));
        var window = new MainWindow { DataContext = vm, Width = options.Width, Height = options.Height };
        window.Show();
        MainWindowViewModel.ApplyTheme(options.Theme);
        vm.Showcase.Dismiss();
        vm.RestoreAsync();
        Program.Settle(400);

        var doc = Program.LastTab(vm) ?? throw new InvalidOperationException("The restored window has no tab.");
        Program.Pump(() => doc.Hosts is { Count: > 0 });
        var choice = doc.Hosts!.FirstOrDefault(h => string.Equals(h.Url, options.HostUrl, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"The host picker never offered {options.HostUrl}.");
        choice.Select.Execute(null);
        Program.Pump(() => doc.Agents is { Count: > 0 }, 60_000);
        Console.WriteLine($"host: {doc.HostName} — agents: "
            + string.Join(", ", doc.Agents!.Select(a => a.AdapterId + (a.Available ? "" : " (unavailable)"))));

        if (options.SessionId is { Length: > 0 } joining)
        {
            Join(doc, joining);
        }
        else
        {
            Open(doc, options);
        }

        Program.Pump(() => doc.Session is not null, OpenTimeoutMs);
        if (doc.Session is null)
        {
            Console.Error.WriteLine("The session never opened. Tab status: " + doc.StatusText);
            Program.Capture(window, "live-00-failed-to-open.png");
            return;
        }

        var session = doc.Session!;
        Console.WriteLine($"session {session.SessionId} open; waiting for the host to report a display…");

        // HasDisplay comes from the open's own answer, but a joined session learns it from the catalogue
        // instead — poll that too rather than assuming which path we came in by.
        Program.Pump(() => session.HasDisplay, 60_000);
        if (!session.HasDisplay)
        {
            Console.Error.WriteLine(doc.ScreenDeclined
                ? "The host opened the session WITHOUT a display (graphical sandboxes may be off)."
                : "The session reports no display.");
            Program.Capture(window, "live-00-no-display.png");
            return;
        }

        // ---- 1. the Screen panel, docked beside the transcript, showing the guest ----
        session.IsDisplayVisible = true;   // what ToggleDisplayCommand does; set directly so it can't toggle off
        var display = session.Display!;
        Program.Pump(() => display.LastFrameAt is not null, FirstFrameTimeoutMs);
        if (display.LastFrameAt is null)
        {
            Console.Error.WriteLine("No frame arrived. Display status: " + display.Status);
            Program.Capture(window, "live-00-no-frame.png");
            return;
        }

        Console.WriteLine($"first frame at {display.LastFrameAt:HH:mm:ss} — {display.Width}x{display.Height}");
        Program.Pump(() => display.Fps > 0, 4_000);   // past one rate window, so the readout says something
        Shot(vm, window, "live-01-screen-panel.png");

        // ---- 2. a person takes control: the amber "You have control" chip ----
        display.TakeControlCommand.Execute(null);
        Program.Pump(() => display.IsUserDriving, 15_000);
        Console.WriteLine($"driver after take: {display.DriverLabel}");
        Shot(vm, window, "live-02-user-has-control.png");

        // ---- 3. drive it: a right-click on the root window opens openbox's menu ----
        // Bottom-right on purpose — the guest session puts an xterm at +40+40, so a right-click in the
        // middle of the screen is an xterm gesture that paints nothing (see LiveGraphicalDisplayProbe).
        var before = display.LastFrameAt;
        _ = display.PointerMoveAsync(1180, 720);
        Program.Settle(150);
        _ = display.PointerButtonAsync(2, true);
        Program.Settle(80);
        _ = display.PointerButtonAsync(2, false);
        Program.Pump(() => display.LastFrameAt > before, 10_000);
        Program.Settle(600);   // let the menu finish painting before the shot
        Shot(vm, window, "live-03-right-click-menu.png");

        // Close the menu and hand the screen back, so the session is left as it was found.
        _ = display.KeyAsync("Escape", true);
        _ = display.KeyAsync("Escape", false);
        Program.Settle(300);
        display.ReleaseControlCommand.Execute(null);
        Program.Pump(() => !display.IsUserDriving, 10_000);

        if (options.Stop)
        {
            var stopping = doc.Host!.StopSessionAsync(session.SessionId);
            Program.Pump(() => stopping.IsCompleted, 60_000);
            Console.WriteLine(stopping.IsCompletedSuccessfully
                ? $"stopped session {session.SessionId} — its sandbox is shut down."
                : $"couldn't stop session {session.SessionId}: {stopping.Exception?.GetBaseException().Message}");
        }

        Console.WriteLine($"live shots in {options.OutDir}");
    }

    private static void Open(SessionDocument doc, Options options)
    {
        if (options.WorkingDirectory is { Length: > 0 } cwd)
        {
            doc.WorkingDirectory = cwd;
        }

        doc.GraphicalSandbox = true;   // also forces UseSandbox on — a screen lives at the VM boundary
        var agent = doc.Agents!.FirstOrDefault(a => a.AdapterId == options.AdapterId)
            ?? throw new InvalidOperationException($"The host offers no '{options.AdapterId}' agent.");
        if (!agent.Available)
        {
            Console.Error.WriteLine($"warning: the host reports {agent.AdapterId} unavailable; starting anyway.");
        }

        doc.SelectAgentChoiceCommand.Execute(agent);
        doc.StartSessionCommand.Execute(null);
        Console.WriteLine($"opening a graphical {options.AdapterId} session — this boots a VM.");
    }

    /// <summary>
    /// A capture with the one-time "Link a GitHub account?" nudge dismissed first. It is raised by the open
    /// itself and it sits across the top of the window, so without this every live shot photographs an
    /// onboarding banner instead of the thing it was taken for.
    /// </summary>
    private static void Shot(MainWindowViewModel vm, Window window, string name)
    {
        vm.ShowGitHubLinkPrompt = false;
        Program.Settle(120);
        Program.Capture(window, name);
    }

    private static void Join(SessionDocument doc, string sessionId)
    {
        Program.Pump(() => doc.HostSessions.HasSessions, 60_000);
        var row = doc.HostSessions.Sessions.FirstOrDefault(s => s.SessionId == sessionId)
            ?? throw new InvalidOperationException($"The host lists no session {sessionId}.");
        doc.HostSessions.AttachCommand.Execute(row);
        Console.WriteLine($"joining session {sessionId}.");
    }
}
