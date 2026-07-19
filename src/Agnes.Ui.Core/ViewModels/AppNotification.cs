namespace Agnes.Ui.Core.ViewModels;

/// <summary>Why a session raised a notification — drives urgency and gating.</summary>
public enum NotificationKind
{
    /// <summary>The agent is blocked awaiting the user (e.g. a permission request).</summary>
    Blocker,

    /// <summary>A turn finished / the agent is done.</summary>
    Completion,

    /// <summary>An error occurred.</summary>
    Error,
}

/// <summary>
/// A user-facing notification a session wants surfaced (in-app toast or OS notification).
/// <paramref name="AnchorId"/> is the transcript item to scroll to when the user activates it.
/// </summary>
public sealed record AppNotification(string Title, string Body, NotificationKind Kind, string SessionId, string? AnchorId = null);

/// <summary>Surfaces <see cref="AppNotification"/>s. Implemented per frontend (toast, OS tray, …).</summary>
public interface INotifier
{
    void Notify(AppNotification notification);
}

/// <summary>No-op notifier (default; tests and headless).</summary>
public sealed class NullNotifier : INotifier
{
    public static readonly NullNotifier Instance = new();

    public void Notify(AppNotification notification) { }
}
