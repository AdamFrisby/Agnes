using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Agnes.App.Desktop;

/// <summary>
/// Makes <c>agnes://</c> links clickable, by telling the desktop that this executable handles them.
///
/// Agnes ships as a downloaded, self-contained binary with no installer, so there is no install step to do
/// this in — the app registers itself, at startup, against wherever it happens to be running from. That also
/// means it follows the binary if you move it, and re-points at the newest copy if you keep several.
///
/// The three platforms need three different things, and only two of them can be done from inside a running
/// process:
///
/// <list type="bullet">
///   <item><b>Linux</b> — a <c>.desktop</c> entry declaring <c>x-scheme-handler/agnes</c>, written to the
///     per-user applications directory. No root, no package manager.</item>
///   <item><b>Windows</b> — a protocol key under <c>HKCU\Software\Classes</c>. Per-user, so no elevation.</item>
///   <item><b>macOS</b> — <c>CFBundleURLTypes</c> in the app bundle's <c>Info.plist</c>, which Launch Services
///     reads from the bundle on disk. A process cannot register itself; the bundle has to exist and be
///     structured correctly, so <c>build.sh</c> produces one and this no-ops.</item>
/// </list>
///
/// Registration is best-effort throughout. A machine that refuses it still runs Agnes perfectly well — you
/// paste the pairing link into the address field instead of clicking it.
/// </summary>
public static class UriScheme
{
    public const string Scheme = "agnes";

    /// <summary>
    /// Registers this executable as the handler, unless it already is. Safe to call on every launch: it reads
    /// what's registered first and only writes when that has changed, so the common case touches nothing.
    /// </summary>
    public static void Register()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                return;
            }

            if (OperatingSystem.IsLinux())
            {
                RegisterLinux(executable);
            }
            else if (OperatingSystem.IsWindows())
            {
                RegisterWindows(executable);
            }

            // macOS: declared by the bundle's Info.plist; nothing a process can do at runtime.
        }
        catch (Exception)
        {
            // Never a reason to fail a launch — the link just stays un-clickable.
        }
    }

    /// <summary>
    /// The <c>.desktop</c> entry contents for an executable. Pure, so the format is testable without touching
    /// a real desktop environment. <c>%u</c> is what passes the clicked URL through as an argument.
    /// </summary>
    public static string DesktopEntry(string executablePath) =>
        $"""
        [Desktop Entry]
        Type=Application
        Name=Agnes
        Comment=Remote interface to coding agents
        Exec={Escape(executablePath)} %u
        Terminal=false
        Categories=Development;
        MimeType=x-scheme-handler/{Scheme};
        StartupWMClass=Agnes

        """;

    /// <summary>Whether an argument is an <c>agnes://</c> link, i.e. how we were launched by a click.</summary>
    public static bool IsSchemeArgument(string? argument)
        => argument is not null
           && argument.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase);

    private static void RegisterLinux(string executable)
    {
        var applications = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "applications");
        Directory.CreateDirectory(applications);
        var file = Path.Combine(applications, "agnes.desktop");
        var contents = DesktopEntry(executable);

        // Only rewrite on change: this runs at every launch, and a needless write would re-run the (slow)
        // database update below each time.
        if (File.Exists(file) && File.ReadAllText(file) == contents)
        {
            return;
        }

        File.WriteAllText(file, contents);
        Run("update-desktop-database", applications);
    }

    private static void RegisterWindows(string executable)
    {
        // Written with reg.exe rather than Microsoft.Win32.Registry: the app targets plain net10.0 so it can
        // build and run on every desktop, and the registry types aren't in that surface. reg.exe is always
        // present on Windows, and the keys are per-user so nothing needs elevation.
        var command = $"\"{executable}\" \"%1\"";
        Run("reg", "add", $@"HKCU\Software\Classes\{Scheme}", "/ve", "/d", "URL:Agnes Protocol", "/f");
        Run("reg", "add", $@"HKCU\Software\Classes\{Scheme}", "/v", "URL Protocol", "/d", string.Empty, "/f");
        Run("reg", "add", $@"HKCU\Software\Classes\{Scheme}\shell\open\command", "/ve", "/d", command, "/f");
    }

    private static void Run(string file, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);
            process?.WaitForExit(5000);
        }
        catch (Exception)
        {
            // The tool isn't installed, or the environment forbids it. Registration simply doesn't happen.
        }
    }

    private static string Escape(string path) => path.Contains(' ', StringComparison.Ordinal) ? $"\"{path}\"" : path;
}
