using Android.Content;
using Android.Speech;

namespace Agnes.App.Mobile.Services;

/// <summary>
/// The platform affordances that make the phone client feel native rather than ported: dictation, the
/// clipboard, and opening a link. Each is guarded — a device without the capability reports it, and the
/// UI hides the control rather than offering a button that does nothing.
/// </summary>
public static class AndroidCapabilities
{
    /// <summary>Request code for the speech-recognition activity result.</summary>
    internal const int DictationRequest = 0x5044; // "PD"

    private static TaskCompletionSource<string?>? _dictation;

    /// <summary>
    /// Whether this device can turn speech into text. Typing a paragraph of instructions on a phone
    /// keyboard is the single worst part of driving an agent from one, so this is worth having — but
    /// plenty of devices ship without a recognizer, hence the probe.
    /// </summary>
    public static bool CanDictate
    {
        get
        {
            try
            {
                var intent = new Intent(RecognizerIntent.ActionRecognizeSpeech);
                return intent.ResolveActivity(AndroidHost.Context.PackageManager!) is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Opens the system dictation UI and resolves with what was said, or null if it was
    /// cancelled or unavailable.</summary>
    public static Task<string?> DictateAsync(string prompt)
    {
        if (AndroidHost.Activity is not { } activity)
        {
            return Task.FromResult<string?>(null);
        }

        // Only one dictation can be in flight; a second request supersedes the first rather than
        // leaving an orphaned continuation.
        _dictation?.TrySetResult(null);
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dictation = pending;

        try
        {
            var intent = new Intent(RecognizerIntent.ActionRecognizeSpeech);
            intent.PutExtra(RecognizerIntent.ExtraLanguageModel, RecognizerIntent.LanguageModelFreeForm);
            intent.PutExtra(RecognizerIntent.ExtraPrompt, prompt);
            intent.PutExtra(RecognizerIntent.ExtraMaxResults, 1);
            activity.StartActivityForResult(intent, DictationRequest);
        }
        catch
        {
            _dictation = null;
            return Task.FromResult<string?>(null);
        }

        return pending.Task;
    }

    /// <summary>Called by the activity when the recognizer returns.</summary>
    internal static void CompleteDictation(string? spoken)
    {
        var pending = _dictation;
        _dictation = null;
        pending?.TrySetResult(spoken);
    }

    /// <summary>Request code for the notification-permission prompt.</summary>
    private const int NotificationRequest = 0x504E; // "PN"

    /// <summary>
    /// Asks for notification permission, once, on Android 13+ where it's a runtime grant.
    ///
    /// Declaring POST_NOTIFICATIONS in the manifest is not enough on API 33+ — without the runtime grant
    /// every notification is silently dropped, which for this app means an agent can get blocked and
    /// never tell you. Called after the first session list has loaded rather than at cold start, so the
    /// prompt arrives when there is visibly something worth being told about.
    /// </summary>
    public static void RequestNotificationPermission()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) || AndroidHost.Activity is not { } activity)
        {
            return;
        }

        try
        {
            const string permission = global::Android.Manifest.Permission.PostNotifications;
            if (activity.CheckSelfPermission(permission) == global::Android.Content.PM.Permission.Granted)
            {
                return;
            }

            activity.RequestPermissions([permission], NotificationRequest);
        }
        catch
        {
            // A refused or unavailable prompt just means no notifications; never break startup over it.
        }
    }

    public static void CopyToClipboard(string text)
    {
        try
        {
            if (AndroidHost.Context.GetSystemService(Context.ClipboardService) is ClipboardManager clipboard)
            {
                clipboard.PrimaryClip = ClipData.NewPlainText("Agnes", text);
            }
        }
        catch
        {
            // The clipboard can be unavailable (restricted profiles); the toast still confirms the intent.
        }
    }

    public static void OpenUrl(string url)
    {
        try
        {
            var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url));
            intent.SetFlags(ActivityFlags.NewTask);
            AndroidHost.Context.StartActivity(intent);
        }
        catch
        {
            // No browser installed, or a malformed URL — nothing useful to do but not crash.
        }
    }
}
