using Agnes.Ui.Core.ViewModels;
using Android.App;
using Android.Content;

namespace Agnes.App.Mobile.Services;

/// <summary>
/// Posts session notifications to the Android shade. The point of this app is that the agent keeps
/// working while the phone is in a pocket, so a blocked agent has to be able to reach out — but only
/// when the user isn't already looking at it: notifications are suppressed while the app is foreground
/// (the in-app banner and haptic cover that case).
///
/// Two channels, because they deserve different urgency: a blocked agent interrupts, a finished turn
/// does not.
/// </summary>
public sealed class AndroidNotifier : INotifier
{
    private const string BlockedChannel = "agnes.blocked";
    private const string ActivityChannel = "agnes.activity";

    /// <summary>Extra key carrying the session a notification came from, so tapping it deep-links.</summary>
    public const string SessionExtra = "agnes.sessionId";

    private readonly Context _context;
    private readonly Func<MobileSettings> _settings;
    private readonly Func<bool> _isForeground;
    private int _nextId = 1000;

    public AndroidNotifier(Context context, Func<MobileSettings> settings, Func<bool> isForeground)
    {
        _context = context;
        _settings = settings;
        _isForeground = isForeground;
        EnsureChannels();
    }

    private void EnsureChannels()
    {
        try
        {
            if (_context.GetSystemService(Context.NotificationService) is not NotificationManager manager)
            {
                return;
            }

            var blocked = new NotificationChannel(BlockedChannel, "Waiting on you", NotificationImportance.High)
            {
                Description = "An agent is blocked and needs an approval or an answer.",
            };
            blocked.EnableVibration(true);

            var activity = new NotificationChannel(ActivityChannel, "Session activity", NotificationImportance.Default)
            {
                Description = "A turn finished, or a session reported an error.",
            };

            manager.CreateNotificationChannel(blocked);
            manager.CreateNotificationChannel(activity);
        }
        catch
        {
            // A device that refuses channels just gets no notifications.
        }
    }

    public void Notify(AppNotification notification)
    {
        var settings = _settings();
        var wanted = notification.Kind switch
        {
            NotificationKind.Blocker => settings.NotifyOnBlocked,
            NotificationKind.Completion => settings.NotifyOnComplete,
            _ => settings.NotifyOnBlocked || settings.NotifyOnComplete,
        };

        // Foreground means the user is already watching this; the in-app surface handles it.
        if (!wanted || _isForeground())
        {
            return;
        }

        try
        {
            Post(notification);
        }
        catch
        {
            // Notification posting can fail (revoked permission, OEM quirks) — never propagate.
        }
    }

    private void Post(AppNotification notification)
    {
        if (_context.GetSystemService(Context.NotificationService) is not NotificationManager manager)
        {
            return;
        }

        var intent = new Intent(_context, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        intent.PutExtra(SessionExtra, notification.SessionId);

        var pending = PendingIntent.GetActivity(
            _context,
            notification.SessionId.GetHashCode(StringComparison.Ordinal),
            intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var channel = notification.Kind == NotificationKind.Blocker ? BlockedChannel : ActivityChannel;
        var builder = new Notification.Builder(_context, channel)
            .SetContentTitle(notification.Title)
            .SetContentText(notification.Body)
            .SetStyle(new Notification.BigTextStyle().BigText(notification.Body))
            .SetSmallIcon(Resource.Drawable.ic_notification)
            .SetColor(unchecked((int)0xFF8A55EE)) // brand violet, tinting the shade's accent
            .SetAutoCancel(true)
            .SetContentIntent(pending);

        if (notification.Kind == NotificationKind.Blocker)
        {
            builder.SetCategory(Notification.CategoryCall); // treated as needing a person, not just news
        }

        // One notification per session, replaced in place: a chatty agent must not bury the shade.
        manager.Notify(IdFor(notification.SessionId), builder.Build());
    }

    private readonly Dictionary<string, int> _ids = [];

    private int IdFor(string sessionId)
    {
        if (_ids.TryGetValue(sessionId, out var id))
        {
            return id;
        }

        id = _nextId++;
        _ids[sessionId] = id;
        return id;
    }

    /// <summary>Clears a session's notification — called when the user opens that session.</summary>
    public void Clear(string sessionId)
    {
        try
        {
            if (_ids.TryGetValue(sessionId, out var id)
                && _context.GetSystemService(Context.NotificationService) is NotificationManager manager)
            {
                manager.Cancel(id);
            }
        }
        catch
        {
            // best-effort
        }
    }
}
