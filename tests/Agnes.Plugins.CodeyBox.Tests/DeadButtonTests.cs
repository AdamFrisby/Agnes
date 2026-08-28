using Agnes.Plugins.CodeyBox;
using Agnes.Plugins.CodeyBox.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// Asserts that no rendered button is dead.
///
/// <para><b>Why this exists.</b> Fifteen buttons across Suggestions, Fleet, Releases and Projects bound
/// their command to <c>$parent[ItemsControl].DataContext.SomeCommand</c> — but those commands live on the
/// <c>Sections</c> view model, and an <c>ItemsSource</c> does not change a control's DataContext, so every
/// one of those paths resolved to nothing. A binding that fails leaves <c>Command</c> null, and a button
/// with a null command is not an error: Avalonia simply renders it permanently disabled. It shipped, and
/// it took a person clicking on it to notice.</para>
///
/// <para>Nothing else in this suite could have caught it. The render tests assert that a screen does not
/// throw, and a screen full of dead buttons throws nothing at all. So this walks the visual tree of every
/// populated section and fails on any button that cannot be pressed.</para>
/// </summary>
[Collection("avalonia-headless")]
public class DeadButtonTests
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

    /// <summary>Renders one section with content and returns every button that has no command.</summary>
    private static List<string> DeadButtons(CodeyBoxSection section, Action<CodeyBoxQueueViewModel> populate)
    {
        var dead = new List<string>();
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
                populate(vm);

                var window = new Window { Width = 1400, Height = 900, Content = new CodeyBoxQueueView { DataContext = vm } };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                foreach (var button in window.GetVisualDescendants().OfType<Button>())
                {
                    // Only buttons that are actually on screen: a control inside a collapsed panel has no
                    // bindings evaluated yet and would be a false positive.
                    if (!button.IsEffectivelyVisible || button.Command is not null)
                    {
                        continue;
                    }

                    // A ToggleButton — CheckBox, RadioButton, ToggleSwitch — is driven by IsChecked, and
                    // having no command is its normal state rather than a broken binding.
                    if (button is Avalonia.Controls.Primitives.ToggleButton)
                    {
                        continue;
                    }

                    // Buttons generated inside another control's template (an Expander's header toggle, a
                    // ComboBox's drop-down) belong to that control, not to this view. A button written in
                    // this view's markup has no templated parent.
                    if (button.TemplatedParent is not null)
                    {
                        continue;
                    }

                    dead.Add($"{section}: \"{Describe(button)}\"");
                }

                window.Close();
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            vm?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        return dead;
    }

    private static string Describe(Button button) => button.Content switch
    {
        string text => text,
        StackPanel panel => string.Join(" ", panel.Children.OfType<TextBlock>().Select(t => t.Text)),
        _ => button.Content?.ToString() ?? button.Name ?? "(unnamed)",
    };

    private static WorkItemRow Row(string state = "Queued") => new(
        Id: "3f2b1c00000000000000000000000001", Title: "An item", State: state, Agent: "claude",
        ProjectId: "codeybox-self", QueuePosition: 1, UpdatedAt: DateTimeOffset.UtcNow, LastError: "boom",
        Prompt: "do it", Priority: 3, MergedPrNumber: 7,
        MergedPrUrl: "https://github.com/AdamFrisby/CodeyBox/pull/7");

    public static TheoryData<string> Sections => new(
        ["Dashboard", "Queue", "Suggestions", "Fleet", "Releases", "Projects", "Supervision", "Diagnostics"]);

    [Theory]
    [MemberData(nameof(Sections))]
    public void No_section_renders_a_button_that_cannot_be_pressed(string sectionName)
    {
        var section = Enum.Parse<CodeyBoxSection>(sectionName);

        var dead = DeadButtons(section, vm =>
        {
            // Every section is populated, because an empty list materialises no item template and would
            // hide exactly the bug this test exists for.
            vm.Load([Row()]);
            vm.Filter = QueueFilter.All;
            vm.Selected = Row();
            vm.ShowMoreActions = true;
            vm.IsCreating = true;
            vm.ShowCreateOptions = true;

            vm.Sections.Suggestions.Add(new Suggestion(
                "s1", "src", "codeybox-self", "A suggestion", "because", "docs", "important", "tiny",
                DateTimeOffset.UtcNow, "open", null, ["a.cs"]));
            vm.Sections.Fleet.Add(new FleetProject(
                "codeybox-self", "CodeyBox", 1, 0, null, false, false, null, 5m, 10m, "ok", ["Done"]));
            vm.Sections.PausedAgents.Add(new AgentPause("claude", "quota", true, DateTimeOffset.UtcNow, null, null));
            vm.Sections.Templates.Add(new TaskTemplate("asvs5", "templates/asvs5.json", 104, null));
            vm.Sections.Projects.Add(new Project(
                "codeybox-self", "CodeyBox", "https://github.com/AdamFrisby/CodeyBox.git", "main", "codex", 25, ["security"]));
            vm.Sections.Quota.Add(new QuotaProbe(
                "codex", "gpt-5.6-sol", "Subscription", false, null, true, 0,
                new QuotaSnapshot(55, true, DateTimeOffset.UtcNow)));
            vm.Sections.Concurrency = new Concurrency(3, 0, new Dictionary<string, int>());
            foreach (var tile in Dashboard.Tiles([Row()], false, 0, 3)) { vm.Sections.Tiles.Add(tile); }
            vm.Sections.NextUp.Add(Row());
        });

        Assert.Empty(dead);
    }
}
