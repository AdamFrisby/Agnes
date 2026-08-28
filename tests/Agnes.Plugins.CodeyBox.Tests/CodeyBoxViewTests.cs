using Agnes.Plugins.CodeyBox.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// Renders the plugin's view for real, in a headless session — the same mechanism the repo's screenshot
/// and mobile-preview tools use.
/// </summary>
/// <remarks>
/// These exist because the view compiling proves very little. The tab shipped with its section buttons
/// passing <c>CommandParameter="Queue"</c> — a string — to a command typed on
/// <see cref="CodeyBoxSection"/>. CommunityToolkit throws on that mismatch from <c>CanExecute</c>, which
/// Avalonia calls while attaching the button to the logical tree, so opening the tab took the whole
/// application down rather than merely failing a click. Only attaching the control catches that, so that
/// is what these do.
/// </remarks>
/// <summary>
/// Avalonia's headless session is process-global, so two classes starting one at the same time deadlock.
/// Both rendering classes join this collection, which xunit runs serially.
/// </summary>
[CollectionDefinition("avalonia-headless", DisableParallelization = true)]
public sealed class HeadlessCollection;

[Collection("avalonia-headless")]
public class CodeyBoxViewTests
{
    /// <summary>A minimal app carrying just enough theme for the control's resource lookups to resolve.</summary>
    private sealed class TestApp : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());
    }

    public static class TestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }

    private static CodeyBoxQueueViewModel NewViewModel(bool configured = true)
        => new(new CodeyBoxClient(new CodeyBoxOptions("http://codeybox.test", "k")),
               action => { action(); return Task.CompletedTask; },
               configured);

    /// <summary>Shows the control in a real window, which is what forces bindings and command parameters
    /// to be evaluated — the step that turns "it built" into "it opens".</summary>
    private static void Render(Action<CodeyBoxQueueViewModel>? arrange = null, bool configured = true)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        session.Dispatch(() =>
        {
            var vm = NewViewModel(configured);
            arrange?.Invoke(vm);
            var window = new Window { Content = new CodeyBoxQueueView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    [Theory]
    [InlineData(CodeyBoxSection.Queue)]
    [InlineData(CodeyBoxSection.Fleet)]
    [InlineData(CodeyBoxSection.Supervision)]
    [InlineData(CodeyBoxSection.Suggestions)]
    [InlineData(CodeyBoxSection.Releases)]
    [InlineData(CodeyBoxSection.Projects)]
    [InlineData(CodeyBoxSection.Testing)]
    [InlineData(CodeyBoxSection.Setup)]
    [InlineData(CodeyBoxSection.Diagnostics)]
    public void Every_section_renders_without_throwing(CodeyBoxSection section)
        => Render(vm => vm.Sections.Section = section); // the assertion is that this does not throw

    [Fact]
    public void The_unconfigured_state_renders_too()
        // A machine with no CodeyBox is ordinary, and its "configure me" pane must open like any other.
        => Render(configured: false);

    [Fact]
    public void Section_buttons_hand_the_command_a_real_enum_value()
    {
        // The exact shape of the crash: a string parameter reaching a RelayCommand<CodeyBoxSection>.
        // CanExecute is the method that threw, so calling it directly is the closest thing to the failure.
        var vm = NewViewModel();

        foreach (var section in Enum.GetValues<CodeyBoxSection>())
        {
            Assert.True(vm.Sections.ShowCommand.CanExecute(section));
        }

        Assert.Throws<ArgumentException>(() => vm.Sections.ShowCommand.CanExecute("Queue"));
    }

    [Fact]
    public void The_new_work_item_form_renders()
    {
        // Its own state rather than a section, so it has to be rendered explicitly to be covered.
        Render(vm => vm.IsCreating = true);
    }

    [Fact]
    public void The_detail_pane_renders()
        // Timeline, diff, costs and the rest, plus the priority and prompt editors.
        => Render(vm => vm.IsDetailVisible = true);

    [Fact]
    public void The_row_detail_pane_renders_when_something_has_been_opened()
        // Shared by releases, projects and suggestions, and only visible once one has been opened.
        => Render(vm => vm.Sections.RowDetail = "── release\n{}");

    [Fact]
    public void The_confirmation_bar_renders_when_something_irreversible_is_armed()
        => Render(vm => vm.Confirmation.Ask("Abandon", "43c8ec28", () => Task.CompletedTask));

    [Fact]
    public void The_secondary_action_row_renders_when_expanded()
        // Replay, resume, recover, uncancel and the two destructive ones, hidden until asked for.
        => Render(vm => vm.ShowMoreActions = true);

    [Fact]
    public void An_irreversible_action_does_not_run_until_it_is_confirmed()
    {
        // The point of the bar: arming must not act. Cancel and abandon used to fire on the first click,
        // from a row where they looked exactly like retry.
        var ran = false;
        var confirmation = new Confirmation();

        confirmation.Ask("Abandon", "43c8ec28", () => { ran = true; return Task.CompletedTask; });

        Assert.True(confirmation.IsPending);
        Assert.False(ran);
        Assert.Contains("43c8ec28", confirmation.Prompt, StringComparison.Ordinal);
        Assert.Contains("cannot be undone", confirmation.Prompt, StringComparison.OrdinalIgnoreCase);

        confirmation.ConfirmCommand.Execute(null);
        Assert.True(ran);
        Assert.False(confirmation.IsPending);
    }

    [Fact]
    public void Dismissing_a_confirmation_discards_the_action()
    {
        var ran = false;
        var confirmation = new Confirmation();
        confirmation.Ask("Cancel", "b7b3e663", () => { ran = true; return Task.CompletedTask; });

        confirmation.DismissCommand.Execute(null);

        Assert.False(confirmation.IsPending);
        Assert.False(ran);

        // And a later confirm cannot resurrect what was dismissed.
        confirmation.ConfirmCommand.Execute(null);
        Assert.False(ran);
    }

    [Fact]
    public void The_current_section_is_named_rather_than_only_highlighted()
    {
        // The rail shows which destination is current; the pane title says it in words, so location does
        // not depend on spotting which of nine buttons looks pressed.
        var vm = NewViewModel();

        vm.Sections.Section = CodeyBoxSection.Supervision;
        Assert.Equal("Supervision", vm.Sections.SectionTitle);

        vm.Sections.Section = CodeyBoxSection.Queue;
        Assert.Equal("Work queue", vm.Sections.SectionTitle);
    }

    /// <summary>A queue shaped like the live one: mostly finished, a little actionable.</summary>
    private static void Seed(CodeyBoxQueueViewModel vm)
    {
        static WorkItemRow Make(string id, string title, string state, string agent, string project, int priority, bool depsOk = true)
            => new(id, title, state, agent, project, 0, DateTimeOffset.UtcNow, null,
                   Priority: priority, CreatedAt: DateTimeOffset.UtcNow.AddDays(-1), DependsOnSatisfied: depsOk);

        vm.Load(
        [
            Make("aaaa1111", "Fix quota detection", "Failed", "claude", "codeybox-self", 55),
            Make("bbbb2222", "Circuit breaker", "Queued", "codex", "codeybox-self", 13),
            Make("cccc3333", "Landed change", "Done", "claude", "jobtrack-cli", 90),
            Make("dddd4444", "Waiting on a dependency", "Queued", "codex", "jobtrack-self", 5, depsOk: false),
        ]);
    }

    [Fact]
    public void The_queue_pane_renders_with_its_controls()
        => Render(Seed);

    [Fact]
    public void Grouping_by_project_renders()
        => Render(vm => { Seed(vm); vm.GroupByProject = true; });

    [Fact]
    public void The_create_form_offers_projects_rather_than_asking_for_an_id()
    {
        // The form used to require the project id typed exactly — knowledge the interface already had.
        var vm = NewViewModel();
        vm.Projects.Add(new ProjectChoice("codeybox-self", "CodeyBox (self-modify)", "codex"));

        vm.ToggleCreateCommand.Execute(null);

        Assert.True(vm.IsCreating);
        Assert.NotNull(vm.NewProject);
        Assert.Equal("codeybox-self", vm.NewProject.Id);
        Assert.Contains("codex", vm.NewProjectAgent, StringComparison.Ordinal);
    }
}

/// <summary>
/// Renders the item pane with an item actually SELECTED. The tests above render it empty, where every
/// element of the header, the fact row, the action bar and the failure box is collapsed and its bindings
/// are never evaluated — which is precisely the blind spot that let a bad CommandParameter reach a user.
/// </summary>
[Collection("avalonia-headless")]
public class ItemPaneRenderTests
{
    private sealed class TestApp : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());
    }

    public static class TestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }

    /// <summary>
    /// Answers every request instantly with a JSON <c>null</c>, which each client getter already coalesces
    /// to an empty result. Without it, selecting an item sends these tests at a real socket: the reads
    /// fail asynchronously, after the headless session they belong to has been torn down, and the run
    /// aborts on the resulting noise. The pane's rendering is what is under test, not its fetching.
    /// </summary>
    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private static void RenderWith(WorkItemRow row, Action<CodeyBoxQueueViewModel>? arrange = null)
    {
        CodeyBoxQueueViewModel? vm = null;
        try
        {
            using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
            session.Dispatch(() =>
            {
                vm = new CodeyBoxQueueViewModel(
                    new CodeyBoxClient(new CodeyBoxOptions("http://127.0.0.1:1", "k"), new OfflineHandler()),
                    action => { action(); return Task.CompletedTask; });
                vm.Load([row]);
                vm.Filter = QueueFilter.All;
                vm.Selected = row;
                arrange?.Invoke(vm);

                var window = new Window { Width = 1280, Height = 800, Content = new CodeyBoxQueueView { DataContext = vm } };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.Close();
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            // Selecting an item starts following the stdout hub, and that connection reconnects on its
            // own. Left undisposed, one per test, they keep the test host alive indefinitely — the run
            // does not fail, it simply never ends.
            vm?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static WorkItemRow Row(
        string state = "Working",
        string? lastError = null,
        string? failureKind = null,
        string? prompt = "do the thing",
        int quotaRetryAttempts = 0,
        int? pr = null,
        string? cancellationSource = null,
        IReadOnlyList<string>? dependsOn = null)
        => new(
            Id: "3f2b1c00-0000-0000-0000-000000000001",
            Title: "Make the pane readable",
            State: state,
            Agent: "claude",
            ProjectId: "codeybox-self",
            QueuePosition: 1,
            UpdatedAt: DateTimeOffset.UtcNow,
            LastError: lastError,
            Prompt: prompt,
            DependsOn: dependsOn,
            Priority: 3,
            CreatedAt: DateTimeOffset.UtcNow.AddHours(-2),
            DependsOnSatisfied: true,
            FailureKind: failureKind,
            MergedPrNumber: pr,
            MergedPrUrl: pr is null ? null : $"https://github.com/AdamFrisby/CodeyBox/pull/{pr}",
            WorkBranch: "codeybox/1f98bff9",
            QuotaRetryAttempts: quotaRetryAttempts,
            CancellationSource: cancellationSource,
            RepositoryUrl: "https://github.com/AdamFrisby/CodeyBox.git");

    [Fact]
    public void Renders_a_running_item() => RenderWith(Row());

    [Fact]
    public void Renders_a_failed_item_with_its_failure_box()
        => RenderWith(Row(state: "Failed", lastError: "Incus inventory entries must contain a JSON object property named 'config'.", failureKind: "infrastructure"));

    [Fact]
    public void Renders_a_cancelled_item_without_claiming_it_failed()
        => RenderWith(Row(state: "Cancelled", lastError: "superseded", cancellationSource: "operator"));

    [Fact]
    public void Renders_an_item_waiting_on_quota()
        => RenderWith(Row(quotaRetryAttempts: 2));

    [Fact]
    public void Renders_a_merged_item_with_its_pr_link()
        => RenderWith(Row(state: "Done", pr: 362));

    [Fact]
    public void Renders_an_item_with_dependencies()
        => RenderWith(Row(state: "Queued", dependsOn: ["a", "b"]));

    [Fact]
    public void Renders_a_very_long_task_collapsed_and_expanded()
    {
        // The median prompt here is 2,726 characters and the longest is 10,207.
        var long_ = new string('x', 10_207);
        RenderWith(Row(prompt: long_));
        RenderWith(Row(prompt: long_), vm => vm.IsTaskExpanded = true);
    }

    [Fact]
    public void Renders_the_new_creation_options()
        => RenderWith(Row(), vm => { vm.IsCreating = true; vm.ShowCreateOptions = true; });

    [Fact]
    public void Renders_the_diff_view()
        => RenderWith(Row(), vm =>
        {
            vm.IsDiffVisible = true;
            foreach (var line in UnifiedDiff.Parse(
                "diff --git a/x.cs b/x.cs\n--- a/x.cs\n+++ b/x.cs\n@@ -1 +1 @@\n-old\n+new\n"))
            {
                vm.Diff.Add(line);
            }
        });

    [Fact]
    public void Renders_the_gate_summary_and_progress_bar()
        => RenderWith(Row(state: "Failed", lastError: "audit blocked"), vm =>
        {
            vm.IsTimelineVisible = true;
            // Shapes taken from the live instance: a gate that blocks repeatedly, one that never has, and
            // an item well past its 25-iteration budget.
            vm.Gates.Add(new GateSummary("completeness:llm-review", 9, 7, true, DateTimeOffset.UtcNow));
            vm.Gates.Add(new GateSummary("security:gitleaks", 9, 0, false, DateTimeOffset.UtcNow));
            vm.Progress = new AuditProgress(52, 25);
        });

    [Fact]
    public void Renders_every_view_of_the_pane()
    {
        // The view state is set directly rather than through the commands: those also kick off a
        // background read whose completion would land on Avalonia objects after this headless session has
        // been torn down. What is under test here is that each view renders, not what fetches it.
        RenderWith(Row(), vm => vm.IsTimelineVisible = true);
        RenderWith(Row(), vm => vm.IsDetailVisible = true);
        RenderWith(Row(), vm => vm.IsDiffVisible = true);
        RenderWith(Row(), vm => { vm.IsTimelineVisible = false; vm.IsDetailVisible = false; });
    }

    [Fact]
    public void The_view_switch_reports_exactly_one_active_view()
    {
        // F6: the operator could not tell which of the three views they were in. Whatever the pane is
        // showing, exactly one chip must read as on.
        var vm = new CodeyBoxQueueViewModel(
            new CodeyBoxClient(new CodeyBoxOptions("http://127.0.0.1:1", "k"), new OfflineHandler()),
            action => { action(); return Task.CompletedTask; });

        Assert.Equal(1, new[] { vm.IsOutputView, vm.IsTimelineView, vm.IsDetailView }.Count(x => x));

        vm.IsTimelineVisible = true;
        Assert.True(vm.IsTimelineView);
        Assert.Equal(1, new[] { vm.IsOutputView, vm.IsTimelineView, vm.IsDetailView }.Count(x => x));

        vm.IsTimelineVisible = false;
        vm.IsDetailVisible = true;
        Assert.True(vm.IsDetailView);
        Assert.Equal(1, new[] { vm.IsOutputView, vm.IsTimelineView, vm.IsDetailView }.Count(x => x));
    }

    [Fact]
    public void Renders_the_disclosed_recovery_actions()
        => RenderWith(Row(state: "Failed", lastError: "boom"), vm => vm.ToggleMoreActionsCommand.Execute(null));
}

/// <summary>
/// Renders each non-queue section with content in it. The existing section test renders them EMPTY, which
/// is the state four of them are permanently in on this host — so it exercised almost none of their
/// bindings. These populate first.
/// </summary>
[Collection("avalonia-headless")]
public class SectionContentRenderTests
{
    private sealed class TestApp : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());
    }

    public static class TestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("null", System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private static void Render(CodeyBoxSection section, Action<CodeyBoxQueueViewModel> arrange)
    {
        CodeyBoxQueueViewModel? vm = null;
        try
        {
            using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
            session.Dispatch(() =>
            {
                vm = new CodeyBoxQueueViewModel(
                    new CodeyBoxClient(new CodeyBoxOptions("http://127.0.0.1:1", "k"), new OfflineHandler()),
                    action => { action(); return Task.CompletedTask; });
                vm.Sections.Section = section;
                arrange(vm);

                var window = new Window { Width = 1280, Height = 800, Content = new CodeyBoxQueueView { DataContext = vm } };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.Close();
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            vm?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    [Fact]
    public void Renders_the_dashboard_in_the_state_this_host_is_actually_in()
        => Render(CodeyBoxSection.Dashboard, vm =>
        {
            // Ten queued, every one dependency-blocked: the stall banner, the "0 runnable" tile and a
            // Next up list whose head cannot start.
            var blocked = Enumerable.Range(0, 10).Select(i => new WorkItemRow(
                Id: $"{i:x32}", Title: $"Blocked item {i}", State: "Queued", Agent: "claude",
                ProjectId: "codeybox-self", QueuePosition: i, UpdatedAt: DateTimeOffset.UtcNow,
                LastError: null, DependsOn: ["other"], Priority: 19 - i, DependsOnSatisfied: false)).ToList();

            foreach (var tile in Dashboard.Tiles(blocked, queuePaused: false, slotsInUse: 0, slotsTotal: 3))
            {
                vm.Sections.Tiles.Add(tile);
            }

            foreach (var next in Dashboard.NextUp(blocked))
            {
                vm.Sections.NextUp.Add(next);
            }

            vm.Sections.IsStalled = true;
            vm.Sections.Concurrency = new Concurrency(3, 0, new Dictionary<string, int> { ["claude"] = 3 });
            vm.Sections.Quota.Add(new QuotaProbe(
                "codex", "gpt-5.6-sol", "Subscription", false, null, true, 0,
                new QuotaSnapshot(55, true, DateTimeOffset.UtcNow.AddHours(6))));
            vm.Sections.Fleet.Add(new FleetProject(
                "codeybox-self", "CodeyBox", 10, 0, null, false, true, null, 92617m, null, "ok",
                ["Done", "Failed", "Done"]));
            vm.Sections.HealthIsMeaningful = false;
            vm.Sections.HealthLabel = Dashboard.HealthLabel(1.0, 0);
            vm.Sections.SpendLabel = "$92,617 spent across 404 items";
        });

    [Fact]
    public void Renders_the_dashboard_when_everything_is_healthy()
        => Render(CodeyBoxSection.Dashboard, vm =>
        {
            var busy = new[]
            {
                new WorkItemRow("a", "Running", "Working", "claude", "p", 0, DateTimeOffset.UtcNow, null),
                new WorkItemRow("b", "Waiting", "Queued", "claude", "p", 1, DateTimeOffset.UtcNow, null),
            };

            foreach (var tile in Dashboard.Tiles(busy, queuePaused: false, slotsInUse: 1, slotsTotal: 3))
            {
                vm.Sections.Tiles.Add(tile);
            }

            vm.Sections.HealthIsMeaningful = true;
            vm.Sections.HealthLabel = Dashboard.HealthLabel(0.95, 40);
        });

    [Fact]
    public void Renders_suggestions_with_a_populated_backlog()
        => Render(CodeyBoxSection.Suggestions, vm =>
        {
            vm.Sections.Suggestions.Add(new Suggestion(
                "1", "src", "codeybox-self", "Fix the sandbox leak",
                "The Incus provider leaks a volume per failed launch.",
                "security", "important", "medium", DateTimeOffset.UtcNow, "open", null,
                ["src/Sandbox/Incus.cs", "src/Sandbox/Broker.cs"]));
            vm.Sections.SuggestionCategories.Add("security");
        });

    [Fact]
    public void Renders_fleet_with_budgets_and_outcome_strips()
        => Render(CodeyBoxSection.Fleet, vm =>
        {
            // One project inside budget, one over it — both bar paths.
            vm.Sections.Fleet.Add(new FleetProject(
                "codeybox-self", "CodeyBox", 3, 1, "work", false, false, null, 40m, 100m, "ok",
                ["Done", "Done", "Failed", "Cancelled", "Done"]));
            vm.Sections.Fleet.Add(new FleetProject(
                "jobtrack", "JobTrack", 0, 0, null, true, true, "paused by operator", 180m, 100m, "over",
                ["Failed", "Failed"]));
        });

    [Fact]
    public void Renders_diagnostics_with_quota_and_capacity_promoted()
        => Render(CodeyBoxSection.Diagnostics, vm =>
        {
            vm.Sections.Concurrency = new Concurrency(3, 3, new Dictionary<string, int> { ["claude"] = 3 });
            // One healthy, one nearly exhausted — the low path is the one that colours.
            vm.Sections.Quota.Add(new QuotaProbe(
                "codex", "gpt-5.6-sol", "Subscription", false, null, true, 0,
                new QuotaSnapshot(55, true, DateTimeOffset.UtcNow.AddHours(6))));
            vm.Sections.Quota.Add(new QuotaProbe(
                "claude", "claude-opus-5", "Subscription", false, null, false, 12,
                new QuotaSnapshot(4, true, DateTimeOffset.UtcNow.AddHours(2))));
        });

    [Fact]
    public void Renders_projects_and_releases_with_content()
    {
        Render(CodeyBoxSection.Projects, vm => vm.Sections.Projects.Add(new Project(
            "codeybox-self", "CodeyBox", "https://github.com/AdamFrisby/CodeyBox.git", "main", "codex", 25,
            ["security", "architecture"])));

        Render(CodeyBoxSection.Releases, vm =>
            vm.Sections.Templates.Add(new TaskTemplate("asvs5", "templates/asvs5.json", 104, null)));
    }
}
