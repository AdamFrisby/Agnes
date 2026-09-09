using System.Text.Json;

namespace Agnes.Plugins.CodeyBox;

/// <summary>
/// The overview's memory of itself.
/// </summary>
/// <remarks>
/// <para>The orchestrator keeps no series of the numbers the overview reports — it answers about now, and
/// only about now. So the sparklines and the control band under each vital have to come from somewhere,
/// and the honest place is here: one sample per refresh, written by the client that took it.</para>
///
/// <para>That makes this a cache, not a record. It lives beside the client-plugins folder under
/// <c>%APPDATA%/Agnes</c>, a missing or corrupt file reads as "no history yet" rather than throwing, and
/// nothing downstream may treat its absence as an error — a fresh install has no history and a fresh
/// install is the ordinary case.</para>
///
/// <para>Thinning is separated from the I/O (<see cref="Thin"/> is pure) because the rules are the part
/// worth testing and a disk is not needed to test them.</para>
/// </remarks>
public sealed class OverviewHistory
{
    /// <summary>Below this spacing two samples say the same thing twice; a refresh can fire far more often
    /// than the fleet actually changes.</summary>
    private static readonly TimeSpan RecentSpacing = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan FullResolution = TimeSpan.FromHours(24);
    private static readonly TimeSpan HourlyHorizon = TimeSpan.FromDays(7);
    private static readonly TimeSpan DailyHorizon = TimeSpan.FromDays(60);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _path;

    /// <param name="path">Where to keep the file. Injected so a test never touches the real one — the
    /// default is the only path the app itself uses.</param>
    public OverviewHistory(string? path = null) => _path = path ?? DefaultPath;

    /// <summary>Beside the client-plugins folder, under the same root, because this belongs to the plugin
    /// rather than to any host it happens to be pointed at.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Agnes",
        "codeybox-overview-history.json");

    /// <summary>The file this instance reads and writes.</summary>
    public string FilePath => _path;

    /// <summary>
    /// Everything kept, oldest first — the order <see cref="OverviewInputs.History"/> is specified in.
    /// Never throws: an unreadable or malformed file is indistinguishable to a caller from an empty one,
    /// which is the correct behaviour for a cache the app can always rebuild.
    /// </summary>
    public IReadOnlyList<OverviewSample> Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var samples = JsonSerializer.Deserialize<List<OverviewSample>>(File.ReadAllText(_path), Json);
            return samples is null ? [] : [.. samples.OrderBy(s => s.At)];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>
    /// Adds a sample, thins the result and persists it, returning what the next build should see. A write
    /// that fails is reported and swallowed: losing a sparkline is not a reason to fail the refresh that
    /// produced it.
    /// </summary>
    public IReadOnlyList<OverviewSample> Append(OverviewSample sample, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(sample);

        var kept = Thin([.. Read(), sample], now ?? sample.At);
        Write(kept);
        return kept;
    }

    /// <summary>
    /// The retention rules, as a function.
    ///
    /// <para>Within a day, everything at five-minute spacing or wider — close enough to see a fleet stall
    /// while it is happening. Out to a week, one sample an hour; out to two months, one a day; older than
    /// that, nothing, because the control band is a trailing norm and a two-month-old normal is not this
    /// fleet's normal any more.</para>
    ///
    /// <para>Walked newest-first so the sample kept in each bucket is its most recent one, and so the very
    /// latest sample always survives however densely the refreshes landed.</para>
    /// </summary>
    /// <returns>The kept samples, oldest first.</returns>
    public static IReadOnlyList<OverviewSample> Thin(IEnumerable<OverviewSample> samples, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var kept = new List<OverviewSample>();
        DateTimeOffset? lastRecent = null;
        var hours = new HashSet<long>();
        var days = new HashSet<long>();

        foreach (var sample in samples.OrderByDescending(s => s.At))
        {
            // A clock that has gone backwards leaves samples "in the future"; they are the newest thing
            // there is, so they belong in the full-resolution band rather than being dropped as ancient.
            var age = now - sample.At;
            if (age < TimeSpan.Zero)
            {
                age = TimeSpan.Zero;
            }

            if (age <= FullResolution)
            {
                if (lastRecent is { } previous && previous - sample.At < RecentSpacing)
                {
                    continue;
                }

                lastRecent = sample.At;
                kept.Add(sample);
            }
            else if (age <= HourlyHorizon)
            {
                if (hours.Add(sample.At.UtcTicks / TimeSpan.TicksPerHour))
                {
                    kept.Add(sample);
                }
            }
            else if (age <= DailyHorizon && days.Add(sample.At.UtcTicks / TimeSpan.TicksPerDay))
            {
                kept.Add(sample);
            }
        }

        kept.Reverse();
        return kept;
    }

    /// <summary>
    /// Writes through a temp file and one <see cref="File.Move(string, string, bool)"/>, so a crash or a
    /// second client mid-write leaves the previous file whole rather than a truncated one that then reads
    /// as "no history".
    /// </summary>
    private void Write(IReadOnlyList<OverviewSample> samples)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_path);
            if (directory is { Length: > 0 })
            {
                Directory.CreateDirectory(directory);
            }

            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(samples, Json));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            Diagnostic.Report("overview-history-write", ex);
        }
    }
}
