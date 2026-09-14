using System.Diagnostics;
using Agnes.App.Desktop.Controls;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Agnes.Screenshots;

/// <summary>
/// What one transcript row costs to build and lay out, control by control — the number behind a page of
/// rows realising from cold. <c>--row-bench 40</c> builds forty of each kind in a window and times them.
/// </summary>
public static class RowBench
{
    public static int? TryParse(string[] args)
    {
        var i = Array.IndexOf(args, "--row-bench");
        return i < 0 ? null : i + 1 < args.Length && int.TryParse(args[i + 1], out var n) ? n : 40;
    }

    private const string Message = """
        I looked at the failing tests and the cause is the **cache key** — it uses the file's mtime, which the
        checkout step resets. Three things to do:

        1. Key the cache on the content hash instead of the mtime.
        2. Keep the mtime in the manifest for the *display* only.
        3. Add a test that touches a file without changing it and expects a hit.

        The relevant bit of `CacheKey.cs`:

        ```csharp
        public static string For(FileInfo file)
            => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.FullName)));
        ```

        That leaves one open question about the manifest format — see the note in `docs/cache.md`, section
        "Keys", which I have not changed. Shall I go ahead with the hash, or do you want the mtime kept as a
        secondary key?
        """;

    public static void Run(int count)
    {
        Time("SelectableTextBlock", count, () => new SelectableTextBlock { Text = Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        Time("MarkdownViewer", count, () => new MarkdownViewer { Markdown = Message });
        Time("MarkdownMessageViewer", count, () => new MarkdownMessageViewer { Markdown = Message });
        // Twice more, so a first-run cost (JIT, regex compilation, font loading) shows as the gap.
        Time("MarkdownMessageViewer (again)", count, () => new MarkdownMessageViewer { Markdown = Message });
        Time("MarkdownMessageViewer (again)", count, () => new MarkdownMessageViewer { Markdown = Message });
    }

    private static void Time(string label, int count, Func<Control> make)
    {
        var window = new Window { Width = 900, Height = 700 };
        var panel = new StackPanel();
        window.Content = new ScrollViewer { Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            panel.Children.Add(make());
        }
        var built = sw.Elapsed;
        sw.Restart();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var laidOut = sw.Elapsed;
        Console.WriteLine($"{label,-32} ×{count}: build {built.TotalMilliseconds / count,7:0.00} ms/row · attach+layout {laidOut.TotalMilliseconds / count,7:0.00} ms/row · total {(built + laidOut).TotalMilliseconds / count,7:0.00} ms/row");
        window.Close();
    }
}
