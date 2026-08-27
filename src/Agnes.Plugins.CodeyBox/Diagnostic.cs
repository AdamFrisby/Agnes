namespace Agnes.Plugins.CodeyBox;

/// <summary>
/// Writes a plugin failure somewhere a person can find it.
/// </summary>
/// <remarks>
/// Every failure here is caught and turned into a one-line status in the tab, which is right for the user
/// but leaves nothing to diagnose from: the message is paraphrased by the time it is reported, the type
/// and stack are gone, and an assembly-load failure — whose whole diagnostic value is in the version and
/// the inner reason — reads as a sentence fragment. Standard error is where the desktop head's own output
/// already goes, so this lands beside it with no new configuration.
///
/// <para>A plugin has no access to the host's logger: the client-plugin contract deliberately does not
/// hand one over. If that changes, this becomes a one-line adapter over it.</para>
/// </remarks>
internal static class Diagnostic
{
    public static void Report(string what, Exception error)
    {
        try
        {
            Console.Error.WriteLine($"[codeybox] {what} failed: {error.GetType().FullName}: {error.Message}");
            for (var inner = error.InnerException; inner is not null; inner = inner.InnerException)
            {
                Console.Error.WriteLine($"[codeybox]   caused by {inner.GetType().FullName}: {inner.Message}");
            }

            Console.Error.WriteLine(error.StackTrace);
        }
        catch
        {
            // Reporting a failure must never become one.
        }
    }
}
