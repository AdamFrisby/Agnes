using System.Threading.Channels;
using Agnes.Abstractions;

namespace Agnes.Host.Tests;

/// <summary>An in-memory agent session the test controls directly (no ACP/process).</summary>
public sealed class ScriptedAgentSession : IAgentSession
{
    private readonly Channel<SessionEvent> _events =
        Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = true });

    public string AgentSessionId { get; } = "scripted";

    public ChannelReader<SessionEvent> Events => _events.Reader;

    /// <summary>Invoked on prompt; emit events via <see cref="Emit"/> and return a stop reason.</summary>
    public Func<IReadOnlyList<ContentBlock>, ScriptedAgentSession, Task<StopReason>> OnPrompt { get; set; }
        = (_, _) => Task.FromResult(StopReason.EndTurn);

    public void Emit(SessionEvent @event) => _events.Writer.TryWrite(@event);

    public Task<StopReason> PromptAsync(IReadOnlyList<ContentBlock> content, CancellationToken cancellationToken = default)
        => OnPrompt(content, this);

    public Task CancelAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RespondToPermissionAsync(string requestId, string optionId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Adapter that hands out a single, test-controlled <see cref="ScriptedAgentSession"/>.</summary>
public sealed class ScriptedAgentAdapter : IAgentAdapter
{
    public ScriptedAgentSession Session { get; } = new();

    /// <summary>The options passed to the most recent <see cref="StartSessionAsync"/> call.</summary>
    public AgentSessionOptions? LastOptions { get; private set; }

    public ScriptedAgentAdapter(string id = "scripted")
        => Descriptor = new() { Id = id, DisplayName = "Scripted Agent" };

    public AgentDescriptor Descriptor { get; }

    public Task<IAgentSession> StartSessionAsync(AgentSessionOptions options, CancellationToken cancellationToken = default)
    {
        LastOptions = options;
        return Task.FromResult<IAgentSession>(Session);
    }
}
