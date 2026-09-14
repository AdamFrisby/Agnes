using System.Diagnostics;

namespace Agnes.App.Mobile.Services;

/// <summary>
/// Timestamped launch milestones, for answering "where did the six seconds go" with measurements rather
/// than guesses.
///
/// <para><b>Off by default, and free when off.</b> Every method is <see cref="ConditionalAttribute"/> on
/// <c>AGNES_STARTUP_TRACE</c>, so without that symbol the compiler deletes the call sites — not the call,
/// the whole expression including any string it would have built. Nothing here runs, and the class is
/// never even touched, so its statics never initialize.</para>
///
/// <para><b>To re-measure:</b> publish with <c>-p:AgnesStartupTrace=true</c> (the csproj turns that into
/// the symbol) and read the <c>agnes.startup</c> tag out of logcat:</para>
/// <code>
/// adb logcat -c &amp;&amp; adb shell am force-stop dev.agnes.app
/// adb shell am start -W dev.agnes.app/crc6462ff24370015d9d7.MainActivity
/// adb logcat -d -v time | grep -E "Start proc|agnes.startup|Displayed dev.agnes"
/// </code>
///
/// <para>The number each line carries is milliseconds since the first milestone — which is the earliest
/// managed code this head controls, not process start. Process start is the <c>ActivityManager: Start
/// proc</c> line in the same log; subtract its wall clock from the first <c>agnes.startup</c> line to get
/// what the runtime spent before any of our code ran.</para>
/// </summary>
public static class StartupTrace
{
    private static readonly long Origin = Stopwatch.GetTimestamp();
    private static readonly HashSet<string> Seen = new(StringComparer.Ordinal);

    /// <summary>Records a milestone.</summary>
    [Conditional("AGNES_STARTUP_TRACE")]
    public static void Mark(string milestone) => Write(milestone);

    /// <summary>
    /// Records a milestone the first time only — for the ones on a path that repeats (a layout pass, a
    /// collection change), where all that is wanted is when it first happened.
    /// </summary>
    [Conditional("AGNES_STARTUP_TRACE")]
    public static void MarkOnce(string milestone)
    {
        lock (Seen)
        {
            if (!Seen.Add(milestone))
            {
                return;
            }
        }

        Write(milestone);
    }

    /// <summary>
    /// Watches the shell for the two moments that actually matter to someone holding the phone: the
    /// session list having rows at all (from this device's own registry), and having the host's answer.
    /// </summary>
    [Conditional("AGNES_STARTUP_TRACE")]
    public static void Watch(ViewModels.ShellViewModel shell)
    {
        shell.Sessions.All.CollectionChanged += (_, _) =>
        {
            if (shell.Sessions.All.Count > 0)
            {
                MarkOnce("sessions.listed.local (rows from this device's registry)");
            }
        };
    }

    private static void Write(string milestone)
    {
        var ms = (Stopwatch.GetTimestamp() - Origin) * 1000.0 / Stopwatch.Frequency;
        var line = string.Create(
            System.Globalization.CultureInfo.InvariantCulture, $"{ms,8:F1} ms  {milestone}");
#if ANDROID
        global::Android.Util.Log.Info("agnes.startup", line);
#else
        // The headless preview harness links this file so the instrumented call sites compile there too.
        Console.WriteLine($"[agnes.startup] {line}");
#endif
    }
}
