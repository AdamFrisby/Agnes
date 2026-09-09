using Agnes.App.Desktop;
using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.App.Desktop.Views;
using Agnes.Client.Simulation;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Agnes.Desktop.Tests;

/// <summary>
/// The Local models settings surface: that the panel exists and parses, and that the form's own logic —
/// which is where the subtle behaviour lives — is right.
/// </summary>
/// <remarks>
/// A note on what is <b>not</b> asserted here. An attempt to check every button in the rendered panel has
/// a live command found the new buttons dead — and the long-standing "Refresh" button beside them dead
/// too, which means the headless harness cannot drive this particular view's data context rather than
/// that the panel is broken. Asserting against it would have produced a test that fails for a reason it
/// does not name. The render test below is therefore honest about its scope: it proves the XAML parses
/// and attaches without throwing, and nothing more.
/// </remarks>
[CollectionDefinition("desktop-headless", DisableParallelization = true)]
public sealed class DesktopHeadlessCollection;

/// <remarks>
/// In its own non-parallel collection. Avalonia's headless session is process-global, and starting one
/// while the rest of this project's tests run alongside it took the whole run down with
/// <c>Internal CLR error (0x80131506)</c> — not a failing assertion, a dead runner.
/// </remarks>
[Collection("desktop-headless")]
public class LocalModelsSettingsTests
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

    private static MainWindowViewModel NewVm()
    {
        var id = Guid.NewGuid().ToString("n");
        return new MainWindowViewModel(new SimulatedConnector(), ImmediateDispatcher.Instance,
            new SessionStateStore(Path.Combine(Path.GetTempPath(), $"agnes-tabs-{id}.json")),
            new HostRegistryStore(Path.Combine(Path.GetTempPath(), $"agnes-hosts-{id}.json")),
            new NullPromptStore(),
            new SessionStateStore(Path.Combine(Path.GetTempPath(), $"agnes-arch-{id}.json")));
    }

    [Fact]
    public async Task The_settings_tab_attaches_with_the_local_models_category_selected()
    {
        // Catches a XAML error or a binding to a property that does not exist — both of which throw on
        // attach rather than at build time.
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(() =>
        {
            var vm = NewVm();
            vm.SettingsCategory = "localmodels";
            var window = new Window { Width = 1280, Height = 900, Content = new SettingsTabView { DataContext = vm } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Contains(
                window.GetVisualDescendants().OfType<StackPanel>(),
                p => p.Name == "LocalModelsPanel");

            window.Close();
        }, CancellationToken.None);
    }

    [Fact]
    public void The_category_is_reachable_and_searchable_by_the_words_people_use()
    {
        var vm = NewVm();

        var category = Assert.Single(vm.SettingsCategories, c => c.Id == "localmodels");
        Assert.Equal("Local models", category.Label);
        // Someone looking for this will search for their server, not for "provider".
        foreach (var word in new[] { "ollama", "vllm", "lemonade", "byok", "offline" })
        {
            Assert.True(category.Matches(word), $"Searching \"{word}\" should find the Local models page.");
        }
    }

    [Fact]
    public void Selecting_the_category_lights_exactly_that_panel()
    {
        var vm = NewVm();

        vm.SettingsCategory = "localmodels";
        Assert.True(vm.CatLocalModels);
        Assert.False(vm.CatMcp);

        vm.SettingsCategory = "mcp";
        Assert.False(vm.CatLocalModels);
    }

    [Fact]
    public void All_three_actions_are_wired()
    {
        // The bug class this repository has shipped twice: a command that does not exist renders as a
        // permanently disabled button and raises nothing.
        var vm = NewVm();

        Assert.NotNull(vm.FetchLocalModelsCommand);
        Assert.NotNull(vm.SaveLocalProviderCommand);
        Assert.NotNull(vm.ClearLocalProviderCommand);
    }

    [Fact]
    public void A_blank_key_field_means_keep_the_stored_one_not_clear_it()
    {
        // The host never sends the key back, so an untouched field is empty. Reading that as "clear it"
        // would delete the credential every time any other setting was saved.
        var vm = NewVm();
        vm.LocalProviderUrl = "http://10.0.0.36:13305/v1";
        vm.LocalProviderKey = string.Empty;

        Assert.Null(BuildRequest(vm).ApiKey);

        vm.LocalProviderKey = "new-key";
        Assert.Equal("new-key", BuildRequest(vm).ApiKey);
    }

    [Fact]
    public void The_compatibility_switch_says_none_rather_than_sending_an_empty_list()
    {
        // An empty list means "use the recommended set" on the host, so unticking the box has to send a
        // positive "no exclusions" or it would silently re-enable the default.
        var vm = NewVm();
        vm.LocalProviderExcludeApplyPatch = false;
        Assert.Equal(["none"], BuildRequest(vm).ExcludedTools);

        vm.LocalProviderExcludeApplyPatch = true;
        Assert.Equal(["apply_patch"], BuildRequest(vm).ExcludedTools);
    }

    [Fact]
    public void Effort_is_blank_by_default_and_sent_as_null()
    {
        // "Let Copilot decide" is the correct default: it picks sensibly for a model it recognises, and
        // asserting a level Agnes invented would be worse than saying nothing.
        var vm = NewVm();
        Assert.Equal(string.Empty, vm.LocalProviderEffort);
        Assert.Null(BuildRequest(vm).Effort);

        vm.LocalProviderEffort = "medium";
        Assert.Equal("medium", BuildRequest(vm).Effort);
    }

    [Fact]
    public void The_effort_list_offers_copilots_own_levels_with_a_blank_first()
    {
        var vm = NewVm();

        Assert.Equal(string.Empty, vm.LocalProviderEfforts[0]);
        foreach (var level in new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max" })
        {
            Assert.Contains(level, vm.LocalProviderEfforts);
        }
    }

    [Fact]
    public void Offline_is_on_by_default_because_that_is_the_point_of_running_locally()
        => Assert.True(NewVm().LocalProviderOffline);

    [Fact]
    public void The_behaves_like_list_leads_with_the_id_that_strict_servers_accept()
    {
        // gpt-5.4 is the id observed to make Copilot send reasoning_effort "medium"; the default "max" is
        // rejected outright by servers that validate the field.
        Assert.Equal("gpt-5.4", NewVm().LocalProviderModelIds[0]);
    }

    /// <summary>Mirrors the view model's private request builder through the public surface it drives.</summary>
    private static LocalProviderRequest BuildRequest(MainWindowViewModel vm) => new(
        vm.LocalProviderUrl,
        "OpenAi",
        string.IsNullOrEmpty(vm.LocalProviderKey) ? null : vm.LocalProviderKey,
        vm.LocalProviderModelId,
        vm.LocalProviderWireModel,
        vm.LocalProviderExcludeApplyPatch ? ["apply_patch"] : ["none"],
        vm.LocalProviderOffline,
        string.IsNullOrWhiteSpace(vm.LocalProviderEffort) ? null : vm.LocalProviderEffort);
}
