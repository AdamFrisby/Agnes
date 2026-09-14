using System.Net;
using System.Text;
using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.Plugins.CodeyBox;
using Agnes.Plugins.CodeyBox.Tests;
using Agnes.Ui.Core;

namespace Agnes.Mobile.Tests;

/// <summary>
/// A CodeyBox that is not there, remembering everything it was asked.
/// </summary>
/// <remarks>
/// The item page's whole job is turning a tap into the right HTTP call — retry is a POST, raising the
/// ceiling is a POST <em>and then</em> a PATCH in that order, cancelling is a DELETE, answering and
/// dismissing are two different POSTs with two different bodies. So what the tests assert is what was
/// SENT, which means the fake has to record it rather than merely answer. The operator's real
/// orchestrator is doing real work and is never touched by a test.
/// </remarks>
internal sealed class FleetHandler : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private readonly List<Sent> _sent = [];

    internal sealed record Sent(string Method, string Path, string Body);

    public IReadOnlyList<Sent> Requests
    {
        get { lock (_gate) { return [.. _sent]; } }
    }

    /// <summary>What <c>GET /workitems/{id}/questions</c> answers. Empty is the ordinary case.</summary>
    public string QuestionsBody { get; set; } = "[]";

    /// <summary>What <c>GET /workitems</c> answers.</summary>
    public string ItemsBody { get; set; } = "[]";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _sent.Add(new Sent(request.Method.Method, path, body));
        }

        var answer = path switch
        {
            _ when path.EndsWith("/questions", StringComparison.Ordinal) => QuestionsBody,
            "/workitems" => ItemsBody,
            "/queue/status" => """{"state":"Running","pausedAt":null,"pausedReason":null}""",
            _ => "[]",
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(answer, Encoding.UTF8, "application/json"),
        };
    }
}

/// <summary>
/// A shell that records what a screen asked of it and needs no Avalonia to do it.
/// </summary>
/// <remarks>
/// Most of what these screens do is answerable without a window — which command a choice runs, what the
/// Inbox projects, what was sent over HTTP. Those tests take this and stay off the UI thread entirely;
/// only the ones that are about the shell itself, or about pixels, stand up the real one.
/// </remarks>
internal sealed class StubShell : IAppShell
{
    public List<PageViewModel> Pushed { get; } = [];

    public List<SheetViewModel> Sheets { get; } = [];

    public List<string> Toasts { get; } = [];

    public int Pops { get; private set; }

    public void Push(PageViewModel page) => Pushed.Add(page);

    public void Pop() => Pops++;

    public void PopToRoot() => Pushed.Clear();

    public void ShowSheet(SheetViewModel sheet) => Sheets.Add(sheet);

    public void CloseSheet() { }

    public void Toast(string message, ToastKind kind = ToastKind.Info) => Toasts.Add(message);

    public void CopyToClipboard(string text, string what) { }

    public void OpenUrl(string url) { }

    public Task<string?> DictateAsync() => Task.FromResult<string?>(null);

    public bool CanDictate => false;

    public bool IsMeteredNetwork => false;

    public string DeviceName => "Test phone";

    public IHaptics Haptics { get; } = NullHaptics.Instance;

    public IReceivedFileHandler ReceivedFiles { get; } = NullReceivedFileHandler.Instance;

    public IUiDispatcher Dispatcher { get; } = ImmediateDispatcher.Instance;

    public MobileSettings Settings { get; } = new();

    public HostBook Hosts { get; } = new(new MobileConnector(), ImmediateDispatcher.Instance);
}

/// <summary>Shapes every CodeyBox test needs, so each one says what it means in a line.</summary>
internal static class Fleet
{
    /// <summary>An address and a key that look real enough to configure with and reach nothing.</summary>
    internal static CodeyBoxConfig Config => new("http://10.0.0.188:5836", "test-key");

    /// <summary>
    /// The canned fleet the phone's screens are drawn against: the plugin's own samples, which its tests
    /// already exercise. Kept there rather than copied here — two heads drawing two different fleets and
    /// calling both "the sample" is how they stop agreeing.
    /// </summary>
    internal static GatheredOverview Gathered() => new(
        OverviewSamples.Fleet(),
        NowWorkingSamples.Items(),
        [new Project("codeybox-self", "CodeyBox", null, "main", "claude", AuditMaxIterations: 25)],
        [],
        new Concurrency(3, 2, new Dictionary<string, int>()),
        new QueueStatus("Running", null, null));

    /// <summary>A fleet on a stub shell, configured, with a recording orchestrator behind it.</summary>
    internal static (StubShell Shell, CodeyBoxViewModel Fleet) Offline(FleetHandler? handler = null)
    {
        Config.Save();
        var shell = new StubShell();
        var fleet = new CodeyBoxViewModel(
            shell,
            clientFactory: handler is null
                ? _ => null
                : options => new CodeyBoxClient(options, handler));
        return (shell, fleet);
    }

    /// <summary>A shell with a fleet configured and nothing behind it.</summary>
    internal static ShellViewModel Shell(FleetHandler? handler = null)
    {
        Config.Save();
        var shell = new ShellViewModel(
            new MobileConnector(), new MobileDispatcher(), new MobileSettings(), "CodeyBox test",
            codeyBoxClient: handler is null
                ? _ => null
                : options => new CodeyBoxClient(options, handler));
        shell.CodeyBox.Apply(CodeyBoxConfig.Load());
        return shell;
    }
}
