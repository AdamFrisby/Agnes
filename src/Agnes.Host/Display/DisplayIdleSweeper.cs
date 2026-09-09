using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Display;

/// <summary>
/// The heartbeat behind the two things that must happen when <em>nothing</em> is happening: a capture
/// connection nobody is using goes away, and a human hold nobody is touching expires.
/// <para>
/// Both are timers, and neither can hang off a request — that is the point of them. A host with no graphical
/// sessions does one dictionary scan every fifteen seconds and nothing else.
/// </para>
/// </summary>
public sealed class DisplayIdleSweeper : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    /// <summary>How long a broker must be unused before it collapses. Long enough to span the gap between two
    /// of an agent's tool calls, short enough that a closed tab doesn't leave a VM streaming pixels.</summary>
    private static readonly TimeSpan CollapseGrace = TimeSpan.FromSeconds(30);

    private readonly DisplayBrokerRegistry _brokers;
    private readonly ILogger<DisplayIdleSweeper> _logger;

    public DisplayIdleSweeper(DisplayBrokerRegistry brokers, ILogger<DisplayIdleSweeper> logger)
    {
        _brokers = brokers;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var collapsed = await _brokers.SweepAsync(CollapseGrace, stoppingToken).ConfigureAwait(false);
                if (collapsed > 0)
                {
                    _logger.LogInformation("Collapsed {Count} idle display broker(s).", collapsed);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The display idle sweep failed; it will run again.");
            }
        }
    }
}
