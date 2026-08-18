using Agnes.App.Desktop;
using Agnes.App.Desktop.Keymaps;
using Agnes.App.Desktop.ViewModels;
using Agnes.App.Desktop.Views;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Agnes.Desktop.Tests;

[Collection("Avalonia headless")]
public sealed class KeymapBindingTests
{
    [Fact]
    public async Task Binder_resolves_window_nested_session_composer_settings_search_and_templated_rename_contexts()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(KeymapTestApp));
        await session.Dispatch(() =>
        {
            using var state = CreateBindingState();
            AssertRepresentativeBindings(state);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Native_menu_and_composer_hint_follow_a_live_reload()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(KeymapTestApp));
        await session.Dispatch(() =>
        {
            using var state = CreatePresentationState();
            Assert.Equal(OperatingSystem.IsMacOS() ? "Cmd+T" : "Ctrl+T", GestureOf(MenuItem(state.MainWindow, "New Tab")));
            Assert.Equal(OperatingSystem.IsMacOS() ? "Cmd+Enter to send" : "Ctrl+Enter to send",
                state.SessionView.FindControl<TextBlock>("ComposerSendHint")?.Text);
            File.WriteAllText(state.Service.UserPath, """
                [
                  { "key": "ctrl+t", "command": "-agnes.tab.new", "when": "window" },
                  { "key": "alt+t", "command": "agnes.tab.new", "when": "window" },
                  { "key": "meta+enter", "command": "-agnes.composer.send", "when": "composerFocus" },
                  { "key": "ctrl+enter", "command": "-agnes.composer.send", "when": "composerFocus" },
                  { "key": "alt+enter", "command": "agnes.composer.send", "when": "composerFocus" },
                  { "key": "ctrl+shift+enter", "command": "-agnes.composer.sendNow", "when": "composerFocus" },
                  { "key": "alt+shift+enter", "command": "agnes.composer.sendNow", "when": "composerFocus" }
                ]
                """);
            Assert.True(state.Service.Reload(), state.Service.Diagnostic?.ToString());
            Assert.Equal("Alt+T", GestureOf(MenuItem(state.MainWindow, "New Tab")));
            Assert.Equal("Alt+Enter to send", state.SessionView.FindControl<TextBlock>("ComposerSendHint")?.Text);
            Assert.Equal("Alt+T", state.MainWindow.DataContext is MainWindowViewModel main
                ? main.PaletteItems.First(item => item.Label == "New tab").Hint
                : null);
            Assert.Contains(state.Composer.KeyBindings, b => GestureOf(b) == "Alt+Enter");
            Assert.DoesNotContain(state.Composer.KeyBindings, b => GestureOf(b) == "Ctrl+Enter");
        }, CancellationToken.None);
    }

    private static BindingState CreateBindingState()
    {
        var directory = TempDirectory();
        var service = KeymapService.CreateDefault(Path.Combine(directory, "settings.json"), watch: false);
        var main = KeymapTests.NewVm(service, directory);
        var sessionVm = SessionVm();
        var plugins = new PluginManagementViewModel(() => null, ImmediateDispatcher.Instance);
        var memory = new MemorySearchViewModel(() => null, ImmediateDispatcher.Instance);
        var profiles = new LaunchProfilesViewModel(() => null, ImmediateDispatcher.Instance);
        var profileRow = new LaunchProfileRowVm(new LaunchProfile("p1", "Default", "codex"));
        var document = new SessionDocument(main, ImmediateDispatcher.Instance);
        document.AttachSession(sessionVm);

        var root = new StackPanel();
        var windowTarget = Target(main, KeymapContext.Window);
        var sessionTarget = Target(sessionVm, KeymapContext.SessionFindFocus);
        var composerTarget = Target(sessionVm, KeymapContext.ComposerFocus);
        var settingsTarget = Target(plugins, KeymapContext.SettingsPluginSearchFocus);
        var searchTarget = Target(memory, KeymapContext.AllSessionsSearchFocus);
        var renameTarget = Target(profileRow, KeymapContext.SettingsLaunchProfileRenameFocus);
        var renameTemplateParent = new Border { DataContext = profiles, Child = renameTarget };
        var allCommandsTarget = new TextBox { DataContext = profileRow };
        Control allCommandsTree = allCommandsTarget;
        foreach (var dataContext in new object[] { profiles, main.PromptLibrary, plugins, memory, sessionVm, document, main })
            allCommandsTree = new Border { DataContext = dataContext, Child = allCommandsTree };
        root.Children.Add(windowTarget);
        root.Children.Add(sessionTarget);
        root.Children.Add(composerTarget);
        root.Children.Add(settingsTarget);
        root.Children.Add(searchTarget);
        root.Children.Add(renameTemplateParent);
        root.Children.Add(allCommandsTree);

        var host = new Window { Content = root };
        KeymapBinder.SetService(host, service);
        return new BindingState(service, host, main, sessionVm, plugins, memory, profiles, profileRow,
            windowTarget, sessionTarget, composerTarget, settingsTarget, searchTarget, renameTarget, allCommandsTarget);
    }

    private static void AssertRepresentativeBindings(BindingState state)
    {
        AssertBound(state.WindowTarget, OperatingSystem.IsMacOS() ? "Cmd+T" : "Ctrl+T", state.Main.NewTabCommand);
        AssertBound(state.SessionTarget, "Enter", state.Session.NextMatchCommand);
        AssertBound(state.ComposerTarget, OperatingSystem.IsMacOS() ? "Cmd+Enter" : "Ctrl+Enter", state.Session.SendCommand);
        AssertBound(state.SettingsTarget, "Enter", state.Plugins.SearchCommand);
        AssertBound(state.SearchTarget, "Enter", state.Memory.SearchCommand);
        var rename = AssertBound(state.RenameTarget, "Enter", state.Profiles.CommitRenameCommand);
        Assert.Same(state.ProfileRow, rename.CommandParameter);
        Assert.All(CommandCatalogue.All, definition => Assert.NotNull(definition.Bind(state.AllCommandsTarget)));
    }

    private static PresentationState CreatePresentationState()
    {
        var directory = TempDirectory();
        var service = KeymapService.CreateDefault(Path.Combine(directory, "settings.json"), watch: false);
        var main = KeymapTests.NewVm(service, directory);
        var mainWindow = new MainWindow { DataContext = main };
        mainWindow.InstallKeymap(service);

        var sessionVm = SessionVm();
        var document = new SessionDocument(main, ImmediateDispatcher.Instance);
        document.AttachSession(sessionVm);
        var sessionView = new SessionTabView { DataContext = document };
        var sessionWindow = new Window { Content = sessionView };
        KeymapBinder.SetService(sessionWindow, service);
        sessionView.InstallKeymap(service);
        var composer = Assert.IsType<TextBox>(sessionView.FindControl<TextBox>("Composer"));
        return new PresentationState(service, mainWindow, sessionWindow, sessionView, composer);
    }

    private static SessionViewModel SessionVm()
    {
        var view = new SessionView("s1");
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo("s1", "codex", string.Empty, 0), [], 0));
        return new SessionViewModel(new FakeHost(), view, ImmediateDispatcher.Instance, "Codex");
    }

    private static TextBox Target(object dataContext, KeymapContext context)
    {
        var target = new TextBox { DataContext = dataContext };
        KeymapBinder.SetContext(target, context);
        return target;
    }

    private static KeyBinding AssertBound(Control target, string gesture, System.Windows.Input.ICommand command)
    {
        var binding = Assert.Single(target.KeyBindings, b => GestureOf(b) == gesture);
        Assert.Same(command, binding.Command);
        return binding;
    }

    private static NativeMenuItem MenuItem(MainWindow window, string header)
        => Descendants(Assert.IsType<NativeMenu>(NativeMenu.GetMenu(window))).Single(item => Equals(item.Header, header));

    private static IEnumerable<NativeMenuItem> Descendants(NativeMenu menu)
    {
        foreach (var item in menu.Items.OfType<NativeMenuItem>())
        {
            yield return item;
            if (item.Menu is not null)
                foreach (var child in Descendants(item.Menu)) yield return child;
        }
    }

    private static string? GestureOf(KeyBinding binding)
        => binding.Gesture is null ? null : KeyGestureParser.Display(binding.Gesture);

    private static string? GestureOf(NativeMenuItem item)
        => item.Gesture is null ? null : KeyGestureParser.Display(item.Gesture);

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "agnes-keymap-headless-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record BindingState(
        KeymapService Service,
        Window Host,
        MainWindowViewModel Main,
        SessionViewModel Session,
        PluginManagementViewModel Plugins,
        MemorySearchViewModel Memory,
        LaunchProfilesViewModel Profiles,
        LaunchProfileRowVm ProfileRow,
        TextBox WindowTarget,
        TextBox SessionTarget,
        TextBox ComposerTarget,
        TextBox SettingsTarget,
        TextBox SearchTarget,
        TextBox RenameTarget,
        TextBox AllCommandsTarget) : IDisposable
    {
        public void Dispose() => Service.Dispose();
    }

    private sealed record PresentationState(
        KeymapService Service,
        MainWindow MainWindow,
        Window SessionWindow,
        SessionTabView SessionView,
        TextBox Composer) : IDisposable
    {
        public void Dispose() => Service.Dispose();
    }
}

public static class KeymapTestApp
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Agnes.App.Desktop.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia();
}

[CollectionDefinition("Avalonia headless", DisableParallelization = true)]
public sealed class AvaloniaHeadlessCollection;
