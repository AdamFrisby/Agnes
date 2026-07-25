using Android.App;
using Android.Content;

namespace Agnes.App.Mobile;

/// <summary>
/// The one place the app touches ambient Android state. Kept deliberately thin: capabilities the view
/// models consume (haptics, notifications, links, dictation) are interfaces or delegates resolved from
/// here once at startup, so nothing above the shell references <c>Android.*</c>.
/// </summary>
internal static class AndroidHost
{
    private static Context? _context;
    private static Activity? _activity;
    private static bool _foreground = true;

    /// <summary>The application context, available from <c>Application.OnCreate</c> onward.</summary>
    public static Context Context => _context
        ?? throw new System.InvalidOperationException("AndroidHost.Attach must run before the Avalonia app starts.");

    /// <summary>The hosting activity while one is alive — needed to start an activity for a result.</summary>
    public static Activity? Activity => _activity;

    /// <summary>Whether the app is in the foreground — notifications are suppressed while it is.</summary>
    public static bool IsForeground => _foreground;

    public static void Attach(Context context) => _context = context;

    public static void AttachActivity(Activity? activity) => _activity = activity;

    public static void SetForeground(bool value) => _foreground = value;
}
