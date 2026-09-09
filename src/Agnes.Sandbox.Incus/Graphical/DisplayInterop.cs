using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Agnes.Sandbox.Incus.Graphical;

/// <summary>
/// The two things the display capture needs from libc: a socket pair to hand QEMU one end of, and our
/// own uid for D-Bus EXTERNAL auth. Linux-only, like the whole Incus backend.
/// </summary>
internal static class DisplayInterop
{
    private const int AfUnix = 1;
    private const int SockStream = 1;

    [DllImport("libc", SetLastError = true)]
    private static extern int socketpair(int domain, int type, int protocol, [Out] int[] sv);

    [DllImport("libc")]
    private static extern uint geteuid();

    /// <summary>
    /// A connected pair of unix stream sockets. <paramref name="peer"/> is handed to QEMU over D-Bus
    /// (it becomes the D-Bus authentication *server* on it); <paramref name="ours"/> is the socket we
    /// speak peer-to-peer D-Bus on, as the client.
    /// </summary>
    internal static void CreateSocketPair(out SafeFileHandle peer, out Socket ours)
    {
        var fds = new int[2];
        if (socketpair(AfUnix, SockStream, 0, fds) != 0)
        {
            throw new IOException($"socketpair failed: {Marshal.GetLastPInvokeError()}");
        }

        peer = new SafeFileHandle((IntPtr)fds[0], ownsHandle: true);
        ours = new Socket(new SafeSocketHandle((IntPtr)fds[1], ownsHandle: true));
    }

    internal static string EffectiveUserId() => geteuid().ToString(CultureInfo.InvariantCulture);

    /// <summary>The host's machine id, which D-Bus's Peer interface is expected to be able to answer with.</summary>
    internal static string MachineId()
    {
        foreach (var path in (string[])["/etc/machine-id", "/var/lib/dbus/machine-id"])
        {
            if (File.Exists(path))
            {
                var id = File.ReadAllText(path).Trim();
                if (id.Length > 0)
                {
                    return id;
                }
            }
        }

        return Guid.NewGuid().ToString("N");
    }
}
