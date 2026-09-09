using Agnes.App.Desktop;
using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Ui.Core.Onboarding;
using Agnes.Ui.Core.ViewModels;
using Avalonia.Controls;

namespace Agnes.Screenshots;

/// <summary>
/// The desktop window against a <b>real</b> host, photographing the one thing a simulator cannot prove
/// about the agent's status line: that a real agent, given a real task and never told to report, reaches
/// for <c>report_status</c> because the standing nudge told it to.
/// </summary>
/// <remarks>
/// <para>
/// The simulated tour's <c>03s-agent-status-away</c> shot renders the same views, and it passes on a build
/// where the tool does not exist, the nudge never reaches a model, and the host drops every report on the
/// floor — because the simulator writes the <c>AgentStatusEvent</c> itself. This mode writes nothing: the
/// only way a line appears in these shots is that a model in a sandbox called the tool over MCP and the
/// host appended, broadcast and replayed it down the shipping path.
/// </para>
/// <para>
/// It is a sibling of <see cref="LiveCapture"/> rather than a flag on it because it wants the opposite
/// session: that one needs a screen and no conversation, this one needs a conversation and no screen.
/// Both share the harness's one honest fiction — <see cref="SessionViewModel.NoteUserInteraction(DateTimeOffset)"/>
/// back-dates *when the person last looked*, which is the only input to the "while you were away" band
/// that no amount of waiting on an agent can produce.
/// </para>
/// </remarks>
public static class LiveStatusCapture
{
    /// <summary>Opening a sandboxed session boots a VM — and, the first time a project is used, bakes its
    /// image first (apt, the agent CLIs, a disk copy). Twenty minutes, because the run that discovers that
    /// cost is exactly the run that must not give up before it.</summary>
    private const int OpenTimeoutMs = 1_200_000;

    /// <summary>A real model doing a real task on a busy machine. Ten minutes, per the brief.</summary>
    private const int TurnTimeoutMs = 600_000;

    /// <summary>How long the person is pretended to have been away for the "while you were away" shot.</summary>
    private static readonly TimeSpan AwayFor = TimeSpan.FromMinutes(8);

