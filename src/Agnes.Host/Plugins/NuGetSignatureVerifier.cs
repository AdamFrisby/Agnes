using Microsoft.Extensions.Logging;
using NuGet.Packaging;
using NuGet.Packaging.Signing;

namespace Agnes.Host.Plugins;

/// <summary>
/// The real <see cref="IPluginPackageVerifier"/>: uses <c>NuGet.Packaging.Signing</c> — the same
/// verification pipeline behind <c>dotnet nuget verify</c> — to check a package's signature.
/// Package signature verification is mandatory by default (AC7): an unsigned package, or one with an
/// invalid/untrusted signature, is refused. <see cref="AllowUnsigned"/> is the explicit, off-by-default
/// override for local plugin development, and every use of it is logged loudly.
/// </summary>
public sealed class NuGetSignatureVerifier(bool allowUnsigned, ILogger<NuGetSignatureVerifier>? logger = null) : IPluginPackageVerifier
{
    private static readonly IReadOnlyList<ISignatureVerificationProvider> TrustProviders =
    [
        new IntegrityVerificationProvider(),
        new SignatureTrustAndValidityVerificationProvider(),
    ];

    private readonly ILogger<NuGetSignatureVerifier> _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<NuGetSignatureVerifier>.Instance;

    public bool AllowUnsigned { get; } = allowUnsigned;

    public async Task<PluginPackageVerificationResult> VerifyAsync(byte[] nupkgBytes, CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream(nupkgBytes);
        using var reader = new PackageArchiveReader(stream, leaveStreamOpen: true);

        var isSigned = await reader.IsSignedAsync(cancellationToken).ConfigureAwait(false);
        if (!isSigned)
        {
            if (AllowUnsigned)
            {
                _logger.LogWarning(
                    "SECURITY: loading an UNSIGNED plugin package because Agnes:Plugins:AllowUnsignedPackages is enabled. " +
                    "This override exists for local plugin development only — never enable it against an untrusted source.");
                return new PluginPackageVerificationResult(true, "Unsigned, but Agnes:Plugins:AllowUnsignedPackages is enabled.");
            }

            return new PluginPackageVerificationResult(false, "Package is not signed. Set Agnes:Plugins:AllowUnsignedPackages to override for local development only.");
        }

        var verifier = new PackageSignatureVerifier(TrustProviders);
        var settings = SignedPackageVerifierSettings.GetVerifyCommandDefaultPolicy(NuGet.Common.EnvironmentVariableWrapper.Instance);
        var result = await verifier.VerifySignaturesAsync(reader, settings, cancellationToken).ConfigureAwait(false);

        if (result.IsValid)
        {
            return PluginPackageVerificationResult.Valid;
        }

        var issues = result.Results
            .SelectMany(r => r.GetErrorIssues())
            .Select(i => i.Message)
            .Distinct();
        var reason = string.Join("; ", issues);
        return new PluginPackageVerificationResult(false, string.IsNullOrWhiteSpace(reason) ? "Signature is invalid or untrusted." : reason);
    }
}
