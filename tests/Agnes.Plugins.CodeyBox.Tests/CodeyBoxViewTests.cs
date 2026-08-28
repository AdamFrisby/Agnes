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