    public static void Run(LiveCapture.Options options)
    {
        Directory.CreateDirectory(options.OutDir);

        // The tab and host stores live beside the shots, never in the user's real profile.
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

        // ListSessionsAsync over the real connection, not a guess: the picker's own catalogue.
        Program.Pump(() => doc.HostSessions.HasSessions, 30_000);
        Console.WriteLine($"catalogue: {doc.HostSessions.Sessions.Count} session(s) — "
            + string.Join(" | ", doc.HostSessions.Sessions.Select(s => $"{s.AdapterId}:{s.SessionId[..Math.Min(8, s.SessionId.Length)]}")));

        // A model the host no longer serves fails the turn on its first token, so settle that before the
        // session is asked to do anything — including on a session that is merely being joined, where the
        // switch relaunches the CLI under the model this run actually wants.
        if (options.ModelId is { Length: > 0 } model && options.SessionId is { Length: > 0 } target)
        {
            var switching = doc.Host!.SwitchModelAsync(target, model);
            Program.Pump(() => switching.IsCompleted, 120_000);
            Console.WriteLine(switching.IsCompletedSuccessfully
                ? $"session {target} switched to model {model}."
                : $"couldn't switch {target} to {model}: {switching.Exception?.GetBaseException().Message}");
        }

        if (options.SessionId is { Length: > 0 } joining)
        {
            Program.Pump(() => doc.HostSessions.HasSessions, 60_000);
            var row = doc.HostSessions.Sessions.FirstOrDefault(s => s.SessionId == joining)
                ?? throw new InvalidOperationException($"The host lists no session {joining}.");
            doc.HostSessions.AttachCommand.Execute(row);
            Console.WriteLine($"joining session {joining}.");
        }
        else
        {
            Open(doc, options);
        }

        Program.Pump(() => doc.Session is not null, OpenTimeoutMs);
        if (doc.Session is null)
        {
            Console.Error.WriteLine("The session never opened. Tab status: " + doc.StatusText);
            Program.Capture(window, "live-status-00-failed-to-open.png");
            return;
        }

        var session = doc.Session!;
        Console.WriteLine($"session {session.SessionId} open (sandbox: {doc.UseSandbox}); working dir {doc.WorkingDirectory}");

        // Every distinct status the client is handed, with the wall-clock moment it landed. This is the
        // record the run exists to produce; the pictures are the corroboration.
        var reports = new List<(DateTimeOffset At, string Text)>();
        var started = DateTimeOffset.Now;
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionViewModel.LatestStatus)
                && session.LatestStatus is { Length: > 0 } line
                && (reports.Count == 0 || reports[^1].Text != line))
            {
                reports.Add((DateTimeOffset.Now, line));
                Console.WriteLine($"  status +{(DateTimeOffset.Now - started).TotalSeconds:F0}s: {line}");
            }
        };

        if (options.Prompt is { Length: > 0 } prompt)
        {
            started = DateTimeOffset.Now;
            session.PromptText = prompt;
            session.SendCommand.Execute(null);
            Console.WriteLine($"prompt sent at {started:HH:mm:ss}: {prompt}");

            // A turn that has not begun yet is not a turn that has ended: wait for Running before waiting
            // for its absence, or the first Pump returns instantly on a session that is still idle.
            Program.Pump(() => session.IsWorking, 120_000);
            Program.Pump(() => !session.IsWorking, TurnTimeoutMs);
            Console.WriteLine($"turn ended after {(DateTimeOffset.Now - started).TotalSeconds:F0}s "
                + $"(still working: {session.IsWorking}).");

            // The host coalesces reports into a window (Agnes:Status:MinIntervalSeconds), so the last thing
            // the agent said may still be pending when the turn ends. Give that window room to close.
            Program.Settle(options.StatusGraceMs);
        }

        Console.WriteLine(reports.Count == 0
            ? "NO STATUS REPORTED."
            : $"{reports.Count} status report(s); latest: \"{session.LatestStatus}\" ({session.StatusAge}).");
        foreach (var (at, text) in reports)
        {
            Console.WriteLine($"  [{at:HH:mm:ss} = +{(at - started).TotalSeconds:F0}s] {text}");
        }

        // ---- 1. the status line under the tab's header ----
        session.NoteUserInteraction();   // attended: an ordinary tab someone is looking at
        Program.Settle(200);
        Shot(vm, window, "live-status-01-session.png");

        // ---- 2. the "while you were away" band ----
        // The agent's work is real and so is its status; the only staged fact is when the person last
        // looked, which is the one thing the band is actually about.
        session.NoteUserInteraction(DateTimeOffset.Now - AwayFor);
        Program.Settle(200);
        if (!session.HasAwayStatus)
        {
            Console.Error.WriteLine("The away band is empty — "
                + (session.HasStatus ? "the agent has not acted since the back-dated visit." : "there is no status."));
        }

        Shot(vm, window, "live-status-02-away-band.png");
        session.NoteUserInteraction();
        Program.Settle(100);

        // ---- 3. the dashboard: every session's line, without opening any of them ----
        vm.OpenDashboardCommand.Execute(null);
        Program.Pump(() => vm.Dashboard is { HasLive: true }, 30_000);
        Program.Pump(() => vm.Dashboard!.HasElsewhere, 15_000);
        Program.Settle(600);
        Shot(vm, window, "live-status-03-dashboard.png");

        if (options.Stop)
        {
            var stopping = doc.Host!.StopSessionAsync(session.SessionId);
            Program.Pump(() => stopping.IsCompleted, 120_000);
            Console.WriteLine(stopping.IsCompletedSuccessfully
                ? $"stopped session {session.SessionId} — its sandbox is shut down."
                : $"couldn't stop session {session.SessionId}: {stopping.Exception?.GetBaseException().Message}");
        }

        Console.WriteLine($"session-id: {session.SessionId}");
        Console.WriteLine($"live status shots in {options.OutDir}");
    }

    private static void Open(SessionDocument doc, LiveCapture.Options options)
    {
        if (options.WorkingDirectory is { Length: > 0 } cwd)
        {
            doc.WorkingDirectory = cwd;
        }

        doc.SkipPermissions = options.Autonomous;
        var agent = doc.Agents!.FirstOrDefault(a => a.AdapterId == options.AdapterId)
            ?? throw new InvalidOperationException($"The host offers no '{options.AdapterId}' agent.");
        if (!agent.Available)
        {
            Console.Error.WriteLine($"warning: the host reports {agent.AdapterId} unavailable; starting anyway.");
        }

        doc.SelectAgentChoiceCommand.Execute(agent);

        // --model applies to a NEW session too: the free-text id overrides the picker, so the open request
        // carries exactly the model this run asked for rather than whatever the catalogue listed first.
        if (options.ModelId is { Length: > 0 } modelId)
        {
            Program.Pump(() => doc.HasModels, 30_000);
            doc.CustomModelId = modelId;
            Console.WriteLine($"model for the new session: {modelId}");
        }

        doc.StartSessionCommand.Execute(null);
        Console.WriteLine($"opening a {options.AdapterId} session "
            + $"(sandbox: {doc.UseSandbox}, autonomous: {doc.SkipPermissions}).");
    }

    /// <summary>A capture with the one-time "Link a GitHub account?" nudge dismissed first — it is raised by
    /// the open itself and sits across the top of every shot otherwise.</summary>
    private static void Shot(MainWindowViewModel vm, Window window, string name)
    {
        vm.ShowGitHubLinkPrompt = false;
        Program.Settle(120);
        Program.Capture(window, name);
    }
}
