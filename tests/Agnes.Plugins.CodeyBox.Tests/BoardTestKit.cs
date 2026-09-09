using System.Net;
using System.Text;
using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// A stub orchestrator that answers by route and records every request — method, path and body.
/// </summary>
/// <remarks>
/// The board's whole job is turning a click into the right set of HTTP calls: a move is a sequence of
/// priority patches, a dropped edge is a replace-set PATCH, a parent's unblock is one of two POSTs. So
/// what these tests assert is what was SENT, which means the fake has to remember it rather than merely
/// answer. Bodies are kept as strings — this is the boundary, and reading the JSON back out is exactly
/// what the assertion is for.
/// </remarks>
internal sealed class RoutingHandler : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private readonly List<Sent> _sent = [];

    internal sealed record Sent(string Method, string Path, string Body);

    public IReadOnlyList<Sent> Requests
    {
        get { lock (_gate) { return [.. _sent]; } }
    }

    /// <summary>Paths that should answer 500, to drive the partial-failure paths.</summary>
    public HashSet<string> Failing { get; } = new(StringComparer.Ordinal);

    /// <summary>Ids handed back by successive <c>POST /workitems</c> calls, in order.</summary>
    public Queue<string> CreatedIds { get; } = new();

    /// <summary>How many creates succeed before the rest answer 500 — a chain that lands halfway, which
    /// is a real outcome because there is no batch endpoint and no transaction. Negative means "all".</summary>
    public int CreatesBeforeFailing { get; set; } = -1;

    private int _creates;

    public string ItemsBody { get; set; } = "[]";

    public string ProjectsBody { get; set; } = "[]";

    public void Clear()
    {
        lock (_gate)
        {
            _sent.Clear();
        }
    }

    public IReadOnlyList<Sent> Of(string method)
        => [.. Requests.Where(r => string.Equals(r.Method, method, StringComparison.Ordinal))];

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

        var creating = path == "/workitems" && request.Method == HttpMethod.Post;
        var refuse = creating
            && CreatesBeforeFailing >= 0
            && Interlocked.Increment(ref _creates) > CreatesBeforeFailing;

        if (refuse || Failing.Contains(path))
        {
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("nope", Encoding.UTF8, "text/plain"),
            };
        }

        var answer = path switch
        {
            "/workitems" when request.Method == HttpMethod.Post =>
                $$"""{"id":"{{(CreatedIds.Count > 0 ? CreatedIds.Dequeue() : Guid.NewGuid().ToString("N"))}}"}""",
            "/workitems" => ItemsBody,
            "/projects" => ProjectsBody,
            "/queue/status" => """{"state":"Running","pausedAt":null,"pausedReason":null}""",
            "/concurrency" => """{"globalMaxConcurrent":2,"currentlyRunningTotal":1,"perAgentCaps":{}}""",
            _ => "null",
        };

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(answer, Encoding.UTF8, "application/json"),
        };
    }
}

/// <summary>Rows and chains shaped like the live queue, so every test says what it means in one line.</summary>
internal static class Fake
{
    public static WorkItemRow Row(
        string id,
        string state = "Queued",
        string title = "An item",
        string? project = "codeybox-self",
        string? agent = "claude",
        int priority = 0,
        bool depsOk = true,
        string[]? dependsOn = null,
        string? externalId = null,
        int ageDays = 1,
        string? prompt = null)
        => new(
            Id: id, Title: title, State: state, Agent: agent, ProjectId: project, QueuePosition: 0,
            UpdatedAt: DateTimeOffset.UtcNow.AddDays(-ageDays), LastError: null,
            Prompt: prompt, DependsOn: dependsOn, Priority: priority,
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-ageDays), DependsOnSatisfied: depsOk,
            WorkBranch: $"feat/{id}", ExternalId: externalId);

    public static Step Step(WorkItemRow item, int index = 0, StepState state = StepState.Ready)
        => new(item, index, state, string.Empty);

    /// <summary>A chain built by hand, so the view model's own behaviour can be tested against one
    /// without waiting for <see cref="BoardModel.Build"/> to exist.</summary>
    public static Chain Chain(
        WorkItemRow head,
        IReadOnlyList<WorkItemRow>? members = null,
        Horizon horizon = Horizon.Next,
        int rank = 0)
    {
        var rows = members ?? [head];
        return new Chain(
            Id: rows[0].Id,
            Title: rows[0].Title,
            ProjectId: rows[0].ProjectId,
            Steps: [.. rows.Select((r, i) => Step(r, i))],
            Head: head,
            Horizon: horizon,
            Reason: WaitReason.None,
            Why: "because",
            Blocker: null,
            DispatchRank: rank,
            LastActivity: head.UpdatedAt,
            Landed: null);
    }

    public static Board Board(params Chain[] next)
        => new([], next, [], [], 0, (1, 2));

    public static Relation Relation(WorkItemRow item, StepState state = StepState.Failed, bool satisfied = false)
        => new(item, state, satisfied);
}
