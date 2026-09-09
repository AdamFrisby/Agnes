using Microsoft.Extensions.Configuration;

namespace Agnes.Host.Hosting;

/// <summary>
/// The one directory every host-state default hangs off — devices, MCP config, projects, the sandbox
/// registry, saved prompts, the relay key. Configured as <c>Agnes:Home</c> and defaulting to
/// <c>~/.agnes</c>, so a second host (a test host, a throwaway instance, a second daemon on one machine)
/// is pointed somewhere else by setting <em>one</em> key rather than a dozen per-file ones.
/// <para>
/// It exists because the per-file defaults were each independently correct and collectively a hazard: any
/// process that booted <c>Program</c> without overriding <em>every</em> one of them wrote into the
/// operator's live <c>~/.agnes</c>. That is exactly what the integration tests did — the host's real
/// device registry filled up with a hundred "Pixel 9" fixtures, and because host ownership was derived
/// from pairing <em>order</em>, the earliest of those fixtures became the owner and locked the operator's
/// own devices out of their own sessions. Individual per-path settings still win where they are set; this
/// only supplies the default they fall back to.
/// </para>
/// </summary>
public static class AgnesHome
{
    /// <summary>The configuration key holding the host's state directory.</summary>
    public const string ConfigKey = "Agnes:Home";

    /// <summary>
    /// When this environment variable is <c>1</c>, a host that has <em>not</em> been told where its home is
    /// refuses to start instead of quietly falling back to <c>~/.agnes</c>. The test projects set it in a
    /// module initializer, so a future test that boots <c>Program</c> without an isolated home fails loudly
    /// on the first request rather than silently editing the developer's real host state.
    /// </summary>
    public const string RefuseDefaultVariable = "AGNES_REFUSE_DEFAULT_HOME";

    /// <summary>The directory name under the user profile used when nothing is configured.</summary>
    public const string DefaultDirectoryName = ".agnes";

    /// <summary>
    /// The host's state directory: <c>Agnes:Home</c> when set, else <c>~/.agnes</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Nothing was configured and <see cref="RefuseDefaultVariable"/> is set — i.e. a test or tool booted the
    /// host without isolating its home.
    /// </exception>
    public static string Resolve(IConfiguration configuration)
    {
        if (configuration[ConfigKey] is { Length: > 0 } configured && !string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        if (string.Equals(Environment.GetEnvironmentVariable(RefuseDefaultVariable), "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{ConfigKey} is not set and {RefuseDefaultVariable}=1, so this host refuses to fall back to "
                + $"~/{DefaultDirectoryName}. A test or tool that boots the Agnes host must point {ConfigKey} at a "
                + "temporary directory (tests: Agnes.TestKit's IsolatedHostHome) — otherwise it writes device "
                + "tokens, projects and registries into the operator's real host state.");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DefaultDirectoryName);
    }
}
