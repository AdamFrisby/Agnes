using System.Collections.Concurrent;

namespace Agnes.Agents.Codex;

/// <summary>
/// A single-threaded, FIFO <see cref="SynchronizationContext"/> assigned to the Codex
/// <c>JsonRpc</c> instance so inbound dispatch is serialized in receive order — keeping item
/// notifications ordered ahead of the <c>turn/completed</c> that follows them on the wire. A copy
/// of the ACP adapter's helper (the two adapters share a transport but not an assembly).
/// </summary>
internal sealed class SerialSynchronizationContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly Thread _thread;

    public SerialSynchronizationContext()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "codex-rpc-pump" };
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
