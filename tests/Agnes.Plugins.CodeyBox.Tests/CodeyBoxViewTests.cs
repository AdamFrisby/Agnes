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
}
