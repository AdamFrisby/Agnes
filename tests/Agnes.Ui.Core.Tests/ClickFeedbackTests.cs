using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core.Transcript;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// A click is answered at once. Building a session view model may happen on another thread without
/// losing the events that arrive meanwhile; a git operation says what it is doing while the host works;
/// a question card says it is sending.
/// </summary>
public class ClickFeedbackTests
{
    private sealed class SlowHost : StubAgnesHost, IAgnesHost
    {
        public TaskCompletionSource<GitCommitResult> Commit { get; } = new();
        public TaskCompletionSource Answer { get; } = new();

        public override Task<GitCommitResult> GitCommitAsync(string sessionId, string message) => Commit.Task;

        public Task AnswerQuestionAsync(string sessionId, string requestId, IReadOnlyList<QuestionAnswer> answers) => Answer.Task;
    }

    private static SessionView Loaded(int count)
    {
        var view = new SessionView("s1");
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo("s1", "opencode", string.Empty, count),
            Enumerable.Range(1, count).Select(i => (SessionEvent)new NoticeEvent($"n{i}") { Sequence = i }).ToList(), count));
        return view;
    }

    [Fact]
    public async Task Events_that_arrive_while_the_view_model_is_being_built_are_not_lost()
    {
        var view = Loaded(3_000);
        var building = Task.Run(() => new SessionViewModel(new StubHost(), view, ImmediateDispatcher.Instance, "OpenCode"));
        // Live events land on the view from another thread throughout the build.
        for (var i = 3_001; i <= 3_400; i++)
        {
            view.Apply(new NoticeEvent($"live {i}") { Sequence = i });
            if (i % 50 == 0)
            {
                await Task.Yield();
            }
        }

        await using var vm = await building;

        // Every event, once, in order — whether it was in the history, buffered during the build, or live after.
        var seen = vm.RawEvents.Select(r => r.Sequence).ToList();
        Assert.Equal(Enumerable.Range(1, 3_400).Select(i => (long)i), seen);
    }

    private sealed class StubHost : StubAgnesHost;

    [Fact]
    public async Task A_git_operation_says_what_it_is_doing_until_the_host_answers()
    {
        var host = new SlowHost();
        await using var vm = new SessionViewModel(host, Loaded(1), ImmediateDispatcher.Instance, "OpenCode");
        vm.CommitMessage = "fix";

        var commit = vm.CommitCommand.ExecuteAsync(null);
        Assert.Equal("Committing…", vm.GitBusy);
        Assert.True(vm.HasGitBusy);

        host.Commit.SetResult(new GitCommitResult(true, "ok"));
        await commit;
        Assert.Equal(string.Empty, vm.GitBusy);
    }

    [Fact]
    public async Task Answering_a_question_says_sending_and_stands_the_buttons_down()
    {
        var host = new SlowHost();
        var view = Loaded(1);
        await using var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "OpenCode");
        view.Apply(new QuestionAskedEvent("q1", "tc1", [new AgentQuestion("which", "Which?", "Pick one", [new QuestionChoice("A", "the first"), new QuestionChoice("B", "the second")])]) { Sequence = 2 });
        var card = Assert.IsType<QuestionItem>(vm.Items.Last());
        Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(vm.AnswerQuestionCommand);

        var sending = ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.AnswerQuestionCommand).ExecuteAsync(card);
        Assert.True(card.IsSubmitting);

        host.Answer.SetResult();
        await sending;
        Assert.False(card.IsSubmitting);
    }
}
