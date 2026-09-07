using Agnes.Abstractions;
using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Desktop.Tests;

/// <summary>The on-demand Files/Terminal overlays must stay hidden on the host-picker/configure screen and
/// only appear once a session is live AND the panel is toggled on. Guards a regression where a XAML
/// MultiBinding with a <c>FallbackValue</c> string collapsed to a vacuously-true <c>And</c> and leaked the
/// panels over the "New session" picker.</summary>
public class OverlayPanelGateTests
{
    private static SessionView Live(string id = "s1")
    {
        var view = new SessionView(id);
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo(id, "opencode", string.Empty, 0), [], 0));
        return view;
    }

    [Fact]
    public void Overlays_stay_hidden_until_a_session_is_live_and_toggled()
    {
        var doc = new SessionDocument(new NullTabController(), ImmediateDispatcher.Instance);

        // Host-picker stage: no session yet.
        Assert.False(doc.IsLive);
        Assert.False(doc.TerminalPanelVisible);
        Assert.False(doc.FileBrowserPanelVisible);

        var vm = new SessionViewModel(new FakeHost(), Live(), ImmediateDispatcher.Instance, "OpenCode");
        doc.AttachSession(vm);

        // Live but nothing toggled: still hidden.
        Assert.True(doc.IsLive);
        Assert.False(doc.TerminalPanelVisible);
        Assert.False(doc.FileBrowserPanelVisible);

        vm.IsTerminalVisible = true;
        Assert.True(doc.TerminalPanelVisible);
        Assert.False(doc.FileBrowserPanelVisible);

        vm.IsFileBrowserVisible = true;
        Assert.True(doc.FileBrowserPanelVisible);

        vm.IsTerminalVisible = false;
        Assert.False(doc.TerminalPanelVisible);
    }

    /// <summary>
    /// The Screen overlay carries one gate the others do not: the session must actually HAVE a display. A
    /// screen panel offered on a session with no screen would open onto nothing, so both the panel and the
    /// chip that toggles it are absent until the host says otherwise.
    /// </summary>
    [Fact]
    public void The_screen_overlay_needs_a_display_as_well_as_a_live_toggled_session()
    {
        var doc = new SessionDocument(new NullTabController(), ImmediateDispatcher.Instance);
        var vm = new SessionViewModel(new FakeHost(), Live(), ImmediateDispatcher.Instance, "OpenCode");
        doc.AttachSession(vm);

        // Live, but the host's catalogue never said this session has a screen.
        Assert.False(doc.ScreenAvailable);
        Assert.False(doc.ScreenPanelVisible);
        Assert.Null(vm.Display);

        // Toggling it on cannot conjure one.
        vm.IsDisplayVisible = true;
        Assert.False(doc.ScreenPanelVisible);

        // Once the session has a display, the affordance appears and the toggle works.
        vm.HasDisplay = true;
        Assert.True(doc.ScreenAvailable);
        Assert.NotNull(vm.Display);
        Assert.True(doc.ScreenPanelVisible);

        vm.IsDisplayVisible = false;
        Assert.False(doc.ScreenPanelVisible);
        Assert.True(doc.ScreenAvailable); // the chip stays; only the panel closed
    }

    /// <summary>
    /// While the panel is closed, the tab's status bar says who is driving — that is the state you cannot see
    /// any other way. With the panel open its own header says it, so the chip stands down.
    /// </summary>
    [Fact]
    public void The_driver_chip_shows_only_while_the_panel_is_closed()
    {
        var doc = new SessionDocument(new NullTabController(), ImmediateDispatcher.Instance);
        var vm = new SessionViewModel(new FakeHost(), Live(), ImmediateDispatcher.Instance, "OpenCode");
        doc.AttachSession(vm);
        vm.HasDisplay = true;

        vm.Display!.Holder = DisplayControlHolder.Agent;
        Assert.True(doc.ShowScreenDriverChip);
        Assert.True(doc.ScreenDriverIsAgent);       // sky: something is in motion
        Assert.False(doc.ScreenDriverIsUser);
        Assert.Contains("Agent is driving", doc.ScreenDriverChip, StringComparison.Ordinal);

        vm.Display.Holder = DisplayControlHolder.User;
        Assert.True(doc.ScreenDriverIsUser);        // amber: it is on you
        Assert.False(doc.ScreenDriverIsAgent);

        vm.IsDisplayVisible = true;
        Assert.False(doc.ShowScreenDriverChip);
    }

    /// <summary>A do-nothing <see cref="ITabController"/> — the gate under test never calls back into it.</summary>
    private sealed class NullTabController : ITabController
    {
        public string DefaultWorkingDirectory => string.Empty;
        public Task<bool> SelectHostAsync(SessionDocument doc, KnownHost host) => Task.FromResult(false);
        public Task AddHostAsync(SessionDocument doc) => Task.CompletedTask;
        public Task DiscoverAuthMethodsAsync(SessionDocument doc) => Task.CompletedTask;
        public Task SignInWithGitHubAsync(SessionDocument doc) => Task.CompletedTask;
        public Task SignInWithKeyAsync(SessionDocument doc) => Task.CompletedTask;
        public Task ForgetHostAsync(SessionDocument doc, KnownHost host) => Task.CompletedTask;
        public bool IsForgettableHost(string url) => false;
        public Task SelectAgentAsync(SessionDocument doc, string adapterId, string displayName, bool skipPermissions = false, string gitCredentialMode = "Off", bool useSandbox = true, string? modelId = null, bool graphical = false) => Task.CompletedTask;
        public Task DiscoverExternalSessionsAsync(SessionDocument doc) => Task.CompletedTask;
        public Task WatchExternalSessionAsync(SessionDocument doc, ExternalSessionInfo external) => Task.CompletedTask;
        public Task AttachCatalogSessionAsync(SessionDocument doc, Agnes.Ui.Core.ViewModels.CatalogSessionRow row) => Task.CompletedTask;
        public bool IsSessionOpen(string sessionId) => false;
        public Task LoadModelsAsync(SessionDocument doc, string adapterId) => Task.CompletedTask;
        public void ToggleModelFavorite(SessionDocument doc, ModelChoice model) { }
        public Task<ProviderAuthStatus?> CheckAgentAuthAsync(SessionDocument doc, string adapterId) => Task.FromResult<ProviderAuthStatus?>(null);
        public Task BeginProviderLoginAsync(SessionDocument doc, string adapterId) => Task.CompletedTask;
        public void BackToHosts(SessionDocument doc) { }
        public void PersistTabs() { }
        public void ArchiveTab(SessionDocument doc) { }
        public Task DuplicateAsync(SessionDocument doc) => Task.CompletedTask;
        public Task NewSessionSameSetupAsync(SessionDocument source) => Task.CompletedTask;
        public Task ForkAsync(SessionDocument doc) => Task.CompletedTask;
        public void FloatTab(SessionDocument doc) { }
        public Task LoadLaunchProfilesAsync(SessionDocument doc) => Task.CompletedTask;
        public Task SaveCurrentAsLaunchProfileAsync(SessionDocument doc, string name) => Task.CompletedTask;
        public void ApplyLaunchProfileMcpApproval(string mcpApproval) { }
        public void RememberWorkingDirectory(string path) { }
    }
}
