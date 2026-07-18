namespace Agnes.Ui.Core.ViewModels;

/// <summary>A clear, high-level state banner shown above a session.</summary>
public enum SessionBanner
{
    None,
    Offline,
    Reconnecting,
    Interrupted,
    Stale,
}
