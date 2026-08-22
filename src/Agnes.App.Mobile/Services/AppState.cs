namespace Agnes.App.Mobile.Services;

/// <summary>Device-local preferences. Small on purpose — anything about a session or a host belongs to
/// the host, not to this phone.</summary>
public sealed record MobileSettings(
    string Theme = "System",
    double TextScale = 1.0,
    bool Haptics = true,
    bool NotifyOnBlocked = true,
    bool NotifyOnComplete = true,
    bool ReducedMotion = false,
    bool ShowThinking = false,
    string LastWorkingDirectory = "",
    bool DemoSeeded = false)
{
    public static MobileSettings Load() => JsonStore.Load("mobile-settings.json", new MobileSettings());

    public void Save() => JsonStore.Save("mobile-settings.json", this);
}

/// <summary>A host this device has paired with. The token is the per-device bearer token issued at
/// pairing — revocable host-side, and never shared between devices.</summary>
public sealed record SavedHost(string Name, string Url, string Token, string? Fingerprint = null);

/// <summary>The device's paired hosts.</summary>
public static class HostRegistry
{
    private const string File = "mobile-hosts.json";

    public static IReadOnlyList<SavedHost> Load()
        => JsonStore.Load(File, new List<SavedHost>());

    public static void Save(IEnumerable<SavedHost> hosts)
        => JsonStore.Save(File, hosts.ToList());
}

/// <summary>A session this device has open, so a relaunch can resume it (the host holds the truth; this
/// is only the pointer back to it).</summary>
/// <param name="Title">What to call it. Starts as the working directory and is replaced by the agent's
/// own name for the conversation once it produces one.</param>
/// <param name="WorkingDirectory">Where it runs on the host. Kept separately from <paramref name="Title"/>
/// precisely because the title gets replaced — without this, the project name is lost the moment the
/// agent renames the conversation. Empty for sessions saved before this field existed; the card falls
/// back to the title then.</param>
public sealed record SavedSession(
    string HostName,
    string HostUrl,
    string Token,
    string SessionId,
    string AdapterId,
    string Title,
    string WorkingDirectory = "",
    bool Pinned = false);

/// <summary>
/// Sessions this device was told to stop showing. Discovery lists what the <b>host</b> has, so without
/// this a forgotten session would reappear on the very next refresh and "remove from this device" would
/// mean nothing. Ids only — the session itself is untouched and still running.
/// </summary>
public static class DismissedSessions
{
    private const string File = "mobile-dismissed.json";

    public static HashSet<string> Load()
        => new(JsonStore.Load(File, new List<string>()), StringComparer.Ordinal);

    public static void Add(string sessionId)
    {
        var all = Load();
        if (all.Add(sessionId))
        {
            JsonStore.Save(File, all.ToList());
        }
    }

    /// <summary>Forgets the dismissal — used when the user deliberately reopens a session.</summary>
    public static void Remove(string sessionId)
    {
        var all = Load();
        if (all.Remove(sessionId))
        {
            JsonStore.Save(File, all.ToList());
        }
    }
}

/// <summary>The device's open sessions, most recent first.</summary>
public static class SessionRegistry
{
    private const string File = "mobile-sessions.json";

    public static IReadOnlyList<SavedSession> Load()
        => JsonStore.Load(File, new List<SavedSession>());

    public static void Save(IEnumerable<SavedSession> sessions)
        => JsonStore.Save(File, sessions.ToList());
}
