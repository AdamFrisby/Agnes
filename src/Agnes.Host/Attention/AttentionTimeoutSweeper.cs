using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Attention;

/// <summary>
/// Background service that periodically asks <see cref="AttentionRequestService.SweepExpired"/> to mark
/// timed-out requests Expired and fire their timeout callbacks. The actual sweep logic lives on the service
/// (driven by an injected clock) so it's unit-testable without this loop; this just ticks it.
/// </summary>
public sealed class AttentionTimeoutSweeper : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    private readonly AttentionRequestService _service;
    private readonly ILogger<AttentionTimeoutSweeper> _logger;

    public AttentionTimeoutSweeper(AttentionRequestService service, ILogger<AttentionTimeoutSweeper> logger)
    {
        _service = service;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
                _service.SweepExpired(stoppingToken); // fire timeout callbacks in the background
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Attention timeout sweep failed.");
            }
        }
    }
}
