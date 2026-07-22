using System.Reflection;
using System.Text.Json;
using Agnes.Abstractions;
using NuGet.Packaging;
using NuGet.Versioning;

namespace Agnes.Host.Plugins;

/// <summary>Thrown when a plugin package's manifest is missing, malformed, or declares an
/// incompatible <c>agnesApiVersion</c> — refused with a clear message rather than a type-load crash.</summary>
public sealed class PluginManifestException(string message) : Exception(message);

/// <summary>
/// Reads and validates a plugin package's <c>agnes-plugin.json</c> manifest from its .nupkg.
/// </summary>
public static class PluginManifestReader
{
    private const string ManifestEntryName = "agnes-plugin.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Reads <c>agnes-plugin.json</c> from the package root. Throws <see cref="PluginManifestException"/>
    /// if it's missing, malformed, or missing required fields.</summary>
    public static PluginManifest Read(PackageArchiveReader reader)
    {
        var entry = reader.GetFiles().FirstOrDefault(f =>
            string.Equals(Path.GetFileName(f), ManifestEntryName, StringComparison.OrdinalIgnoreCase) &&
            !f.Contains('/', StringComparison.Ordinal));

        if (entry is null)
        {
            throw new PluginManifestException($"Package is missing a root-level {ManifestEntryName}.");
        }

        PluginManifest? manifest;
        try
        {
            using var stream = reader.GetStream(entry);
            manifest = JsonSerializer.Deserialize<PluginManifest>(stream, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new PluginManifestException($"{ManifestEntryName} is not valid JSON: {ex.Message}");
        }

        if (manifest is null)
        {
            throw new PluginManifestException($"{ManifestEntryName} deserialized to null.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            throw new PluginManifestException($"{ManifestEntryName} is missing required field 'id'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.AgnesApiVersion))
        {
            throw new PluginManifestException($"{ManifestEntryName} is missing required field 'agnesApiVersion'.");
        }

        if (!VersionRange.TryParse(manifest.AgnesApiVersion, out _))
        {
            throw new PluginManifestException($"{ManifestEntryName}'s 'agnesApiVersion' ('{manifest.AgnesApiVersion}') is not a valid version range.");
        }

        return manifest;
    }

    /// <summary>Whether the manifest's declared <c>agnesApiVersion</c> range accepts the host's actual
    /// <c>Agnes.Abstractions</c> version — checked by range, per the doc's "prototype the simpler
    /// declared-range approach first" open question.</summary>
    public static bool IsCompatibleWithHost(PluginManifest manifest)
    {
        var range = VersionRange.Parse(manifest.AgnesApiVersion);
        var hostVersion = HostAbstractionsVersion();
        return range.Satisfies(hostVersion);
    }

    private static NuGetVersion HostAbstractionsVersion()
    {
        var asm = typeof(IAgentAdapter).Assembly;
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informational is not null && NuGetVersion.TryParse(informational.Split('+')[0], out var parsed))
        {
            return parsed;
        }

        var version = asm.GetName().Version ?? new Version(0, 0, 0, 0);
        return new NuGetVersion(version.Major, version.Minor, Math.Max(version.Build, 0));
    }
}
