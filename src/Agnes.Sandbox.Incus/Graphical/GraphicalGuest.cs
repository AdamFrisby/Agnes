using System.Globalization;

namespace Agnes.Sandbox.Incus.Graphical;

/// <summary>
/// The guest half of a graphical sandbox: an X server on the virtual GPU and a window manager, as
/// systemd units baked into the graphical image and started at boot.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is ordinary, boring X. That is the point: the capture happens *below* the guest, at
/// the QEMU framebuffer, so the guest needs no capture software, no VNC server, no agent and no network
/// egress — it only has to draw. Anything that can draw on X works, including a browser.
/// </para>
/// <para>
/// The mode is set once, in the session script, from the size the sandbox was created with. There is no
/// resize path on purpose (see <see cref="Agnes.Sandbox.GraphicalDisplay"/>).
/// </para>
/// </remarks>
internal static class GraphicalGuest
{
    internal const string XorgConfPath = "/etc/X11/xorg.conf.d/10-agnes-virtual.conf";
    internal const string SessionScriptPath = "/usr/local/bin/agnes-session";
    internal const string XUnitPath = "/etc/systemd/system/agnes-x.service";
    internal const string SessionUnitPath = "/etc/systemd/system/agnes-desktop.service";
    internal const string GeometryFile = "/etc/agnes-display-geometry";

    internal static string XorgConf(GraphicalDisplay display) => $"""
        Section "Device"
            Identifier  "AgnesGPU"
            Driver      "modesetting"
        EndSection
        Section "Monitor"
            Identifier  "AgnesMonitor"
            Option      "PreferredMode" "{Size(display)}"
        EndSection
        Section "Screen"
            Identifier  "AgnesScreen"
            Device      "AgnesGPU"
            Monitor     "AgnesMonitor"
            DefaultDepth 24
            SubSection "Display"
                Depth   24
                Modes   "{Size(display)}"
                Virtual {display.Width.ToString(CultureInfo.InvariantCulture)} {display.Height.ToString(CultureInfo.InvariantCulture)}
            EndSubSection
        EndSection

        """;

    /// <summary>
    /// The session: force the mode, paint a background, run a window manager and one terminal. The
    /// mode is forced with an explicit <c>cvt</c> modeline because a headless virtio-gpu advertises no
    /// EDID, so the driver's mode list is whatever it made up — the requested size is usually not on it.
    /// </summary>
    internal static string SessionScript => $$"""
        #!/bin/bash
        set -u
        export DISPLAY=:0
        read -r WIDTH HEIGHT < {{GeometryFile}}
        for _ in $(seq 1 120); do xdpyinfo >/dev/null 2>&1 && break; sleep 0.5; done
        OUTPUT=$(xrandr | awk '/ connected/{print $1; exit}')
        MODE="${WIDTH}x${HEIGHT}"
        if ! xrandr | grep -q " $MODE "; then
          MODELINE=$(cvt "$WIDTH" "$HEIGHT" 60 | sed -n '2s/^Modeline //p' | tr -d '"')
          NAME=$(echo "$MODELINE" | awk '{print $1}')
          xrandr --newmode $MODELINE 2>/dev/null
          xrandr --addmode "$OUTPUT" "$NAME" 2>/dev/null
          xrandr --output "$OUTPUT" --mode "$NAME" 2>/dev/null
        else
          xrandr --output "$OUTPUT" --mode "$MODE" 2>/dev/null
        fi
        xsetroot -solid '#1b1d2b'
        openbox &
        exec xterm -geometry 100x30+40+40 -fa DejaVuSansMono -fs 11 -bg '#101223' -fg '#d8dcf0'

        """;

    internal static string XUnit => """
        [Unit]
        Description=Agnes X server (virtual GPU)
        After=systemd-user-sessions.service

        [Service]
        Type=simple
        ExecStart=/usr/bin/Xorg :0 vt7 -nolisten tcp -noreset
        Restart=always
        RestartSec=2

        [Install]
        WantedBy=multi-user.target

        """;

    internal static string SessionUnit(IncusOptions options) => $"""
        [Unit]
        Description=Agnes graphical session
        Requires=agnes-x.service
        After=agnes-x.service

        [Service]
        Type=simple
        User={options.GuestUserId.ToString(CultureInfo.InvariantCulture)}
        Group={options.GuestGroupId.ToString(CultureInfo.InvariantCulture)}
        Environment=DISPLAY=:0
        Environment=HOME={options.GuestHome}
        ExecStart={SessionScriptPath}
        Restart=always
        RestartSec=2

        [Install]
        WantedBy=multi-user.target

        """;

    internal static string Size(GraphicalDisplay display)
        => $"{display.Width.ToString(CultureInfo.InvariantCulture)}x{display.Height.ToString(CultureInfo.InvariantCulture)}";

    internal static string Geometry(GraphicalDisplay display)
        => $"{display.Width.ToString(CultureInfo.InvariantCulture)} {display.Height.ToString(CultureInfo.InvariantCulture)}\n";
}
