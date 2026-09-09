using System.Text.Json;
using Agnes.Agents.Copilot;
using Agnes.Protocol;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Hosting;

/// <summary>
/// The host's local model provider: where to reach it, which model to use, and the compatibility
/// settings a local endpoint needs. Persisted to <c>~/.agnes/local-provider.json</c>.
///
/// <para>There is exactly one, not a list, because Copilot's BYOK axis is process-wide — it is selected
/// by environment variables at launch, so a session cannot pick a different provider from another
/// session's. Modelling it as a collection would offer a choice the CLI cannot honour.</para>
///
/// <para>Read at <b>launch</b> rather than at host start, so a change takes effect on the next session
/// instead of requiring a restart.</para>
/// </summary>
public sealed class LocalProviderRegistry
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ILogger<LocalProviderRegistry>? _logger;
    private Record _current = new();

    public LocalProviderRegistry(string dataFilePath, ILogger<LocalProviderRegistry>? logger = null)
    {
        _path = dataFilePath;
        _logger = logger;
        Load();
    }

    /// <summary>What a client is told. The API key is never included — see <see cref="LocalProviderInfo"/>.</summary>
    public LocalProviderInfo Info()
    {
        lock (_gate)
        {
            return new LocalProviderInfo(
                _current.BaseUrl,
                _current.ProviderType ?? nameof(CopilotProviderType.OpenAi),
                HasApiKey: !string.IsNullOrEmpty(_current.ApiKey),
                _current.ModelId,
                _current.WireModel,
                _current.ExcludedTools ?? [],
                _current.Offline,
                _current.Effort,
                IsConfigured: !string.IsNullOrWhiteSpace(_current.BaseUrl));
        }
    }

    /// <summary>
    /// The options the Copilot adapter should launch with, or null when nothing is configured — in which
    /// case Copilot uses GitHub's own model routing exactly as before.
    /// </summary>
    public CopilotProviderOptions? ProviderOptions()
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_current.BaseUrl))
            {
                return null;
            }

            return new CopilotProviderOptions
            {
                BaseUrl = _current.BaseUrl,
                Type = Enum.TryParse<CopilotProviderType>(_current.ProviderType, ignoreCase: true, out var type)
                    ? type
                    : CopilotProviderType.OpenAi,
                ApiKey = _current.ApiKey,
                ModelId = _current.ModelId,
                WireModel = _current.WireModel,
                // COPILOT_MODEL is left unset when the split is in use: setting it too would override both
                // halves and undo the reason for splitting them.
                Model = string.IsNullOrWhiteSpace(_current.ModelId) ? _current.WireModel : null,
            };
        }
    }

    /// <summary>
    /// Tools to withhold. An empty stored list means "use the recommended set" rather than "withhold
    /// nothing", because the recommended set is what makes a local provider start at all; an operator
    /// who genuinely wants none says so with a single sentinel entry.
    /// </summary>
    public IReadOnlyList<string> ExcludedTools()
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(_current.BaseUrl))
            {
                return [];
            }

            var configured = _current.ExcludedTools ?? [];
            if (configured.Count == 1 && string.Equals(configured[0], "none", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            return configured.Count > 0 ? configured : CopilotLocalCompatibility.RecommendedExcludedTools;
        }
    }

    /// <summary>
    /// The configured reasoning effort, or null for Copilot's own choice. This is the direct control over
    /// the value a strict local server may reject; naming a well-known model id also moves it, but as a
    /// side effect of changing the whole agent profile.
    /// </summary>
    public CopilotEffort? Effort
    {
        get
        {
            lock (_gate)
            {
                return Enum.TryParse<CopilotEffort>(_current.Effort, ignoreCase: true, out var effort)
                    ? effort
                    : null;
            }
        }
    }

    public bool Offline
    {
        get { lock (_gate) { return _current.Offline && !string.IsNullOrWhiteSpace(_current.BaseUrl); } }
    }

    public LocalProviderInfo Save(LocalProviderRequest request)
    {
        lock (_gate)
        {
            _current = new Record
            {
                BaseUrl = Trim(request.BaseUrl),
                ProviderType = Trim(request.ProviderType) ?? _current.ProviderType,
                // Null keeps the stored key; empty clears it. A settings form that always sent the key
                // back would have to be given it first.
                ApiKey = request.ApiKey is null ? _current.ApiKey : Trim(request.ApiKey),
                ModelId = Trim(request.ModelId),
                WireModel = Trim(request.WireModel),
                ExcludedTools = request.ExcludedTools?.Where(t => !string.IsNullOrWhiteSpace(t)).ToArray(),
                Offline = request.Offline,
                Effort = Trim(request.Effort),
            };

            Persist();
            return Info();
        }
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                _current = JsonSerializer.Deserialize<Record>(File.ReadAllText(_path)) ?? new Record();
            }
        }
        catch (Exception ex)
        {
            // A corrupt file must not stop the host: an unconfigured provider simply means Copilot uses
            // GitHub's routing, which is the state every host starts in.
            _logger?.LogWarning(ex, "Couldn't read the local provider settings at {Path}; starting unconfigured.", _path);
            _current = new Record();
        }
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_current, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _path, overwrite: true);

            // The file holds a provider credential, so it is owner-only — the same stance the device
            // registry takes towards its tokens.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Couldn't save the local provider settings to {Path}.", _path);
        }
    }

    private sealed record Record
    {
        public string? BaseUrl { get; init; }
        public string? ProviderType { get; init; }
        public string? ApiKey { get; init; }
        public string? ModelId { get; init; }
        public string? WireModel { get; init; }
        public IReadOnlyList<string>? ExcludedTools { get; init; }
        public bool Offline { get; init; }
        public string? Effort { get; init; }
    }
}
