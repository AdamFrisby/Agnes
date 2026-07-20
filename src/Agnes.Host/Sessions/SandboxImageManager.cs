using System.Collections.Concurrent;
using System.Text.Json;
using Agnes.Host.Projects;
using Agnes.Sandbox;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Sessions;

/// <summary>State of the baked baseline image, surfaced to the UI.</summary>
public enum SandboxImageState
{
    Absent,
    Building,
    Ready,
    Failed,
}

public sealed record SandboxImageStatus(SandboxImageState State, string Message, DateTimeOffset? UpdatedAt);

/// <summary>
/// Owns the sandbox-image manifest (persisted at <c>~/.agnes/sandbox-image.json</c>) and drives the
/// bake through <see cref="ISandboxImageBuilder"/>. Ensures the baseline image exists before a
/// sandboxed session launches (baking it if missing, single-flight so concurrent opens share one
/// bake), and exposes live status for the UI.
/// </summary>
public sealed class SandboxImageManager
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ISandboxImageBuilder _builder;
    private readonly string _path;
    private readonly ILogger<SandboxImageManager> _logger;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, Task> _bakesByAlias = new();       // per-alias single-flight
    private readonly ConcurrentDictionary<string, SandboxImageStatus> _statusByAlias = new();
    private volatile SandboxImageStatus _status = new(SandboxImageState.Absent, "Not built yet.", null);

    public SandboxImageManager(ISandboxImageBuilder builder, string dataFilePath, ILogger<SandboxImageManager> logger)
    {
        _builder = builder;
        _path = dataFilePath;
        _logger = logger;
    }

    public SandboxImageStatus Status => _status;

    public SandboxImageManifest Manifest => Load();

    /// <summary>The image alias sessions launch from.</summary>
    public string Alias => Load().Alias;

    /// <summary>Whether an adapter's CLI is baked into the current image (for sandboxed availability).</summary>
    public bool ImageHasAgent(string adapterId) => Load().Agents.Any(a => a.AdapterId == adapterId);

    /// <summary>The image alias a project's sessions launch from (unique per project).</summary>
    public static string ProjectAlias(Project project) => $"agnes-proj-{project.Id}";

    /// <summary>Whether an adapter's CLI is in a project's sandbox manifest (sandboxed availability).</summary>
    public bool ImageHasAgent(Project project, string adapterId) => project.Sandbox.Agents.Any(a => a.AdapterId == adapterId);

    /// <summary>The bake status for a specific image alias.</summary>
    public SandboxImageStatus StatusFor(string alias)
        => _statusByAlias.TryGetValue(alias, out var status) ? status : new SandboxImageStatus(SandboxImageState.Absent, "Not built yet.", null);

    /// <summary>
    /// Ensures a project's sandbox image is baked (from its own manifest, to its own alias) and returns
    /// the alias its sessions launch from. Concurrent callers for the same project share one bake.
    /// </summary>
    public async Task<string> EnsureForProjectAsync(Project project, CancellationToken cancellationToken = default)
    {
        var alias = ProjectAlias(project);
        if (await _builder.ImageExistsAsync(alias, cancellationToken).ConfigureAwait(false))
        {
            SetStatus(alias, new SandboxImageStatus(SandboxImageState.Ready, $"{alias} ready.", DateTimeOffset.UtcNow));
            return alias;
        }

        await BakeAsync(project.Sandbox with { Alias = alias }, cancellationToken).ConfigureAwait(false);
        return alias;
    }

    /// <summary>Rebuilds a project's image from its manifest (used when the project's sandbox is saved).</summary>
    public Task RebuildForProjectAsync(Project project, CancellationToken cancellationToken = default)
        => BakeAsync(project.Sandbox with { Alias = ProjectAlias(project) }, cancellationToken);

    /// <summary>Bakes the baseline image if it isn't present. Concurrent callers share one bake.</summary>
    public async Task EnsureAsync(CancellationToken cancellationToken = default)
    {
        var manifest = Load();
        if (await _builder.ImageExistsAsync(manifest.Alias, cancellationToken).ConfigureAwait(false))
        {
            _status = new SandboxImageStatus(SandboxImageState.Ready, $"{manifest.Alias} ready.", DateTimeOffset.UtcNow);
            return;
        }

        await BakeAsync(manifest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Saves a new manifest and rebuilds the image (used by the UI on save / rebuild).</summary>
    public Task SaveAndRebuildAsync(SandboxImageManifest manifest, CancellationToken cancellationToken = default)
    {
        Persist(manifest);
        return BakeAsync(manifest, cancellationToken);
    }

    public Task RebuildAsync(CancellationToken cancellationToken = default) => BakeAsync(Load(), cancellationToken);

    private Task BakeAsync(SandboxImageManifest manifest, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_bakesByAlias.TryGetValue(manifest.Alias, out var running) && !running.IsCompleted)
            {
                return running; // single-flight per alias — join the in-progress bake
            }

            var bake = RunBakeAsync(manifest, cancellationToken);
            _bakesByAlias[manifest.Alias] = bake;
            return bake;
        }
    }

    private async Task RunBakeAsync(SandboxImageManifest manifest, CancellationToken cancellationToken)
    {
        SetStatus(manifest.Alias, new SandboxImageStatus(SandboxImageState.Building, $"Building {manifest.Alias}…", DateTimeOffset.UtcNow));
        try
        {
            // Update only the message, preserving the current state — Progress<T> posts asynchronously,
            // so a late progress callback must not clobber the final Ready/Failed state.
            var progress = new Progress<string>(line =>
                SetStatus(manifest.Alias, StatusFor(manifest.Alias) with { Message = line, UpdatedAt = DateTimeOffset.UtcNow }));
            await _builder.BuildImageAsync(manifest, progress, cancellationToken).ConfigureAwait(false);
            SetStatus(manifest.Alias, new SandboxImageStatus(SandboxImageState.Ready, $"{manifest.Alias} ready.", DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            SetStatus(manifest.Alias, new SandboxImageStatus(SandboxImageState.Failed, ex.Message, DateTimeOffset.UtcNow));
            _logger.LogError(ex, "Sandbox image bake failed");
            throw;
        }
    }

    // Track per-alias status and mirror the most recent into the legacy global status.
    private void SetStatus(string alias, SandboxImageStatus status)
    {
        _statusByAlias[alias] = status;
        _status = status;
    }

    private SandboxImageManifest Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                return JsonSerializer.Deserialize<SandboxImageManifest>(File.ReadAllText(_path), Options) ?? new SandboxImageManifest();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load sandbox image manifest from {Path}", _path);
        }

        return new SandboxImageManifest();
    }

    private void Persist(SandboxImageManifest manifest)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(manifest, Options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist sandbox image manifest to {Path}", _path);
        }
    }
}
