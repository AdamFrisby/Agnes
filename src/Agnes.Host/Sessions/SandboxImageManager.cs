using System.Text.Json;
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
    private Task? _bake;
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
            if (_bake is { IsCompleted: false })
            {
                return _bake; // single-flight — join the in-progress bake
            }

            _bake = RunBakeAsync(manifest, cancellationToken);
            return _bake;
        }
    }

    private async Task RunBakeAsync(SandboxImageManifest manifest, CancellationToken cancellationToken)
    {
        _status = new SandboxImageStatus(SandboxImageState.Building, $"Building {manifest.Alias}…", DateTimeOffset.UtcNow);
        try
        {
            var progress = new Progress<string>(line =>
                _status = _status with { Message = line, UpdatedAt = DateTimeOffset.UtcNow });
            await _builder.BuildImageAsync(manifest, progress, cancellationToken).ConfigureAwait(false);
            _status = new SandboxImageStatus(SandboxImageState.Ready, $"{manifest.Alias} ready.", DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _status = new SandboxImageStatus(SandboxImageState.Failed, ex.Message, DateTimeOffset.UtcNow);
            _logger.LogError(ex, "Sandbox image bake failed");
            throw;
        }
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
