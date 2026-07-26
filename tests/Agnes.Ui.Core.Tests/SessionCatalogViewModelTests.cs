using Agnes.Client;
using Agnes.Client.Simulation;
using Agnes.Protocol;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// The shared "what's already running over there?" list both heads use after pairing: it aggregates each
/// host's session catalogue, orders it by what most wants a human, and hands the shell an attach request
/// rather than subscribing itself.
/// </summary>
public class SessionCatalogViewModelTests
{
    private static SessionCatalogViewModel For(params IAgnesHost[] hosts)
        => new(() => hosts, ImmediateDispatcher.Instance);

    [Fact]
    public async Task Lists_what_the_host_is_running_including_sessions_this_client_never_opened()
    {
        var host = new SimulatedHost();
        await host.ConnectAsync();
        var mine = await host.OpenSessionAsync("claude-code", "/work/mine");

        var vm = For(host);
        await vm.LoadAsync();

        Assert.True(vm.HasSessions);
        Assert.Contains(vm.Sessions, r => r.SessionId == mine.SessionId);
        // The simulated host also carries sessions that predate this client — the whole point of the surface.
        Assert.Contains(vm.Sessions, r => r.SessionId != mine.SessionId);
    }

    [Fact]
    public async Task Orders_by_need_blocked_first_then_working_then_the_rest()
    {
        var host = new StubHost(
            Summary("idle-one", SessionRunState.Idle),
            Summary("working-one", SessionRunState.Working),
            Summary("blocked-one", SessionRunState.Idle, approvals: 1),
            Summary("dormant-one", SessionRunState.Dormant));

        var vm = For(host);
        await vm.LoadAsync();

        Assert.Equal(
            ["blocked-one", "working-one", "idle-one", "dormant-one"],
            vm.Sessions.Select(r => r.SessionId).ToArray());
        Assert.Equal(1, vm.BlockedCount);
    }

    [Fact]
    public async Task A_row_reads_as_needing_you_before_it_reads_as_working()
    {
        // A session can be mid-turn AND holding an unanswered request; "needs you" is the actionable half.
        var host = new StubHost(Summary("s", SessionRunState.Working, approvals: 2));
        var vm = For(host);
        await vm.LoadAsync();

        var row = Assert.Single(vm.Sessions);
        Assert.True(row.IsBlocked);
        Assert.Equal("Needs you (2)", row.StateText);
    }

    [Fact]
    public async Task Falls_back_from_title_to_folder_name_so_a_row_is_never_blank()
    {
        var host = new StubHost(new SessionSummary("s", "claude-code", "/home/you/projects/agnes", Title: null,
            SessionRunState.Idle, HeadSequence: 3));
        var vm = For(host);
        await vm.LoadAsync();

        Assert.Equal("agnes", Assert.Single(vm.Sessions).Title);
    }

    [Fact]
    public async Task Activating_a_row_asks_the_shell_to_attach_rather_than_subscribing_itself()
    {
        var host = new StubHost(Summary("s", SessionRunState.Idle));
        var vm = For(host);
        await vm.LoadAsync();

        CatalogSessionRow? asked = null;
        vm.AttachRequested += row => asked = row;
        vm.AttachCommand.Execute(vm.Sessions[0]);

        Assert.NotNull(asked);
        Assert.Equal("s", asked!.SessionId);
        Assert.Equal(0, host.Subscribes); // listing and joining are separate acts
    }

    [Fact]
    public async Task One_unreachable_host_does_not_blank_the_list()
    {
        var good = new StubHost(Summary("s", SessionRunState.Idle));
        var bad = new StubHost { Throws = true };

        var vm = For(bad, good);
        await vm.LoadAsync();

        Assert.Single(vm.Sessions);
    }

    [Fact]
    public async Task Says_so_plainly_when_the_host_is_running_nothing()
    {
        var vm = For(new StubHost());
        await vm.LoadAsync();

        Assert.False(vm.HasSessions);
        Assert.Equal("Nothing is running here yet.", vm.Status);
    }

    private static SessionSummary Summary(string id, SessionRunState state, int approvals = 0)
        => new(id, "claude-code", "/work/" + id, id, state, HeadSequence: 10, OpenApprovals: approvals,
            StartedAt: DateTimeOffset.UnixEpoch, LastActivityAt: DateTimeOffset.UnixEpoch);

    /// <summary>A host that answers with a fixed catalogue (or fails), so ordering and error handling are
    /// exercised without a simulated agent's timing.</summary>
    private sealed class StubHost : StubAgnesHost
    {
        private readonly SessionSummary[] _summaries;

        public StubHost(params SessionSummary[] summaries) => _summaries = summaries;

        /// <summary>Whether this host fails every call, standing in for one that's unreachable.</summary>
        public bool Throws { get; init; }

        /// <summary>How many times something subscribed — proves listing doesn't.</summary>
        public int Subscribes { get; private set; }

        public override Task<IReadOnlyList<SessionSummary>> ListSessionsAsync()
            => Throws
                ? Task.FromException<IReadOnlyList<SessionSummary>>(new InvalidOperationException("unreachable"))
                : Task.FromResult<IReadOnlyList<SessionSummary>>(_summaries);

        public override Task<SessionView> SubscribeAsync(string sessionId, long since = 0)
        {
            Subscribes++;
            return Task.FromResult(new SessionView(sessionId));
        }
    }
}
