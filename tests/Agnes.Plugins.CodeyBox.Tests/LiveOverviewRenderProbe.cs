using Agnes.Plugins.CodeyBox.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// Renders the real overview — live inputs from a configured orchestrator through the real model and the
/// real view — to PNGs, so a layout can be judged against the data it will actually meet rather than
/// against a hand-built sample. Silent where no orchestrator is configured or reachable, and read-only
/// against one that is. Writes only when <c>AGNES_OVERVIEW_LIVE_SHOTS</c> names a directory.
/// </summary>
public sealed class LiveOverviewRenderProbe
{
    [Fact]
    public async Task Renders_the_live_overview_at_two_widths()
    {
        var shotDir = Environment.GetEnvironmentVariable("AGNES_OVERVIEW_LIVE_SHOTS");
        var options = CodeyBoxOptions.Resolve();
        if (string.IsNullOrEmpty(shotDir) || !options.IsConfigured)
        {
            return;
        }

        var inputs = await LiveOverviewInputs.GatherAsync(options);
        if (inputs is null)
        {
            return;
        }

        var overview = OverviewModel.Build(inputs);
        Directory.CreateDirectory(shotDir);
        Render(overview, Path.Combine(shotDir, "overview-wide.png"), 1500, 2300);
        Render(overview, Path.Combine(shotDir, "overview-narrow.png"), 1000, 2300);
    }

    private static void Render(Overview overview, string path, double width, double height)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(PixelAppBuilder));
        session.Dispatch(() =>
        {
            var window = new Window
            {
                Width = width,
                Height = height,
                Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x16)),
                Content = new ScrollViewer
                {
                    Padding = new Avalonia.Thickness(14),
                    HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                    Content = new OverviewView { DataContext = overview },
                },
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame();
            if (frame is null)
            {
                return;
            }
            using var file = File.Create(path);
            frame.Save(file, new PngBitmapEncoderOptions());
            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }
}

/// <summary>
/// The same stand-in theme the render tests use, on a platform that actually draws: Skia behind the
/// headless windowing, so the captured frame has pixels in it.
/// </summary>
internal sealed class PixelApp : Avalonia.Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            ["Bg"] = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x16)),
            ["Panel"] = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x20)),
            ["PanelAlt"] = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x2C)),
            ["Line"] = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x38)),
            ["Fg"] = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xF2)),
            ["FgDim"] = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xB6)),
            ["FgFaint"] = new SolidColorBrush(Color.FromRgb(0x76, 0x76, 0x86)),
            ["Accent"] = new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6)),
            ["Danger"] = new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0x9B)),
            ["StatusWorking"] = new SolidColorBrush(Color.FromRgb(0x5A, 0xB8, 0xF0)),
            ["StatusAttention"] = new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x39)),
            ["StatusDone"] = new SolidColorBrush(Color.FromRgb(0x5A, 0xD6, 0xA8)),
            ["StatusError"] = new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0x9B)),
            ["StatusIdle"] = new SolidColorBrush(Color.FromRgb(0x76, 0x76, 0x86)),
        });
    }
}

public static class PixelAppBuilder
{
    public static Avalonia.AppBuilder BuildAvaloniaApp()
        => Avalonia.AppBuilder.Configure<PixelApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>The live gather, shared by the probes: the same reads the section makes, GET only.</summary>
internal static class LiveOverviewInputs
{
    public static async Task<OverviewInputs?> GatherAsync(CodeyBoxOptions options)
    {
        await using var client = new CodeyBoxClient(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        IReadOnlyList<WorkItemRow> items;
        try
        {
            items = await client.ListWorkItemsAsync(cts.Token);
        }
        catch (Exception)
        {
            return null;   // configured but not running
        }

        var probes = await client.GetQuotaProbesAsync(cts.Token);
        var concurrency = await client.GetConcurrencyAsync(cts.Token);
        var queue = await client.GetQueueStatusAsync(cts.Token);
        var health = await client.GetTransitionHealthAsync(cts.Token);
        var projects = await client.GetProjectsAsync(cts.Token);
        var live = items.Where(i => !i.IsTerminal || i.IsFailed).ToList();
        var traces = new List<ItemAuditProgress>();
        foreach (var item in live)
        {
            var rows = await client.GetAuditProgressAsync(item.Id, cts.Token);
            if (rows.Count > 0)
            {
                traces.Add(new ItemAuditProgress(item.Id, rows));
            }
        }

        var questions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in items.Where(i => i.State == "NeedsOperatorInput"))
        {
            var open = (await client.GetQuestionsAsync(item.Id, cts.Token)).Count(q => q.IsOpen);
            if (open > 0)
            {
                questions[item.Id] = open;
            }
        }

        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var rowsOut = new List<QuotaHistoryRow>();
        foreach (var agent in probes.Where(p => p.IsKnown).Select(p => p.Agent).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            rowsOut.AddRange(await client.GetQuotaHistoryAsync(agent, since, cts.Token));
        }

        var ceilings = projects
            .Where(p => p.AuditMaxIterations > 0)
            .ToDictionary(p => p.Id, p => p.AuditMaxIterations, StringComparer.Ordinal);
        var effort = new List<ItemEffort>();
        foreach (var item in items.Where(i => i.State == "Done").OrderByDescending(i => i.UpdatedAt).Take(OverviewModel.BurnSample)
                     .Concat(items.Where(i => !i.IsTerminal)))
        {
            var runs = await client.GetAgentRunsAsync(item.Id, cts.Token);
            if (runs.Count > 0)
            {
                effort.Add(new ItemEffort(item.Id, ItemEffort.ActiveTime(runs, DateTimeOffset.UtcNow, item.IsActive), item.State == "Done", item.UpdatedAt));
            }
        }
        // The operator's own accumulated history, read-only: what the real tab would judge against today.
        var history = new OverviewHistory().Read();
        return new OverviewInputs(
            DateTimeOffset.Now, items, traces, questions, queue, concurrency, probes,
            QuotaHistoryMap.ToBurnDown(rowsOut, probes, DateTimeOffset.UtcNow), health, history, ceilings)
        {
            Effort = effort,
        };
    }
}
