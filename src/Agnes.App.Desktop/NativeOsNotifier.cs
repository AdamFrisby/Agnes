using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Agnes.App.Desktop;

/// <summary>
/// Fires a real OS notification by shelling out to the platform's notifier — no extra dependencies:
/// <c>notify-send</c> on Linux, <c>osascript</c> on macOS, a PowerShell toast on Windows. Best-effort;
/// used when the app is in the background so a finished/blocked turn reaches the user outside the window.
/// </summary>
internal static class NativeOsNotifier
{
    public static void Notify(string title, string body)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Run("notify-send", ["-a", "Agnes", title, body]);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var script = $"display notification {AppleQuote(body)} with title {AppleQuote(title)}";
                Run("osascript", ["-e", script]);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Run("powershell", ["-NoProfile", "-Command", WindowsToastScript(title, body)]);
            }
        }
        catch
        {
            // Notifications are best-effort; never let one take the app down.
        }
    }

    private static void Run(string file, string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        Process.Start(psi);
    }

    private static string AppleQuote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string WindowsToastScript(string title, string body)
    {
        static string Ps(string s) => s.Replace("'", "''");
        // Uses the built-in Windows.UI.Notifications toast API via PowerShell — no install required.
        return
            "[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime] | Out-Null;" +
            "$t=[Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02);" +
            $"$n=$t.GetElementsByTagName('text');$n.Item(0).AppendChild($t.CreateTextNode('{Ps(title)}'))|Out-Null;" +
            $"$n.Item(1).AppendChild($t.CreateTextNode('{Ps(body)}'))|Out-Null;" +
            "[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('Agnes').Show([Windows.UI.Notifications.ToastNotification]::new($t));";
    }
}
