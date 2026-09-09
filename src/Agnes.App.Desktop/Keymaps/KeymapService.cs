namespace Agnes.App.Desktop.Keymaps;

public interface IKeymapLauncher
{
    Task LaunchAsync(string path);
}

public sealed class SystemKeymapLauncher : IKeymapLauncher
{
    public Task LaunchAsync(string path)
    {
        _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true })
            ?? throw new InvalidOperationException($"The operating system did not open '{path}'.");
        return Task.CompletedTask;
    }
}

public sealed class KeymapService : IDisposable
{
    private readonly string _defaultJson;
    private readonly string? _platformJson;
    private readonly FileSystemWatcher? _watcher;
    private readonly IKeymapLauncher _launcher;
    private readonly TimeProvider _timeProvider;
    private readonly ITimer? _statusTimer;
    private readonly object _gate = new();
    private CancellationTokenSource? _debounce;
    private EffectiveKeymap _effective;
    private DateTimeOffset? _lastLoadedUserChange;

    public KeymapService(
        string defaultJson,
        string? platformJson,
        string userPath,
        IKeymapLauncher? launcher = null,
        bool watch = true,
        TimeProvider? timeProvider = null)
    {
        _defaultJson = defaultJson;
        _platformJson = platformJson;
        UserPath = Path.GetFullPath(userPath);
        _launcher = launcher ?? new SystemKeymapLauncher();
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (!TryBuild(includeUser: false, out _effective, out var diagnostic, out _))
        {
            throw new InvalidOperationException($"The packaged keymap is invalid: {diagnostic}");
        }

        if (File.Exists(UserPath))
        {
            if (TryBuild(includeUser: true, out var withUser, out diagnostic, out var loadedChange))
            {
                _effective = withUser;
                _lastLoadedUserChange = loadedChange;
            }
            else
            {
                // A broken file present at startup is treated exactly like a broken live save: packaged
                // bindings remain usable and Settings explains why the override was rejected.
                Diagnostic = diagnostic;
            }
        }
        var directory = Path.GetDirectoryName(UserPath)!;
        Directory.CreateDirectory(directory);
        if (watch)
        {
            _watcher = new FileSystemWatcher(directory, Path.GetFileName(UserPath))
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileChanged;
            _watcher.Deleted += OnFileChanged;
            _watcher.Renamed += OnFileChanged;
            _statusTimer = _timeProvider.CreateTimer(
                static state => ((KeymapService)state!).StatusChanged?.Invoke(state, EventArgs.Empty),
                this,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(1));
        }
    }

    public static KeymapService CreateDefault(string settingsPath, IKeymapLauncher? launcher = null, bool watch = true)
    {
        static string Read(string asset)
        {
            var resource = $"Agnes.App.Desktop.Assets.Keymaps.{asset}";
            using var stream = typeof(KeymapService).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Packaged keymap resource '{resource}' was not found.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        return new KeymapService(
            Read("default.json"),
            OperatingSystem.IsMacOS() ? Read("macos.json") : null,
            Path.Combine(Path.GetDirectoryName(settingsPath)!, "keymap.json"),
            launcher,
            watch);
    }

    public event EventHandler? Changed;
    public event EventHandler? StatusChanged;

    public string UserPath { get; }
    public EffectiveKeymap Effective { get { lock (_gate) return _effective; } }
    public KeymapDiagnostic? Diagnostic { get; private set; }
    public bool UserFileExists => File.Exists(UserPath);
    public string Status
    {
        get
        {
            if (Diagnostic is not null) return $"Last save rejected · {Diagnostic}";
            DateTimeOffset? changed;
            lock (_gate) changed = _lastLoadedUserChange;
            return changed is { } loaded
                ? $"Live reload active · latest change {RelativeAge(loaded, _timeProvider.GetUtcNow())}"
                : "Live reload active · using packaged defaults";
        }
    }

    public static string RelativeAge(DateTimeOffset changedAt, DateTimeOffset now)
    {
        var elapsed = now - changedAt;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed < TimeSpan.FromMinutes(1)) return "just now";
        if (elapsed < TimeSpan.FromHours(1))
        {
            var minutes = (int)elapsed.TotalMinutes;
            return minutes == 1 ? "1 minute ago" : $"{minutes} minutes ago";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            var hours = (int)elapsed.TotalHours;
            return hours == 1 ? "1 hour ago" : $"{hours} hours ago";
        }

        var days = (int)elapsed.TotalDays;
        return days == 1 ? "1 day ago" : $"{days} days ago";
    }

    public async Task EditAsync()
    {
        if (!File.Exists(UserPath))
        {
            await File.WriteAllTextAsync(UserPath, "[]\n").ConfigureAwait(false);
            Reload();
        }

        await _launcher.LaunchAsync(UserPath).ConfigureAwait(false);
    }

    public bool Reload()
    {
        if (!TryBuild(includeUser: true, out var next, out var diagnostic, out var loadedChange))
        {
            Diagnostic = diagnostic;
            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }

        lock (_gate)
        {
            _effective = next;
            _lastLoadedUserChange = loadedChange;
        }
        Diagnostic = null;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private bool TryBuild(
        bool includeUser,
        out EffectiveKeymap keymap,
        out KeymapDiagnostic? diagnostic,
        out DateTimeOffset? loadedChange)
    {
        loadedChange = null;
        var layers = new List<(string Name, string Json)> { ("default.json", _defaultJson) };
        if (_platformJson is not null) layers.Add(("macos.json", _platformJson));
        if (includeUser && File.Exists(UserPath))
        {
            try
            {
                layers.Add((UserPath, File.ReadAllText(UserPath)));
                loadedChange = new DateTimeOffset(File.GetLastWriteTimeUtc(UserPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                keymap = new EffectiveKeymap([]);
                diagnostic = new KeymapDiagnostic($"{UserPath}: {ex.Message}");
                return false;
            }
        }

        return KeymapLoader.TryResolve(layers, out keymap, out diagnostic);
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        lock (_gate)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = new CancellationTokenSource();
            _ = ReloadAfterDelayAsync(_debounce.Token);
        }
    }

    private async Task ReloadAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            Reload();
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _statusTimer?.Dispose();
        _watcher?.Dispose();
        lock (_gate)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = null;
        }
    }
}
