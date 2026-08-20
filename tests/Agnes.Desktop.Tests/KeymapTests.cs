using Agnes.App.Desktop.Keymaps;
using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client.Simulation;
using Agnes.Ui.Core;
using Avalonia.Input;

namespace Agnes.Desktop.Tests;

public sealed class KeymapTests
{
    private const string OneRule = """
        [{ "key": "ctrl+t", "command": "agnes.tab.new", "when": "window" }]
        """;

    [Theory]
    [InlineData("CTRL+1", Key.D1, KeyModifiers.Control, "Ctrl+1")]
    [InlineData("cmd+shift+enter", Key.Enter, KeyModifiers.Meta | KeyModifiers.Shift, "Cmd+Shift+Enter")]
    [InlineData("Option+PageDown", Key.PageDown, KeyModifiers.Alt, "Alt+PageDown")]
    [InlineData("f8", Key.F8, KeyModifiers.None, "F8")]
    public void Gestures_are_case_insensitive_and_normalized(string text, Key key, KeyModifiers modifiers, string display)
    {
        Assert.True(KeyGestureParser.TryParse(text, out var gesture, out var error), error);
        Assert.Equal(key, gesture.Key);
        Assert.Equal(modifiers, gesture.KeyModifiers);
        Assert.Equal(display, KeyGestureParser.Display(gesture));
    }

