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
/// Three channels, because they deserve different urgency: a blocked agent interrupts, a finished turn
/// does not, and a file the agent sent is news you can silence on its own without losing the other two.
/// </summary>
public sealed class AndroidNotifier : INotifier
{
    private const string BlockedChannel = "agnes.blocked";
    private const string ActivityChannel = "agnes.activity";
    private const string FilesChannel = "agnes.files";

    /// <summary>Extra key carrying the session a notification came from, so tapping it deep-links.</summary>
    public const string SessionExtra = "agnes.sessionId";

    /// <summary>Extra key carrying the transcript item the notification was about, so tapping it lands on
    /// that moment rather than at the top of the session.</summary>
    public const string AnchorExtra = "agnes.anchorId";

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

            // Default importance, deliberately: a file is worth telling you about, but it is not a person
            // waiting. Its own channel so it can be silenced in Android's settings without also silencing
            // the one that says an agent is stuck.
            var files = new NotificationChannel(FilesChannel, "Files sent to you", NotificationImportance.Default)
            {
                Description = "An agent produced a file for you — a screenshot, a report, a build.",
            };

            manager.CreateNotificationChannel(blocked);
            manager.CreateNotificationChannel(activity);
            manager.CreateNotificationChannel(files);
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
            NotificationKind.File => settings.NotifyOnFile,
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
        if (notification.AnchorId is { Length: > 0 } anchor)
        {
            intent.PutExtra(AnchorExtra, anchor);
        }

        var pending = PendingIntent.GetActivity(
            _context,
            // Distinct per notification, not per session: two PendingIntents that compare equal are the
            // same object, so a file tap would otherwise reuse the blocked one's extras and open the
            // wrong moment.
            IdFor(KeyFor(notification)),
            intent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var channel = notification.Kind switch
        {
            NotificationKind.Blocker => BlockedChannel,
            NotificationKind.File => FilesChannel,
            _ => ActivityChannel,
        };
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

        // One notification per session *per kind*, replaced in place: a chatty agent must not bury the
        // shade, but a file arriving must not silently overwrite "this agent is blocked on you" either.
        manager.Notify(IdFor(KeyFor(notification)), builder.Build());
    }

    /// <summary>The shade slot a notification occupies: its session, and whether it's a file.</summary>
    private static string KeyFor(AppNotification notification)
        => notification.Kind == NotificationKind.File
            ? notification.SessionId + "#file"
            : notification.SessionId;

    private readonly Dictionary<string, int> _ids = [];

    private int IdFor(string key)
    {
        if (_ids.TryGetValue(key, out var id))
        {
            return id;
        }

        id = _nextId++;
        _ids[key] = id;
        return id;
    }

    /// <summary>Clears a session's notifications — called when the user opens that session.</summary>
    public void Clear(string sessionId)
    {
        try
        {
            if (_context.GetSystemService(Context.NotificationService) is not NotificationManager manager)
            {
                return;
            }

            foreach (var key in new[] { sessionId, sessionId + "#file" })
            {
                if (_ids.TryGetValue(key, out var id))
                {
                    manager.Cancel(id);
                }
            }
        }
        catch
        {
            // best-effort
        }
    }
}
