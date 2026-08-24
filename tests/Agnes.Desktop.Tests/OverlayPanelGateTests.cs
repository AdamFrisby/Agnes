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
        public Task SelectAgentAsync(SessionDocument doc, string adapterId, string displayName, bool skipPermissions = false, string gitCredentialMode = "Off", bool useSandbox = true, string? modelId = null) => Task.CompletedTask;
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
        public void AdjustChatFontSize(int direction) { }
    }
}
