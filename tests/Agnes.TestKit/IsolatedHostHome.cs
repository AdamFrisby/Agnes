namespace Agnes.TestKit;

/// <summary>
/// A throwaway <c>Agnes:Home</c> for a test that boots the real host.
/// <para>
/// This exists because of a real incident, not a hypothetical one. Every host-state default hung off
/// <c>~/.agnes</c>, and the integration tests booted <c>Program</c> through
/// <c>WebApplicationFactory&lt;Program&gt;</c> without redirecting them — so each test run appended real
/// device records to the developer's real host. The operator's registry ended up holding a hundred-odd
/// fixtures ("Pixel 9" from the approval tests, <c>key:my-laptop</c> from the keypair ones), and because
/// host ownership was then derived from pairing <em>order</em>, the oldest of those fixtures owned the host
/// and the human's own devices could not see a single session.
/// </para>
/// <para>
/// So: every in-process host in the test tree takes its configuration from here, and the host itself refuses
/// to fall back to <c>~/.agnes</c> while <c>AGNES_REFUSE_DEFAULT_HOME=1</c> is set — which
/// <see cref="RefuseDefaultHome"/> does once per test assembly, from a module initializer. A future test that
/// forgets fails on its first request with a message saying exactly this, instead of quietly editing
/// somebody's machine.
/// </para>
/// </summary>
public sealed class IsolatedHostHome : IDisposable
{
    /// <summary>The configuration key the host reads its state directory from.</summary>
    public const string HomeKey = "Agnes:Home";

    /// <summary>The environment variable that turns the default <c>~/.agnes</c> into a startup failure.</summary>
    public const string RefuseDefaultVariable = "AGNES_REFUSE_DEFAULT_HOME";

    public IsolatedHostHome()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agnes-test-home-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>The temporary directory this host's state lives in. Deleted on <see cref="Dispose"/>.</summary>
    public string Path { get; }

    /// <summary>
    /// The configuration overrides to merge into the host under test. One key, because every host-state path
    /// defaults off it — a test that needed to remember a dozen keys would eventually forget one.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?>
    {
        [HomeKey] = Path,
    };

    /// <summary>
    /// Makes this process's hosts refuse the <c>~/.agnes</c> fallback. Call it from a
    /// <c>[ModuleInitializer]</c> in any test assembly that can boot a host, so the refusal is in place
    /// before the first test — including a test added later that nobody remembered to isolate.
    /// </summary>
    public static void RefuseDefaultHome()
        => Environment.SetEnvironmentVariable(RefuseDefaultVariable, "1");

    /// <summary>The files this host actually wrote — handy for asserting that a run touched nothing else.</summary>
    public IReadOnlyList<string> Files()
        => Directory.Exists(Path)
            ? Directory.GetFiles(Path, "*", SearchOption.AllDirectories)
            : [];

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is noise, never a test failure.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
