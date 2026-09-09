using Agnes.Abstractions;
using Agnes.App.Desktop.Persistence;
using Agnes.App.Desktop.ViewModels;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Desktop.Tests;

/// <summary>
/// A do-nothing <see cref="ITabController"/>. A <see cref="SessionDocument"/> needs one to exist at all, but
/// a test that only looks at what a document derives from its session never calls back into it — so this is
/// the whole of the seam, shared rather than restated in each test that wants a document.
/// </summary>
internal sealed class NullTabController : ITabController
{
    public string DefaultWorkingDirectory => string.Empty;
    public Task<bool> SelectHostAsync(SessionDocument doc, KnownHost host) => Task.FromResult(false);
    public Task AddHostAsync(SessionDocument doc) => Task.CompletedTask;
    public Task DiscoverAuthMethodsAsync(SessionDocument doc) => Task.CompletedTask;
    public Task SignInWithGitHubAsync(SessionDocument doc) => Task.CompletedTask;
    public Task SignInWithKeyAsync(SessionDocument doc) => Task.CompletedTask;
    public Task ForgetHostAsync(SessionDocument doc, KnownHost host) => Task.CompletedTask;
    public void OpenDevicesSettings() { }
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
    public void AdjustChatFontSize(int direction) { }
}
