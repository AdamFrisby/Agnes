using System.Text;
using Agnes.Abstractions;

namespace Agnes.Host.Sessions;

/// <summary>The text an agent produced during a one-shot run, plus why its single turn stopped.</summary>
public sealed record OneShotResult(string Text, StopReason? StopReason);

/// <summary>
/// A generic, feature-agnostic primitive for "spin up a bounded, non-interactive agent session, send one
/// prompt, take its final text, tear it down" (see <c>git-and-files/01-deep-git-integration.md</c>). Built once
/// and shared so one-shot generation tasks (commit-message suggestion, session-summary generation, …) don't each
/// grow their own ad-hoc session lifecycle that drifts apart over time.
/// </summary>
/// <remarks>
/// The session is opened directly on the adapter (bypassing the full interactive <see cref="SessionManager"/>
/// open path) with permissions skipped, since the task is a single self-contained turn with no human in the loop.
/// It is always disposed — including on timeout or fault — so no throwaway agent process is leaked.
/// </remarks>
public sealed class OneShotAgentRunner
{
    /// <summary>Default upper bound on a single one-shot run before it is abandoned.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    private readonly TimeSpan _timeout;

    public OneShotAgentRunner(TimeSpan? timeout = null)
        => _timeout = timeout is { } t && t > TimeSpan.Zero ? t : DefaultTimeout;

    /// <summary>
    /// Opens a throwaway session on <paramref name="adapter"/> in <paramref name="workingDirectory"/>, sends
    /// <paramref name="prompt"/> once, and returns the concatenated assistant text of the resulting turn. The
    /// session is torn down before this returns. Throws <see cref="TimeoutException"/> if the turn does not end
    /// within the configured timeout (the session is still disposed); an outer <paramref name="cancellationToken"/>
    /// cancellation propagates as <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<OneShotResult> RunAsync(
        IAgentAdapter adapter,
        string workingDirectory,
        IReadOnlyList<ContentBlock> prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(prompt);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        var ct = timeoutCts.Token;
        var timedOut = false;

        var options = new AgentSessionOptions { WorkingDirectory = workingDirectory, SkipPermissions = true };
        var session = await adapter.StartSessionAsync(options, ct).ConfigureAwait(false);
        await using (session.ConfigureAwait(false))
        {
            var text = new StringBuilder();
            StopReason? stop = null;

            // Send the single prompt; its Task completes when the turn ends. We read the event stream
            // concurrently so streamed assistant chunks are captured in order up to the TurnEndedEvent.
            var promptTask = session.PromptAsync(prompt, ct);
            try
            {
                await foreach (var @event in session.Events.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    if (@event is MessageChunkEvent { Role: MessageRole.Assistant, Content: TextContent chunk })
                    {
                        text.Append(chunk.Text);
                    }
                    else if (@event is TurnEndedEvent turn)
                    {
                        stop = turn.Reason;
                        break;
                    }
                }

                // Observe the prompt task's own completion (and stop reason, if no TurnEndedEvent was seen).
                var reason = await promptTask.ConfigureAwait(false);
                stop ??= reason;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                timedOut = true;
                await ObserveQuietlyAsync(promptTask).ConfigureAwait(false);
            }

            if (timedOut)
            {
                throw new TimeoutException($"One-shot agent run exceeded {_timeout}.");
            }

            return new OneShotResult(text.ToString().Trim(), stop);
        }
    }

    // Awaits a possibly-faulted/cancelled prompt task without letting its exception escape or go unobserved —
    // on the timeout path the TimeoutException is what we surface, not the prompt's own cancellation.
    private static async Task ObserveQuietlyAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Intentionally swallowed: the run already timed out; the send's own outcome is not separately surfaced.
        }
    }
}