    [Theory]
    [InlineData("ctrl+k ctrl+s", "Chords")]
    [InlineData("hyper+x", "modifier")]
    [InlineData("ctrl+notakey", "Unknown key")]
    public void Unsupported_gestures_are_rejected(string text, string message)
    {
        Assert.False(KeyGestureParser.TryParse(text, out _, out var error));
        Assert.Contains(message, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rules_support_comments_trailing_commas_aliases_removals_blockers_and_later_precedence()
    {
        const string overrides = """
            [
              // replace this key, while adding another key as an alias
              { "key": "ctrl+t", "command": "agnes.tab.close", "when": "window" },
              { "key": "alt+t", "command": "agnes.tab.close", "when": "window" },
              { "key": "ctrl+t", "command": "-agnes.tab.close", "when": "window" },
              { "key": "ctrl+t", "command": "", "when": "window" },
            ]
            """;

        Assert.True(KeymapLoader.TryResolve([("default", OneRule), ("user", overrides)], out var map, out var diagnostic), diagnostic?.ToString());
        Assert.Contains(map.Rules, r => r.Command == AgnesCommand.TabClose && KeyGestureParser.Display(r.Gesture) == "Alt+T");
        Assert.Contains(map.Rules, r => r.Command is null && KeyGestureParser.Display(r.Gesture) == "Ctrl+T");
        Assert.DoesNotContain(map.Rules, r => r.Command == AgnesCommand.TabNew);
    }

    [Fact]
    public void Removing_a_later_override_reveals_the_earlier_matching_rule()
    {
        const string overrides = """
            [
              { "key": "ctrl+t", "command": "agnes.tab.close", "when": "window" },
              { "key": "ctrl+t", "command": "-agnes.tab.close", "when": "window" }
            ]
            """;

        Assert.True(KeymapLoader.TryResolve([("default", OneRule), ("user", overrides)], out var map, out var diagnostic), diagnostic?.ToString());
        AssertGesture(map, AgnesCommand.TabNew, "Ctrl+T");
        Assert.DoesNotContain(map.Rules, r => r.Command == AgnesCommand.TabClose);
    }

    [Fact]
    public void A_later_layer_replaces_inherited_bindings_for_the_same_command_and_context()
    {
        const string defaults = """
            [
              { "key": "ctrl+t", "command": "agnes.tab.new", "when": "window" },
              { "key": "alt+t", "command": "agnes.tab.new", "when": "window" }
            ]
            """;
        const string overrides = """
            [
              { "key": "cmd+t", "command": "agnes.tab.new", "when": "window" },
              { "key": "cmd+n", "command": "agnes.tab.new", "when": "window" }
            ]
            """;

        Assert.True(KeymapLoader.TryResolve([("default", defaults), ("user", overrides)], out var map, out var diagnostic), diagnostic?.ToString());
        Assert.Equal(
            ["Cmd+T", "Cmd+N"],
            map.Rules.Where(r => r.Command == AgnesCommand.TabNew).Select(r => KeyGestureParser.Display(r.Gesture)));
    }

    [Theory]
    [InlineData("noSuchContext", "agnes.tab.new", "context")]
    [InlineData("window && session", "agnes.tab.new", "Boolean")]
    [InlineData("window", "agnes.unknown", "command")]
    public void Semantic_errors_are_line_aware(string context, string command, string expected)
    {
        var json = $"[\n  {{ \"key\": \"ctrl+t\", \"command\": \"{command}\", \"when\": \"{context}\" }}\n]";
        Assert.False(KeymapLoader.TryResolve([("keymap.json", json)], out _, out var diagnostic));
        Assert.Equal(2, diagnostic!.Line);
        Assert.Contains(expected, diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Packaged_defaults_and_macos_overrides_only_reference_known_bindable_entries()
    {
        var defaults = File.ReadAllText(RepoFile("src/Agnes.App.Desktop/Assets/Keymaps/default.json"));
        var mac = File.ReadAllText(RepoFile("src/Agnes.App.Desktop/Assets/Keymaps/macos.json"));
        Assert.True(KeymapLoader.TryResolve([("default.json", defaults)], out var regular, out var regularError), regularError?.ToString());
        Assert.True(KeymapLoader.TryResolve([("default.json", defaults), ("macos.json", mac)], out var macos, out var macError), macError?.ToString());

        Assert.All(regular.Rules.Where(r => r.Command is not null), r => Assert.Contains(r.Context, CommandCatalogue.Definition(r.Command!.Value).Contexts));
        Assert.All(macos.Rules.Where(r => r.Command is not null), r => Assert.Contains(r.Context, CommandCatalogue.Definition(r.Command!.Value).Contexts));
        Assert.Equal(Enum.GetValues<KeymapContext>().Order(), regular.Rules.Select(r => r.Context).Distinct().Order());

        AssertGesture(macos, AgnesCommand.TabNew, "Cmd+T");
        AssertGesture(macos, AgnesCommand.TabClose, "Cmd+W");
        AssertGesture(macos, AgnesCommand.PaletteOpen, "Cmd+K");
        AssertGesture(macos, AgnesCommand.DashboardOpen, "Cmd+Shift+D");
        AssertGesture(macos, AgnesCommand.SessionFindOpen, "Cmd+F");
        AssertGesture(macos, AgnesCommand.ComposerSend, "Cmd+Enter");
        AssertGesture(macos, AgnesCommand.ComposerSendNow, "Cmd+Shift+Enter");
        Assert.DoesNotContain(macos.Rules, r =>
            (r.Command is AgnesCommand.TabNew or AgnesCommand.TabClose or AgnesCommand.PaletteOpen
                or AgnesCommand.DashboardOpen or AgnesCommand.SessionFindOpen or AgnesCommand.ComposerSend or AgnesCommand.ComposerSendNow
                || r.Command is >= AgnesCommand.TabPosition1 and <= AgnesCommand.TabPosition9)
            && r.Gesture.KeyModifiers.HasFlag(KeyModifiers.Control));
        for (var position = AgnesCommand.TabPosition1; position <= AgnesCommand.TabPosition9; position++)
        {
            AssertGesture(macos, position, $"Cmd+{(int)position - (int)AgnesCommand.TabPosition1 + 1}");
        }
        Assert.Contains(macos.Rules, r => r.Command == AgnesCommand.TabNext && KeyGestureParser.Display(r.Gesture) == "Ctrl+Tab");
        Assert.Contains(macos.Rules, r => r.Command == AgnesCommand.ComposerRecallPrevious && KeyGestureParser.Display(r.Gesture) == "Alt+Up");
    }

    [Fact]
    public void Common_defaults_preserve_the_complete_windows_and_linux_binding_surface()
    {
        var defaults = File.ReadAllText(RepoFile("src/Agnes.App.Desktop/Assets/Keymaps/default.json"));
        Assert.True(KeymapLoader.TryResolve([("default.json", defaults)], out var map, out var error), error?.ToString());
        var actual = string.Join('\n', map.Rules.Select(r => $"{r.Context.Id()}|{KeyGestureParser.Display(r.Gesture)}|{r.Command?.Id() ?? "<block>"}"));
        const string expected = """
            window|Ctrl+T|agnes.tab.new
            window|Ctrl+W|agnes.tab.close
            window|Ctrl+Tab|agnes.tab.next
            window|Ctrl+Shift+Tab|agnes.tab.previous
            window|Ctrl+PageDown|agnes.tab.next
            window|Ctrl+PageUp|agnes.tab.previous
            window|Ctrl+1|agnes.tab.position1
            window|Ctrl+2|agnes.tab.position2
            window|Ctrl+3|agnes.tab.position3
            window|Ctrl+4|agnes.tab.position4
            window|Ctrl+5|agnes.tab.position5
            window|Ctrl+6|agnes.tab.position6
            window|Ctrl+7|agnes.tab.position7
            window|Ctrl+8|agnes.tab.position8
            window|Ctrl+9|agnes.tab.position9
            window|Ctrl+K|agnes.palette.open
            window|Ctrl+Shift+D|agnes.dashboard.open
            paletteFocus|Down|agnes.palette.next
            paletteFocus|Up|agnes.palette.previous
            paletteFocus|Enter|agnes.palette.run
            paletteFocus|Escape|agnes.palette.close
            allSessionsSearchFocus|Enter|agnes.search.allSessions
            settingsMcpSearchFocus|Enter|agnes.settings.mcp.search
            settingsPluginSearchFocus|Enter|agnes.settings.plugin.search
            settingsSkillSearchFocus|Enter|agnes.settings.skill.search
            settingsLaunchProfileRenameFocus|Enter|agnes.launchProfile.rename.commit
            settingsLaunchProfileRenameFocus|Escape|agnes.launchProfile.rename.cancel
            session|Ctrl+F|agnes.session.find.open
            session|Escape|agnes.session.find.close
            session|F3|agnes.session.find.next
            session|Shift+F3|agnes.session.find.previous
            session|F7|agnes.session.prompt.previous
            session|F8|agnes.session.prompt.next
            session|Ctrl+F8|agnes.session.change.next
            session|Ctrl+F7|agnes.session.change.previous
            sessionRenameFocus|Enter|agnes.session.rename.commit
            sessionRenameFocus|Escape|agnes.session.rename.cancel
            sessionTagFocus|Enter|agnes.session.tag.add
            sessionFindFocus|Enter|agnes.session.find.next
            sessionFindFocus|Shift+Enter|agnes.session.find.previous
            sessionFindFocus|Escape|agnes.session.find.close
            sessionReferenceFocus|Enter|agnes.session.reference.add
            composerFocus|Cmd+Enter|agnes.composer.send
            composerFocus|Ctrl+Enter|agnes.composer.send
            composerFocus|Ctrl+Shift+Enter|agnes.composer.sendNow
            composerFocus|Alt+Up|agnes.composer.recallPrevious
            composerFocus|Alt+Down|agnes.composer.recallNext
            """;
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task Live_create_save_replace_delete_keeps_the_last_good_map_on_invalid_saves()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agnes-keymap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "keymap.json");
        using var service = new KeymapService(OneRule, null, path);
        AssertGesture(service.Effective, AgnesCommand.TabNew, "Ctrl+T");

        await File.WriteAllTextAsync(path, "[{\"key\":\"alt+t\",\"command\":\"agnes.tab.new\",\"when\":\"window\"}]");
        await WaitUntilAsync(() => service.Effective.Rules.Any(r => KeyGestureParser.Display(r.Gesture) == "Alt+T"));
        Assert.DoesNotContain(service.Effective.Rules, r =>
            r.Command == AgnesCommand.TabNew && KeyGestureParser.Display(r.Gesture) == "Ctrl+T");

        await File.WriteAllTextAsync(path, "[{\"key\":\"alt+x\",\"command\":\"agnes.notKnown\",\"when\":\"window\"}]");
        await WaitUntilAsync(() => service.Diagnostic is not null);
        Assert.Contains(service.Effective.Rules, r => KeyGestureParser.Display(r.Gesture) == "Alt+T");

        await File.WriteAllTextAsync(path, "[ broken");
        await WaitUntilAsync(() => service.Diagnostic is not null);
        Assert.Contains(service.Effective.Rules, r => KeyGestureParser.Display(r.Gesture) == "Alt+T");

        var replacement = path + ".replacement";
        await File.WriteAllTextAsync(replacement, "[{\"key\":\"shift+t\",\"command\":\"agnes.tab.new\",\"when\":\"window\"}]");
        File.Move(replacement, path, true);
        await WaitUntilAsync(() => service.Effective.Rules.Any(r => KeyGestureParser.Display(r.Gesture) == "Shift+T"));

        File.Delete(path);
        await WaitUntilAsync(() => !service.UserFileExists && service.Diagnostic is null
            && service.Effective.Rules.Any(r => r.Command == AgnesCommand.TabNew
                && KeyGestureParser.Display(r.Gesture) == "Ctrl+T"));
        AssertGesture(service.Effective, AgnesCommand.TabNew, "Ctrl+T");
    }

    [Fact]
    public async Task Edit_creates_a_strict_empty_override_and_launches_the_resolved_path()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agnes-keymap-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "keymap.json");
        var launcher = new RecordingLauncher();
        using var service = new KeymapService(OneRule, null, path, launcher, watch: false);

        await service.EditAsync();

        Assert.Equal("[]\n", await File.ReadAllTextAsync(path));
        Assert.Equal(Path.GetFullPath(path), launcher.Path);
    }

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(1, "1 minute ago")]
    [InlineData(7, "7 minutes ago")]
    [InlineData(60, "1 hour ago")]
    [InlineData(180, "3 hours ago")]
    [InlineData(1440, "1 day ago")]
    public void Relative_loaded_age_uses_readable_units(int minutes, string expected)
    {
        var now = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, KeymapService.RelativeAge(now.AddMinutes(-minutes), now));
    }

    [Fact]
    public void Status_uses_the_latest_successfully_loaded_file_change_time()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agnes-keymap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "keymap.json");
        File.WriteAllText(path, OneRule);
        var now = DateTimeOffset.UtcNow;
        File.SetLastWriteTimeUtc(path, now.AddMinutes(-4).UtcDateTime);
        using var service = new KeymapService(OneRule, null, path, watch: false, timeProvider: new FixedTimeProvider(now));

        Assert.Contains("latest change 4 minutes ago", service.Status, StringComparison.OrdinalIgnoreCase);

        File.WriteAllText(path, "[ broken");
        Assert.False(service.Reload());
        Assert.StartsWith("Last save rejected", service.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_user_file_at_startup_keeps_packaged_bindings_and_reports_the_line()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agnes-keymap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "keymap.json");
        File.WriteAllText(path, "[\n broken");

        using var service = new KeymapService(OneRule, null, path, watch: false);

        AssertGesture(service.Effective, AgnesCommand.TabNew, "Ctrl+T");
        Assert.NotNull(service.Diagnostic);
        Assert.True(service.Diagnostic.Line >= 1);
    }

    [Fact]
    public void Settings_lists_unassigned_commands_and_searches_ids_contexts_and_effective_gestures()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agnes-keymap-" + Guid.NewGuid().ToString("N"));
        using var service = new KeymapService(OneRule, null, Path.Combine(directory, "keymap.json"), watch: false);
        var vm = NewVm(service, directory);

        Assert.Equal(CommandCatalogue.All.Count, vm.KeymapGroups.Sum(g => g.Commands.Count));
        Assert.Contains(vm.KeymapGroups.SelectMany(g => g.Commands), row => row.Gesture == "Unassigned");
        var tabNew = vm.KeymapGroups.SelectMany(g => g.Commands).Single(row => row.Command == AgnesCommand.TabNew);
        Assert.Equal("{ \"key\": \"ctrl+t\", \"command\": \"agnes.tab.new\", \"when\": \"window\" }", tabNew.JsonRule);
        Assert.True(tabNew.CanCopyJson);
        Assert.All(vm.KeymapGroups.SelectMany(g => g.Commands).Where(row => row.Gesture == "Unassigned"), row =>
        {
            Assert.Null(row.JsonRule);
            Assert.False(row.CanCopyJson);
        });

        vm.SettingsSearch = "ctrl+t";
        var gestureMatch = Assert.Single(vm.KeymapGroups.SelectMany(g => g.Commands));
        Assert.Equal(AgnesCommand.TabNew, gestureMatch.Command);
        Assert.Equal("keymap", vm.SettingsCategory);

        vm.SettingsSearch = "composerFocus";
        Assert.All(vm.KeymapGroups.SelectMany(g => g.Commands), row => Assert.Contains("composerFocus", row.Context, StringComparison.Ordinal));

        vm.SettingsSearch = "agnes.composer.sendNow";
        Assert.Equal(AgnesCommand.ComposerSendNow, Assert.Single(vm.KeymapGroups.SelectMany(g => g.Commands)).Command);

        vm.SettingsSearch = "keyboard";
        Assert.Equal(CommandCatalogue.All.Count, vm.KeymapGroups.Sum(g => g.Commands.Count));
        Assert.Equal("keymap", vm.SettingsCategory);
        Assert.True(vm.SettingsCategories.Single(category => category.Id == "keymap").IsVisible);
    }

    [Fact]
    public async Task Settings_reports_keymap_diagnostics_and_launcher_failures()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agnes-keymap-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "keymap.json");
        using var service = new KeymapService(OneRule, null, path, new ThrowingLauncher(), watch: false);
        var vm = NewVm(service, directory);

        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, "[\n broken");
        Assert.False(service.Reload());
        Assert.True(vm.HasKeymapDiagnostic);
        Assert.Contains("Line", vm.KeymapDiagnostic, StringComparison.Ordinal);

        File.Delete(path);
        await vm.EditKeymapCommand.ExecuteAsync(null);
        Assert.Contains("Couldn't open the keymap", vm.KeymapDiagnostic, StringComparison.Ordinal);
    }

    private static void AssertGesture(EffectiveKeymap map, AgnesCommand command, string gesture)
        => Assert.Contains(map.Rules, r => r.Command == command && KeyGestureParser.Display(r.Gesture) == gesture);

    private static string RepoFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Agnes.Core.slnf"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Repository root not found."), relative);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    internal static MainWindowViewModel NewVm(KeymapService service, string directory)
        => new(
            new SimulatedConnector(),
            ImmediateDispatcher.Instance,
            new SessionStateStore(Path.Combine(directory, "tabs.json")),
            new HostRegistryStore(Path.Combine(directory, "hosts.json")),
            archiveStore: new SessionStateStore(Path.Combine(directory, "archive.json")),
            settingsStore: new SettingsStore(Path.Combine(directory, "settings.json")),
            keymap: service);

    private sealed class RecordingLauncher : IKeymapLauncher
    {
        public string? Path { get; private set; }
        public Task LaunchAsync(string path) { Path = path; return Task.CompletedTask; }
    }

    private sealed class ThrowingLauncher : IKeymapLauncher
    {
        public Task LaunchAsync(string path) => throw new InvalidOperationException("No JSON association");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
