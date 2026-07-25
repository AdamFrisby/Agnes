using Agnes.App.Mobile.Services;
using Agnes.Ui.Core;

namespace Agnes.App.Mobile.ViewModels;

/// <summary>Tone of a transient in-app message.</summary>
public enum ToastKind
{
    Info,
    Success,
    Warning,
    Danger,
}

/// <summary>
/// What a screen is allowed to ask of the shell: navigate, summon a sheet, say something, feel
/// something. Screens take this rather than the concrete shell so they stay independent of the
/// navigation host, and so each one can be exercised without standing up the whole app.
/// </summary>
public interface IAppShell
{
    /// <summary>Pushes a full-screen page onto the navigation stack.</summary>
    void Push(PageViewModel page);

    /// <summary>Pops the top page.</summary>
    void Pop();

    /// <summary>Pops back to the tab root.</summary>
    void PopToRoot();

    /// <summary>Opens a bottom sheet, replacing any sheet already open.</summary>
    void ShowSheet(SheetViewModel sheet);

    /// <summary>Dismisses the open sheet, if any.</summary>
    void CloseSheet();

    /// <summary>Shows a brief in-app message.</summary>
    void Toast(string message, ToastKind kind = ToastKind.Info);

    /// <summary>Copies text to the system clipboard, with a confirmation toast.</summary>
    void CopyToClipboard(string text, string what);

    /// <summary>Opens a URL in the browser.</summary>
    void OpenUrl(string url);

    /// <summary>Asks the system for a spoken phrase, or null when dictation is unavailable/cancelled.</summary>
    Task<string?> DictateAsync();

    /// <summary>Whether this device can dictate — the composer's mic is hidden entirely when it can't,
    /// rather than shipping a button that does nothing.</summary>
    bool CanDictate { get; }

    /// <summary>How this device names itself in a host's paired-device list, so a later revocation is
    /// an obvious choice rather than a guess.</summary>
    string DeviceName { get; }

    IHaptics Haptics { get; }

    IUiDispatcher Dispatcher { get; }

    MobileSettings Settings { get; }

    HostBook Hosts { get; }
}
