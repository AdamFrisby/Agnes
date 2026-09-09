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

    /// <summary>
    /// Whether the network in use right now bills by the byte. Only the graphical-session screen asks:
    /// it is the one surface that streams continuously, and the cheap tier is a real difference on a
    /// train. False everywhere the platform can't say, including the headless harness.
    /// </summary>
    bool IsMeteredNetwork { get; }

    /// <summary>How this device names itself in a host's paired-device list, so a later revocation is
    /// an obvious choice rather than a guess.</summary>
    string DeviceName { get; }

    IHaptics Haptics { get; }

    /// <summary>
    /// What this device can do with a file an agent sent it. Android's answer (Downloads, the share
    /// sheet, an app that opens the type) is a platform type, so it arrives here rather than being
    /// constructed by a screen — and the headless preview and the tests get the null one, which reports
    /// that it can do nothing and so renders the sheet's buttons disabled rather than lying.
    /// </summary>
    IReceivedFileHandler ReceivedFiles { get; }

    IUiDispatcher Dispatcher { get; }

    MobileSettings Settings { get; }

    HostBook Hosts { get; }
}
