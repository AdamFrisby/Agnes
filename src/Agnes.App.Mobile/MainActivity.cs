using Agnes.App.Mobile.Services;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia.Android;

namespace Agnes.App.Mobile;

/// <summary>
/// The single Android activity hosting the Avalonia surface. Everything above it is Avalonia — the app
/// is one activity with an in-app navigation stack (see <c>ShellViewModel</c>), which is what lets a
/// back gesture pop a sheet, then a screen, then leave, without activity churn. Config changes are
/// handled in-process, so a rotation never rebuilds a live session view.
/// </summary>
[Activity(
    Label = "Agnes",
    Theme = "@style/AgnesSplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    Exported = true,
    // AdjustResize plus Avalonia's insets manager: the bottom composer lifts with the soft keyboard
    // rather than being covered by it.
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode
        | ConfigChanges.Density | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize
        | ConfigChanges.KeyboardHidden | ConfigChanges.Keyboard)]
// Deep link: scanning a host's pairing QR with the system camera opens the app straight into the
// connect screen, pre-filled. Typing an address and a code on a phone keyboard is the worst part of
// setup, so it's worth removing entirely where the host can offer a code.
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "agnes")]
public sealed class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        AndroidHost.AttachActivity(this);
        base.OnCreate(savedInstanceState);
        HandleLaunch(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        HandleLaunch(intent);
    }

    protected override void OnResume()
    {
        base.OnResume();
        AndroidHost.AttachActivity(this);
        AndroidHost.SetForeground(true);
    }

    protected override void OnPause()
    {
        AndroidHost.SetForeground(false);
        base.OnPause();
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == AndroidCapabilities.DictationRequest)
        {
            var spoken = resultCode == Result.Ok
                ? data?.GetStringArrayListExtra(global::Android.Speech.RecognizerIntent.ExtraResults)?.FirstOrDefault()
                : null;
            AndroidCapabilities.CompleteDictation(spoken);
        }
    }

    /// <summary>
    /// Routes what launched us: a notification tap carries the session it came from, and an
    /// <c>agnes://pair?host=…&amp;code=…</c> link carries a host address and pairing code.
    /// </summary>
    private static void HandleLaunch(Intent? intent)
    {
        if (intent is null || (Avalonia.Application.Current as App)?.Shell is not { } shell)
        {
            return;
        }

        if (intent.GetStringExtra(AndroidNotifier.SessionExtra) is { Length: > 0 } sessionId)
        {
            shell.Dispatcher.Post(() => shell.OpenSessionById(sessionId));
            return;
        }

        if (intent.Action == Intent.ActionView && intent.Data is { Scheme: "agnes" } uri)
        {
            var host = uri.GetQueryParameter("host");
            // `grant` is a scanned one-time secret; `code` is a typed bootstrap code. `session` rides
            // along when the QR came from a specific session, so scanning lands you in it.
            var grant = uri.GetQueryParameter("grant");
            var code = uri.GetQueryParameter("code");
            var session = uri.GetQueryParameter("session");
            if (!string.IsNullOrWhiteSpace(host))
            {
                shell.Dispatcher.Post(() => shell.BeginPairing(host!, grant ?? code, session, autoSubmit: !string.IsNullOrWhiteSpace(grant)));
            }
        }
    }
}
