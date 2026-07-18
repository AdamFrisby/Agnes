using Agnes.Abstractions;
using Agnes.Host.Hosting;
using Agnes.Protocol;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Sessions;

/// <summary>
/// Runs due scheduled tasks in the background: opens a session, sends the prompt, waits for the turn
/// to end, and records the result in the inbox (broadcast to connected clients). Ticks every 5s.
/// </summary>
public sealed class ScheduledRunner : BackgroundService
{
    private readonly ScheduledTaskManager _tasks;
    private readonly SessionManager _sessions;
    private readonly IHubContext<AgnesHub, IAgnesClient> _hub;
    private readonly ILogger<ScheduledRunner> _logger;

    public ScheduledRunner(
        ScheduledTaskManager tasks,
        SessionManager sessions,
        IHubContext<AgnesHub, IAgnesClient> hub,
        ILogger<ScheduledRunner> logger)
    {
        _tasks = tasks;
        _sessions = sessions;
        _hub = hub;
        _logger = logger;
        _tasks.RunRecorded += run => _ = _hub.Clients.All.OnInboxRun(run);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                foreach (var task in _tasks.TakeDue(DateTimeOffset.UtcNow))
                {
                    _ = RunAsync(task, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled runner tick failed");
            }
        }
    }

    private async Task RunAsync(ScheduledTask task, CancellationToken cancellationToken)
    {
        try
        {
            var info = await _sessions.OpenSessionAsync(task.AdapterId, task.WorkingDirectory, cancellationToken: cancellationToken).ConfigureAwait(false);
            await _sessions.PromptAsync(info.SessionId, [new TextContent(task.Prompt)]).ConfigureAwait(false);

            var summary = "…";
            for (var i = 0; i < 120 && !cancellationToken.IsCancellationRequested; i++)
            {
                var snapshot = await _sessions.GetSnapshotAsync(info.SessionId, 0, cancellationToken).ConfigureAwait(false);
                var lastAssistant = snapshot.Events.OfType<MessageChunkEvent>()
                    .LastOrDefault(m => m.Role == MessageRole.Assistant);
                if (lastAssistant?.Content is TextContent text)
                {
                    summary = text.Text;
                }

                if (snapshot.Events.Any(e => e is TurnEndedEvent))
                {
                    break;
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }

            _tasks.Record(new InboxRun(
                Guid.NewGuid().ToString("n"), task.Id, Truncate(task.Prompt, 60), Truncate(summary, 200), DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scheduled task {TaskId} failed", task.Id);
        }
    }

    private static string Truncate(string s, int max)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length > max ? s[..max] + "…" : s;
    }
}
