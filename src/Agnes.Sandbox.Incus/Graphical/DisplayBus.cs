using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Agnes.Sandbox.Incus.Graphical;

/// <summary>
/// The private D-Bus a graphical sandbox's QEMU and this host meet on — one bus per instance, because
/// QEMU claims the well-known name <c>org.qemu</c> and two of them on one bus would fight over it.
/// </summary>
/// <remarks>
/// <para>
/// Three facts about Incus's QEMU force this shape, all of them established the hard way (see
/// docs/graphical-sandbox.md):
/// </para>
/// <list type="bullet">
/// <item>QEMU's <c>-display dbus</c> only accepts a peer-to-peer socket via the QMP <c>add_client</c>
/// command, and Incus keeps the QMP socket in a root-only directory. So we use bus mode, which needs a
/// bus daemon.</item>
/// <item>QEMU connects to that bus while still <em>root</em> (Incus's <c>-run-with user=incus</c> drops
/// privileges after display setup), so the bus policy has to admit root, and the socket has to sit on a
/// path root can traverse — which a 0700 <c>$XDG_RUNTIME_DIR</c> is not.</item>
/// <item>The socket therefore lives under a 0711 directory, and access control moves into the bus
/// policy: a uid allowlist, enforced by D-Bus's EXTERNAL auth against SO_PEERCRED, not by the
/// directory mode.</item>
/// </list>
/// <para>
/// The daemon is deliberately <c>--fork</c>ed and <em>not</em> a child of this process: if it died with
/// the Agnes host, every running graphical sandbox would lose its capture permanently (QEMU cannot be
/// told to reconnect without restarting the VM). Orphaned, it outlives a host restart and the next
/// <c>AttachAsync</c> simply reconnects. <see cref="Stop"/> reaps it when the sandbox is deleted.
/// </para>
/// </remarks>
internal sealed class DisplayBus
{
    private readonly IncusOptions _options;
    private readonly ILogger _logger;

    internal DisplayBus(IncusOptions options, ILogger logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>Directory holding one instance's bus socket, config and pid file.</summary>
    internal string DirectoryFor(string instance)
    {
        IncusInputValidation.ValidateInstanceName(instance);
        return Path.Combine(_options.DisplayRuntimeDirectory, instance);
    }

    internal string SocketPathFor(string instance) => Path.Combine(DirectoryFor(instance), "bus");

    internal string AddressFor(string instance) => "unix:path=" + SocketPathFor(instance);

    /// <summary>The <c>raw.qemu</c> line that points this instance's QEMU at its bus.</summary>
    internal string RawQemuFor(string instance) => "-display dbus,addr=" + AddressFor(instance);

    /// <summary>
    /// The <c>raw.apparmor</c> lines the instance needs. Incus confines QEMU with a per-instance
    /// AppArmor profile that knows nothing about our socket; without both of these the VM refuses to
    /// start (a file <c>connect</c> denial first, then a D-Bus mediation denial on <c>Hello</c> — the
    /// bus daemon's own AppArmor mediation, not the kernel's file check).
    /// </summary>
    internal string RawAppArmor()
        => $"{_options.DisplayRuntimeDirectory}/** rwk,\ndbus (send, receive, bind) bus=session,\n";

    /// <summary>
    /// Ensures a bus daemon is listening for this instance, starting one if the socket is absent or
    /// stale. Idempotent: safe to call on every create, start and attach.
    /// </summary>
    internal async Task EnsureRunningAsync(string instance, CancellationToken cancellationToken = default)
    {
        var dir = DirectoryFor(instance);
        var socket = SocketPathFor(instance);
        if (await CanConnectAsync(socket, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        Directory.CreateDirectory(_options.DisplayRuntimeDirectory);
        Directory.CreateDirectory(dir);
        // 0711 on both: QEMU (root at connect time, then uid 'incus') must traverse to the socket, but
        // must not be able to list what else is here. The socket itself is world-connectable — the uid
        // allowlist in the policy below is what actually keeps other local users out.
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(_options.DisplayRuntimeDirectory, TraverseOnly);
            File.SetUnixFileMode(dir, TraverseOnly);
        }

        var configPath = Path.Combine(dir, "bus.conf");
        await File.WriteAllTextAsync(configPath, RenderBusConfig(socket), cancellationToken).ConfigureAwait(false);

        // A stale socket file from a crashed daemon would make dbus-daemon fail to bind.
        File.Delete(socket);

        var psi = new ProcessStartInfo(_options.DbusDaemonPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--config-file=" + configPath);
        psi.ArgumentList.Add("--fork");
        psi.ArgumentList.Add("--print-pid=1");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start '{_options.DbusDaemonPath}'.");
        var pid = (await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dbus-daemon for {instance} failed ({process.ExitCode}): {stderr.Trim()}");
        }

        if (pid.Length > 0)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "bus.pid"), pid, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Started display bus for {Instance} at {Socket} (pid {Pid})", instance, socket, pid);
    }

    /// <summary>Reaps an instance's bus daemon and its directory. Best effort — called on delete.</summary>
    internal void Stop(string instance)
    {
        var dir = DirectoryFor(instance);
        try
        {
            var pidFile = Path.Combine(dir, "bus.pid");
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), CultureInfo.InvariantCulture, out var pid))
            {
                using var process = Process.GetProcessById(pid);
                process.Kill();
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or NotSupportedException)
        {
            // Already gone, or never started. Either way there's nothing to reap.
            _logger.LogDebug(ex, "No display bus to stop for {Instance}", instance);
        }

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not remove display bus directory for {Instance}", instance);
        }
    }

    /// <summary>
    /// The bus policy: a uid allowlist. <c>root</c> is on it because QEMU connects before Incus's
    /// <c>-run-with user=incus</c> takes effect, <c>incus</c> because that is who it becomes, and the
    /// host's own user because that is who reads the framebuffer. Everyone else is refused at connect,
    /// which is the only access control this socket has (its directory is traverse-for-all).
    /// </summary>
    internal string RenderBusConfig(string socketPath) => $"""
        <!DOCTYPE busconfig PUBLIC "-//freedesktop//DTD D-Bus Bus Configuration 1.0//EN"
         "http://www.freedesktop.org/standards/dbus/1.0/busconfig.dtd">
        <busconfig>
          <type>session</type>
          <listen>unix:path={socketPath}</listen>
          <auth>EXTERNAL</auth>
          <policy context="default">
            <deny user="*"/>
            <allow user="root"/>
            <allow user="{_options.DisplayBusQemuUser}"/>
            <allow user="{Environment.UserName}"/>
            <allow own="*"/>
            <allow send_type="method_call"/>
            <allow send_type="signal"/>
            <allow send_type="method_return"/>
            <allow send_type="error"/>
            <allow receive_type="method_call"/>
            <allow receive_type="signal"/>
            <allow receive_type="method_return"/>
            <allow receive_type="error"/>
          </policy>
        </busconfig>

        """;

    private const UnixFileMode TraverseOnly =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private static async Task<bool> CanConnectAsync(string socketPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(socketPath))
        {
            return false;
        }

        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await probe.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
