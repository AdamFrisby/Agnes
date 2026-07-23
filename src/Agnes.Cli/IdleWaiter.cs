using Agnes.Abstractions;
using Agnes.Client;

namespace Agnes.Cli;

/// <summary>The terminal result of a <c>wait</c> (and the shared core of <c>send --wait</c>).</summary>
public enum WaitOutcome
{
    /// <summary>The session reached idle within the timeout — exit 0.</summary>
    Idle,

    /// <summary>The timeout elapsed while the session was still running — exit 1, session left untouched.</summary>
    Timeout,

    /// <summary>The session ended in an agent error — exit 2, distinct from a timeout.</summary>
    AgentError,
}

/// <summary>Maps CLI outcomes to process exit codes so scripts can branch on <c>$?</c>.</summary>
public static class ExitCodes
{
    public const int Success = 0;

    /// <summary>A usage error, an unresolved/ambiguous id, or a failed operation.</summary>
    public const int Failure = 1;

    /// <summary>Reserved for <c>wait</c>/<c>send --wait</c>: the agent turn failed (see <see cref="WaitOutcome.AgentError"/>).</summary>
    public const int AgentError = 2;

    /// <summary>0 = idle, 1 = timeout, 2 = agent error — the contract the acceptance criteria pin down.</summary>
    public static int ForWait(WaitOutcome outcome) => outcome switch
    {
        WaitOutcome.Idle => Success,
        WaitOutcome.Timeout => Failure,
        WaitOutcome.AgentError => AgentError,
        _ => Failure,
    };
}

/// <summary>
/// Blocks until a subscribed session goes idle, resolving purely from its existing event stream (no new
/// host primitive, per the spec). The wait is read-only: on timeout it returns without cancelling or
/// otherwise touching the session. The idle/error decision runs through <see cref="SessionActivity"/> so
/// it matches <c>status</c> exactly.
/// </summary>
public static class IdleWaiter
{
    public static async Task<WaitOutcome> WaitAsync(
        SessionView view,
        TimeSpan? timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(timeProvider);

        // Already terminal from the snapshot? Resolve immediately without waiting.
        var initial = Classify(SessionActivity.Evaluate(view.Events));
        if (initial is not null)
        {
            return initial.Value;
        }

        var completion = new TaskCompletionSource<WaitOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnAppended(SessionEvent _)
        {
            var outcome = Classify(SessionActivity.Evaluate(view.Events));
            if (outcome is not null)
            {
                completion.TrySetResult(outcome.Value);
            }
        }

        view.EventAppended += OnAppended;
        try
        {
            // Re-check once after subscribing: an event that landed between the snapshot read and the
            // handler being attached would otherwise be missed.
            OnAppended(null!);

            var waitTask = completion.Task;
            var delay = timeout is { } t
                ? Task.Delay(t, timeProvider, cancellationToken)
                : Task.Delay(Timeout.InfiniteTimeSpan, timeProvider, cancellationToken);

            var finished = await Task.WhenAny(waitTask, delay).ConfigureAwait(false);
            if (finished == waitTask)
            {
                return await waitTask.ConfigureAwait(false);
            }

            // The delay won: either the timeout elapsed or the wait was cancelled by the caller.
            await delay.ConfigureAwait(false); // surfaces an OperationCanceledException if cancelled
            return WaitOutcome.Timeout;
        }
        finally
        {
            view.EventAppended -= OnAppended;
        }
    }

    private static WaitOutcome? Classify(SessionState state) => state switch
    {
        SessionState.Idle => WaitOutcome.Idle,
        SessionState.Errored => WaitOutcome.AgentError,
        _ => null,
    };
}
