using Microsoft.Extensions.Logging;

namespace Agnes.Sandbox.Incus.Graphical;

/// <summary>
/// An Incus sandbox that was launched with a display, and so can be watched and driven.
/// </summary>
/// <remarks>
/// A separate type rather than a flag on <see cref="IncusSandbox"/> because the capability is what a
/// caller tests: <c>sandbox is IDisplaySource</c> has to be false for a headless VM, and a class that
/// implements the interface and throws would be a lie the compiler can't catch.
/// </remarks>
internal sealed class GraphicalIncusSandbox : IncusSandbox, IDisplaySource
{
    private readonly IncusOptions _options;
    private readonly DisplayBus _bus;
    private readonly ILogger _logger;

    internal GraphicalIncusSandbox(
        string id, IncusOptions options, IIncusCliRunner cli, ILogger logger, DisplayBus bus, GraphicalDisplay display)
        : base(id, options, cli, logger)
    {
        _options = options;
        _bus = bus;
        _logger = logger;
        Display = display;
    }

    /// <inheritdoc />
    public GraphicalDisplay Display { get; }

    public async Task<IDisplaySession> OpenDisplayAsync(CancellationToken cancellationToken = default)
    {
        // The bus daemon outlives this process, so a session opened after a host restart just reconnects.
        // What it cannot survive is the *daemon* dying while the VM runs: QEMU connected once, at boot, and
        // has no reconnect. Starting a fresh bus here would leave us talking to nobody, so this call only
        // ensures one exists — a VM whose QEMU lost its bus needs a restart to be capturable again.
        await _bus.EnsureRunningAsync(Id, cancellationToken).ConfigureAwait(false);
        return await IncusDisplaySession.OpenAsync(
            _bus.AddressFor(Id), _options.DisplayReadyTimeout, _logger, cancellationToken).ConfigureAwait(false);
    }

    public override async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // The bus has to be listening before QEMU starts: QEMU connects to it during display setup and
        // fails to boot at all if it isn't there.
        await _bus.EnsureRunningAsync(Id, cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await base.DeleteAsync(cancellationToken).ConfigureAwait(false);
        _bus.Stop(Id);
    }
}
