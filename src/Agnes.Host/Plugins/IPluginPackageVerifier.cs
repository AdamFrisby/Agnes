namespace Agnes.Host.Plugins;

public sealed record PluginPackageVerificationResult(bool IsValid, string Reason)
{
    public static readonly PluginPackageVerificationResult Valid = new(true, "Signature is valid and trusted.");
}

/// <summary>
/// Verifies a downloaded plugin package's NuGet signature before anything from it runs — see the
/// "Security model" section of .ideas/00-plugin-architecture.md. Behind a seam (rather than calling
/// <c>NuGet.Packaging.Signing</c> directly from <see cref="PluginInstaller"/>) so tests can substitute
/// a fake: building a real, validly-signed test package requires a real trusted certificate chain,
/// which isn't something a unit test can fabricate.
/// </summary>
public interface IPluginPackageVerifier
{
    Task<PluginPackageVerificationResult> VerifyAsync(byte[] nupkgBytes, CancellationToken cancellationToken = default);
}
