using System.Security.Cryptography;
using NuGet.Versioning;

namespace Agnes.Host.Plugins;

/// <summary>Configuration for the production plugin provenance allowlist.</summary>
public sealed class PluginTrustOptions
{
    public List<string> Sources { get; set; } = [];
    public List<PluginPackageApprovalOptions> ApprovedPackages { get; set; } = [];
}

/// <summary>An exact, operator-approved package artifact.</summary>
public sealed class PluginPackageApprovalOptions
{
    public string Source { get; set; } = string.Empty;
    public string PackageId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;

    /// <summary>Base64-encoded SHA-512 of the complete .nupkg payload.</summary>
    public string Sha512 { get; set; } = string.Empty;
}

internal sealed record PluginPackageApproval(string Source, string PackageId, NuGetVersion Version, byte[] Sha512);

/// <summary>
/// Separates Development's signed-package workflow from Production's exact artifact allowlist.
/// Production never chooses an unpinned latest version or an ambient package source.
/// </summary>
public sealed class PluginTrustPolicy
{
    private readonly IReadOnlyList<PluginPackageApproval> _approvals;

    private PluginTrustPolicy(bool requiresExactApproval, IReadOnlyList<PluginPackageApproval> approvals)
    {
        RequiresExactApproval = requiresExactApproval;
        _approvals = approvals;
    }

    public bool RequiresExactApproval { get; }

    public static PluginTrustPolicy Development { get; } = new(false, []);

