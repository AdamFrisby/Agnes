using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// Answering from the approvals list itself. A session permission is answered in its session — the transcript
/// is the context the decision needs — but everything else in that list (an external system's question, an
/// approval-gated action) has nowhere else to go, so the row carries the answers it accepts. Without this the
/// "waiting on you" surface can show a request no one can clear, which is worse than not listing it.
/// </summary>
public class ApprovalAnswerTests
{
    private static ApprovalsViewModel For(IAgnesHost host)
        => new(() => [host], ImmediateDispatcher.Instance);

    [Fact]
    public async Task An_external_request_offers_its_own_options_and_answering_reaches_the_host()
    {
        var host = new RecordingHost(new OpenApproval(
            SessionId: null, RequestId: "att-1", Title: "Deploy to production?", ToolCallId: string.Empty,
            RequestedAt: DateTimeOffset.UnixEpoch, Kind: OpenApprovalKind.ExternalAttention,
            Source: "release-bot", Options: ["Ship it", "Hold"]));

        var vm = For(host);
        await vm.LoadAsync();

        var row = Assert.Single(vm.Approvals);
        Assert.True(row.HasChoices);
        Assert.Equal(["Ship it", "Hold"], row.Choices.Select(c => c.Label));
        Assert.True(row.Choices[0].IsPrimary);

        row.Choices[1].Command.Execute(null);

        Assert.Equal(("att-1", "Hold"), host.Answered);
    }

    [Fact]
    public async Task A_gated_action_offers_approve_and_reject_and_both_reach_the_host()
    {
        var host = new RecordingHost(new OpenApproval(
            SessionId: null, RequestId: "gate-1", Title: "Delete 3 sandboxes", ToolCallId: string.Empty,
            RequestedAt: DateTimeOffset.UnixEpoch, Kind: OpenApprovalKind.GatedAction, Source: "sandbox.reap"));

        var vm = For(host);
        await vm.LoadAsync();

        var row = Assert.Single(vm.Approvals);
        Assert.Equal(["Approve", "Reject"], row.Choices.Select(c => c.Label));

        row.Choices[1].Command.Execute(null);
        Assert.Equal(("gate-1", false), host.Resolved);

        // The list re-reads after an answer, so the row's command still points at the same request.
        row.Choices[0].Command.Execute(null);
        Assert.Equal(("gate-1", true), host.Resolved);
    }

    [Fact]
    public async Task A_session_permission_offers_no_inline_answer_because_it_is_answered_in_the_session()
    {
        var host = new RecordingHost(new OpenApproval(
            SessionId: "s1", RequestId: "req-1", Title: "Run `rm -rf build`", ToolCallId: "tc-1",
            RequestedAt: DateTimeOffset.UnixEpoch));

        var vm = For(host);
        await vm.LoadAsync();

        var row = Assert.Single(vm.Approvals);
        Assert.False(row.HasChoices);
        Assert.Empty(row.Choices);
    }

    [Fact]
    public async Task An_external_request_with_no_stated_options_can_still_be_acknowledged()
    {
        var host = new RecordingHost(new OpenApproval(
            SessionId: null, RequestId: "att-2", Title: "Backup finished — all good?", ToolCallId: string.Empty,
            RequestedAt: DateTimeOffset.UnixEpoch, Kind: OpenApprovalKind.ExternalAttention, Source: "cron"));

        var vm = For(host);
        await vm.LoadAsync();

        var row = Assert.Single(vm.Approvals);
        var only = Assert.Single(row.Choices);
        only.Command.Execute(null);

        Assert.Equal("att-2", host.Answered.RequestId);
    }

    /// <summary>A host that serves one open approval and records how it was answered.</summary>
    private sealed class RecordingHost : StubAgnesHost
    {
        private readonly OpenApproval _approval;

        public RecordingHost(OpenApproval approval) => _approval = approval;

        public (string RequestId, string Answer) Answered { get; private set; }

        public (string RequestId, bool Approve) Resolved { get; private set; }

        public override Task<IReadOnlyList<OpenApproval>> GetOpenApprovalsAsync()
            => Task.FromResult<IReadOnlyList<OpenApproval>>([_approval]);

        public override Task AnswerAttentionRequestAsync(string requestId, string answer)
        {
            Answered = (requestId, answer);
            return Task.CompletedTask;
        }

        public override Task ResolveGatedApprovalAsync(string requestId, bool approve)
        {
            Resolved = (requestId, approve);
            return Task.CompletedTask;
        }
    }
}
