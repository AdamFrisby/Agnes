using Agnes.Abstractions;
using Agnes.Acp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agnes.Host;

/// <summary>
/// A user-configured extra ACP agent backend. Host-side config only (the CLI must live on this
/// host): each entry is materialized into a generic <see cref="AcpAgentAdapter"/> and joins the
/// same adapter registry as the built-in plugins, so clients cannot tell it from a shipped adapter.
/// </summary>
public sealed record CustomAcpBackendConfig
{
    /// <summary>Stable adapter id, e.g. <c>my-acp-cli</c>. Must be unique across custom entries.</summary>
    public required string Id { get; init; }

    /// <summary>Human-friendly name shown in the agent picker.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Executable that speaks ACP over stdio (resolved on PATH or an absolute path).</summary>
    public required string Command { get; init; }

    /// <summary>Arguments that start the CLI in ACP mode.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Extra environment variables for the agent process.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    /// <summary>Preferred initial ACP mode id, if the CLI exposes modes (advisory, not yet enforced).</summary>
    public string? DefaultModeId { get; init; }
}

/// <summary>
/// Materializes <see cref="CustomAcpBackendConfig"/> entries from host configuration into
/// <see cref="AcpAgentAdapter"/> instances. Fail-closed per entry: a malformed, invalid, or
/// duplicate-id entry is skipped (and logged) so the host still starts.
/// </summary>
public static class CustomBackends
{
    /// <summary>Configuration key for the JSON array of custom backends.</summary>
    public const string ConfigurationSection = "Agnes:CustomBackends";

    /// <summary>Builds a generic ACP adapter from a single (already-validated) config entry.</summary>
    public static AcpAgentAdapter Build(CustomAcpBackendConfig config, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        Validate(config);

        var spec = new AcpLaunchSpec
        {
            Command = config.Command,
            Arguments = config.Arguments,
            Environment = config.Environment is { Count: > 0 } env ? env : null,
            Descriptor = new AgentDescriptor
            {
                Id = config.Id,
                DisplayName = config.DisplayName,
            },
        };
        return new AcpAgentAdapter(spec, loggerFactory);
    }

    /// <summary>
    /// Binds the <see cref="ConfigurationSection"/> array and returns the valid entries. Each array
    /// element is bound and validated independently so one bad entry never blocks the rest (or startup).
    /// </summary>
    public static IReadOnlyList<CustomAcpBackendConfig> Load(IConfiguration configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        var result = new List<CustomAcpBackendConfig>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var child in configuration.GetSection(ConfigurationSection).GetChildren())
        {
            CustomAcpBackendConfig? config;
            try
            {
                config = child.Get<CustomAcpBackendConfig>();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Skipping malformed custom ACP backend at '{Path}'.", child.Path);
                continue;
            }

            if (config is null)
            {
                logger.LogWarning("Skipping empty custom ACP backend at '{Path}'.", child.Path);
                continue;
            }

            try
            {
                Validate(config);
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning(ex, "Skipping invalid custom ACP backend at '{Path}'.", child.Path);
                continue;
            }

            if (!seenIds.Add(config.Id))
            {
                logger.LogWarning("Skipping custom ACP backend with duplicate id '{Id}'.", config.Id);
                continue;
            }

            result.Add(config);
        }

        return result;
    }

    /// <summary>
    /// Loads every valid custom backend and registers one <see cref="IAgentAdapter"/> per entry into
    /// the DI container, alongside the built-in adapters.
    /// </summary>
    public static void Register(IServiceCollection services, IConfiguration configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(services);

        foreach (var config in Load(configuration, logger))
        {
            services.AddSingleton<IAgentAdapter>(sp =>
                Build(config, sp.GetRequiredService<ILoggerFactory>()));
        }
    }

    private static void Validate(CustomAcpBackendConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Id))
        {
            throw new ArgumentException("Custom ACP backend requires a non-empty Id.", nameof(config));
        }

        if (string.IsNullOrWhiteSpace(config.DisplayName))
        {
            throw new ArgumentException($"Custom ACP backend '{config.Id}' requires a non-empty DisplayName.", nameof(config));
        }

        if (string.IsNullOrWhiteSpace(config.Command))
        {
            throw new ArgumentException($"Custom ACP backend '{config.Id}' requires a non-empty Command.", nameof(config));
        }
    }
}