    public static PluginTrustPolicy CreateProduction(PluginTrustOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configuredSources = options.Sources.Select(CanonicalizeSource).Distinct(StringComparer.Ordinal).ToArray();
        var approvals = options.ApprovedPackages.Select(ParseApproval).ToArray();
        var duplicates = approvals.GroupBy(a => (a.Source, a.PackageId, Version: a.Version.ToNormalizedString()),
            new ApprovalIdentityComparer()).FirstOrDefault(group => group.Count() > 1);
        if (duplicates is not null)
        {
            throw new InvalidOperationException(
                $"Agnes:Plugins:ApprovedPackages contains duplicate approval '{duplicates.Key.PackageId}' {duplicates.Key.Version} from {duplicates.Key.Source}.");
        }

        // The management protocol asks for package id + version, not an arbitrary feed selector.
        // Refuse ambiguous approvals rather than allowing a feed-order change to choose code.
        var ambiguousPackage = approvals.GroupBy(
                approval => (approval.PackageId, Version: approval.Version.ToNormalizedString()),
                new PackageVersionComparer())
            .FirstOrDefault(group => group.Count() > 1);
        if (ambiguousPackage is not null)
        {
            throw new InvalidOperationException(
                $"Production package '{ambiguousPackage.Key.PackageId}' {ambiguousPackage.Key.Version} may be approved from only one source.");
        }

        foreach (var configuredSource in configuredSources)
        {
            if (!approvals.Any(approval => string.Equals(approval.Source, configuredSource, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Production plugin source '{configuredSource}' has no exact package approval.");
            }
        }

        foreach (var approval in approvals)
        {
            if (!configuredSources.Contains(approval.Source, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Production package approval '{approval.PackageId}' {approval.Version.ToNormalizedString()} names a source that is not configured.");
            }
        }

        return new PluginTrustPolicy(true, approvals);
    }

    internal PluginPackageApproval? ResolveInstall(string packageId, string? version)
    {
        if (!RequiresExactApproval)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new PluginInstallException("Production plugin installation requires an exact approved package version.");
        }

        var parsedVersion = ParseVersion(version, "requested plugin version");
        return _approvals.SingleOrDefault(approval =>
                   string.Equals(approval.PackageId, packageId, StringComparison.OrdinalIgnoreCase) &&
                   approval.Version == parsedVersion)
               ?? throw new PluginInstallException(
                   $"Package '{packageId}' version '{parsedVersion.ToNormalizedString()}' is not approved for this production host.");
    }

    internal PluginPackageApproval? ResolveUpdate(PluginRecord record)
    {
        if (!RequiresExactApproval)
        {
            return null;
        }

        ValidateRecordApproval(record);
        var currentVersion = ParseVersion(record.Version, "installed plugin version");
        return _approvals
            .Where(approval => string.Equals(approval.PackageId, record.PackageId, StringComparison.OrdinalIgnoreCase))
            .Where(approval => approval.Version > currentVersion)
            .OrderByDescending(approval => approval.Version)
            .FirstOrDefault();
    }

    internal bool IsApprovedPackage(string packageId)
        => !RequiresExactApproval || _approvals.Any(approval =>
            string.Equals(approval.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

    internal IReadOnlyList<string> ApprovedVersions(string packageId)
        => _approvals
            .Where(approval => string.Equals(approval.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(approval => approval.Version)
            .Select(approval => approval.Version.ToNormalizedString())
            .ToArray();

    /// <summary>Verifies the package source, identity, version and payload digest before extraction.</summary>
    internal string VerifyDownloaded(NuGetPluginPackage package, PluginPackageApproval? expectedApproval)
    {
        var digest = Convert.ToBase64String(SHA512.HashData(package.Content));
        if (!RequiresExactApproval)
        {
            return digest;
        }

        if (expectedApproval is null)
        {
            throw new InvalidOperationException("Production package verification requires an approval.");
        }

        var packageVersion = ParseVersion(package.Version, "downloaded plugin version");
        var sourceMatches = string.Equals(CanonicalizeSource(package.Source), expectedApproval.Source, StringComparison.Ordinal);
        var identityMatches = string.Equals(package.PackageId, expectedApproval.PackageId, StringComparison.OrdinalIgnoreCase);
        var versionMatches = packageVersion == expectedApproval.Version;
        var digestMatches = CryptographicOperations.FixedTimeEquals(
            Convert.FromBase64String(digest), expectedApproval.Sha512);
        if (!sourceMatches || !identityMatches || !versionMatches || !digestMatches)
        {
            throw new PluginInstallException(
                $"Downloaded package '{package.PackageId}' {package.Version} does not match its approved production provenance.");
        }

        return digest;
    }

    internal void ValidateRecordApproval(PluginRecord record)
    {
        if (!RequiresExactApproval)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(record.Source) || string.IsNullOrWhiteSpace(record.Sha512))
        {
            throw new PluginInstallException(
                $"Enabled plugin '{record.PluginId}' has legacy state without source and SHA-512 provenance. Reinstall it from an approved package.");
        }

        var version = ParseVersion(record.Version, "installed plugin version");
        var source = CanonicalizeSource(record.Source);
        var digest = ParseSha512(record.Sha512, $"plugin '{record.PluginId}' state");
        var approval = _approvals.SingleOrDefault(candidate =>
            string.Equals(candidate.Source, source, StringComparison.Ordinal) &&
            string.Equals(candidate.PackageId, record.PackageId, StringComparison.OrdinalIgnoreCase) &&
            candidate.Version == version);
        if (approval is null || !CryptographicOperations.FixedTimeEquals(digest, approval.Sha512))
        {
            throw new PluginInstallException(
                $"Enabled plugin '{record.PluginId}' is not approved by this production host configuration.");
        }
    }

    public static string CanonicalizeSource(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Plugin package sources must be absolute HTTPS URLs.");
        }

        return uri.AbsoluteUri;
    }

    private static PluginPackageApproval ParseApproval(PluginPackageApprovalOptions approval)
    {
        if (string.IsNullOrWhiteSpace(approval.PackageId))
        {
            throw new InvalidOperationException("Every production plugin approval requires a packageId.");
        }

        return new PluginPackageApproval(
            CanonicalizeSource(approval.Source),
            approval.PackageId.Trim(),
            ParseVersion(approval.Version, $"approved version for '{approval.PackageId}'"),
            ParseSha512(approval.Sha512, $"approved SHA-512 for '{approval.PackageId}'"));
    }

    private static NuGetVersion ParseVersion(string version, string description)
    {
        if (!NuGetVersion.TryParse(version, out var parsed))
        {
            throw new InvalidOperationException($"Invalid {description} '{version}'.");
        }

        return parsed;
    }

    private static byte[] ParseSha512(string digest, string description)
    {
        try
        {
            var bytes = Convert.FromBase64String(digest);
            if (bytes.Length == 64)
            {
                return bytes;
            }
        }
        catch (FormatException)
        {
            // The contextual error below is more useful to operators.
        }

        throw new InvalidOperationException($"{description} must be a base64-encoded SHA-512 digest.");
    }

    private sealed class ApprovalIdentityComparer : IEqualityComparer<(string Source, string PackageId, string Version)>
    {
        public bool Equals((string Source, string PackageId, string Version) x, (string Source, string PackageId, string Version) y)
            => string.Equals(x.Source, y.Source, StringComparison.Ordinal) &&
               string.Equals(x.PackageId, y.PackageId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(x.Version, y.Version, StringComparison.Ordinal);

        public int GetHashCode((string Source, string PackageId, string Version) value)
            => HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(value.Source),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.PackageId),
                StringComparer.Ordinal.GetHashCode(value.Version));
    }

    private sealed class PackageVersionComparer : IEqualityComparer<(string PackageId, string Version)>
    {
        public bool Equals((string PackageId, string Version) x, (string PackageId, string Version) y)
            => string.Equals(x.PackageId, y.PackageId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(x.Version, y.Version, StringComparison.Ordinal);

        public int GetHashCode((string PackageId, string Version) value)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.PackageId),
                StringComparer.Ordinal.GetHashCode(value.Version));
    }
}
