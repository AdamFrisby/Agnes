using System.Collections.Concurrent;

namespace Agnes.Acp;

/// <summary>
/// A single-threaded, FIFO <see cref="SynchronizationContext"/>. Assigned to a
/// <c>JsonRpc</c> instance so that inbound message dispatch — notifications and the
/// completion of outbound requests — is serialized in receive order. This guarantees
/// that <c>session/update</c> notifications are surfaced before the <c>session/prompt</c>
/// response that follows them on the wire, keeping the event stream correctly ordered.
/// </summary>
internal sealed class SerialSynchronizationContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly Thread _thread;

    public SerialSynchronizationContext()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "acp-rpc-pump" };
        _thread.Start();
    }

    public override void Post(SendOrPostCallback d, object? state) => Enqueue(d, state);

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Thread.CurrentThread == _thread)
        {
            d(state);
            return;
        }

        using var done = new ManualResetEventSlim(false);
        Enqueue(_ => { d(state); done.Set(); }, null);
        done.Wait();
    }

    /// <summary>
    /// Completes once everything queued ahead of this call has run. The queue is FIFO and single-threaded,
    /// so awaiting a marker posted to it is a precise "the inbound messages received so far are fully
    /// handled" barrier — which is not the same as awaiting an outbound request's own response. That
    /// response's completion is posted here too, but the caller's continuation resumes on the thread pool
    /// and can overtake the notification handlers still queued in front of it. Callers that must not miss
    /// (or must not see) anything the agent sent before its reply await this afterwards.
    /// </summary>
    public Task FlushAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(_ => done.TrySetResult(), null);
        if (_queue.IsAddingCompleted)
        {
            // Disposed: the marker was dropped and the pump is finishing, so there is nothing left to wait
            // for. Completing here rather than hanging keeps shutdown from deadlocking a caller.
            done.TrySetResult();
        }

        return done.Task;
    }

    private void Enqueue(SendOrPostCallback d, object? state)
    {
        if (!_queue.IsAddingCompleted)
        {
            _queue.Add((d, state));
        }
    }

    private void Run()
    {
        SetSynchronizationContext(this);
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
        {
            callback(state);
        }
    }

    public void Dispose() => _queue.CompleteAdding();
}
