using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// The plugin's local sample store: the only source of the sparklines and control bands, because the
/// orchestrator answers about now and keeps no series of its own.
///
/// <para>The thinning rules are tested as a function, without a disk, because that is where the decisions
/// are. The file half is tested for the two things that actually go wrong in the field: a half-written
/// file, and a file that is not there at all.</para>
/// </summary>
public class OverviewHistoryTests
{
    private static OverviewSample At(DateTimeOffset at, int landed = 0)
        => new(at, landed, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static string TempPath() => Path.Combine(
        Path.GetTempPath(), $"agnes-overview-{Guid.NewGuid():N}", "history.json");

    [Fact]
    public void Within_a_day_samples_are_kept_at_five_minute_spacing()
    {
        var now = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

        // One a minute for an hour: dense enough that most of them say nothing the previous one didn't.
        var minutely = Enumerable.Range(0, 60).Select(i => At(now.AddMinutes(-i))).ToList();

        var kept = OverviewHistory.Thin(minutely, now);

        Assert.Equal(12, kept.Count);
        Assert.Equal(now, kept[^1].At);                     // the latest sample always survives
        Assert.True(kept.Zip(kept.Skip(1)).All(p => p.Second.At > p.First.At));
        Assert.All(
            kept.Zip(kept.Skip(1)),
            p => Assert.True(p.Second.At - p.First.At >= TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Past_a_day_it_falls_to_hourly_then_daily_then_stops()
    {
        var now = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

        var samples = new List<OverviewSample>();
        samples.AddRange(Enumerable.Range(0, 6).Select(i => At(now.AddDays(-2).AddMinutes(-10 * i))));   // 1-7d
        samples.AddRange(Enumerable.Range(0, 6).Select(i => At(now.AddDays(-30).AddHours(-2 * i))));     // 7-60d
        samples.Add(At(now.AddDays(-90)));                                                               // older

        var kept = OverviewHistory.Thin(samples, now);

        // Six samples spanning 50 minutes two days back collapse to the one hour they fall in — except
        // where they straddle an hour boundary, which these do.
        var hourly = kept.Where(s => now - s.At < TimeSpan.FromDays(7)).ToList();
        Assert.Equal(2, hourly.Count);

        // Twelve hours' worth thirty days back is one day.
        Assert.Single(kept, s => now - s.At > TimeSpan.FromDays(7));

        // Nothing at all from beyond the horizon: a two-month-old normal is not this fleet's normal.
        Assert.DoesNotContain(kept, s => now - s.At > TimeSpan.FromDays(60));
    }

    [Fact]
    public void A_sample_from_a_clock_that_went_backwards_is_treated_as_the_newest()
    {
        var now = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

        var kept = OverviewHistory.Thin([At(now.AddHours(2), landed: 7)], now);

        // The alternative is dropping it as "older than sixty days", which is how a clock skew turns into
        // a history that silently stops growing.
        Assert.Equal(7, Assert.Single(kept).Landed7d);
    }

    [Fact]
    public void Appending_reads_back_what_it_kept()
    {
        var path = TempPath();
        var history = new OverviewHistory(path);
        var now = DateTimeOffset.UtcNow;

        history.Append(At(now.AddHours(-1), landed: 1), now);
        var kept = history.Append(At(now, landed: 2), now);

        Assert.Equal([1, 2], kept.Select(s => s.Landed7d));
        Assert.Equal([1, 2], new OverviewHistory(path).Read().Select(s => s.Landed7d));

        // Nothing is left behind for the next writer to trip over.
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void A_missing_file_reads_as_empty()
        => Assert.Empty(new OverviewHistory(TempPath()).Read());

    [Fact]
    public void A_corrupt_file_reads_as_empty_rather_than_throwing()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "[{\"at\":\"2026-09-0");   // a write that did not finish

        var history = new OverviewHistory(path);

        // A cache the app can always rebuild must never be a reason the overview fails to open.
        Assert.Empty(history.Read());
        Assert.Single(history.Append(At(DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void The_path_is_injectable_and_defaults_beside_the_client_plugins()
    {
        var path = TempPath();
        Assert.Equal(path, new OverviewHistory(path).FilePath);

        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Agnes",
                "codeybox-overview-history.json"),
            OverviewHistory.DefaultPath);
    }
}
