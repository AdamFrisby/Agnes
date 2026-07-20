using Agnes.Sandbox;
using Agnes.Sandbox.Incus;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Sandbox.Tests;

public class ImageBakeTests
{
    /// <summary>Records every incus invocation and returns success (0) so orchestration proceeds.</summary>
    private sealed class RecordingRunner : IIncusCliRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
            IReadOnlyList<string> argv, string? stdin = null,
            Action<string>? stdoutChunk = null, Action<string>? stderrChunk = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(argv);
            return Task.FromResult((0, string.Empty, string.Empty));
        }

        public Task RunCheckedAsync(string what, IReadOnlyList<string> argv, string? stdin = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(argv);
            return Task.CompletedTask;
        }

        // Index of the first call whose argv contains the given verb (after `--`, exec args count).
        public int IndexOf(params string[] contains)
            => Calls.FindIndex(c => contains.All(c.Contains));
    }

    private static IncusSandboxProvider Provider(RecordingRunner runner)
        => new(new IncusOptions(), NullLoggerFactory.Instance, runner);

    [Fact]
    public async Task Bake_runs_provision_install_copy_clean_publish_in_order()
    {
        var runner = new RecordingRunner();
        var manifest = new SandboxImageManifest
        {
            Alias = "agnes-baseline",
            AptPackages = ["git"],
            Node = true,
            NpmGlobals = ["some-mcp"],
            Agents = [new SandboxImageAgent("claude-code-native", "copy:definitely-not-a-real-binary-xyz")],
        };

        await Provider(runner).BuildImageAsync(manifest);

        // Provision → apt → npm → cloud-init clean → stop → publish → delete, in that order.
        var init = runner.IndexOf("init");
        var aptInstall = runner.IndexOf("apt-get", "install");
        var npm = runner.IndexOf("npm", "-g"); // "npm" also appears as an apt package; -g is unique to the npm step
        var clean = runner.IndexOf("cloud-init", "clean");
        var publish = runner.IndexOf("publish");
        var delete = runner.IndexOf("delete");

        Assert.True(init >= 0 && aptInstall > init && npm > aptInstall && clean > npm && publish > clean);
        Assert.True(delete > publish); // the throwaway bake VM is always removed

        // node was requested → nodejs/npm are in the apt install; the npm MCP package is installed.
        Assert.Contains(runner.Calls, c => c.Contains("apt-get") && c.Contains("nodejs") && c.Contains("git"));
        Assert.Contains(runner.Calls, c => c.Contains("npm") && c.Contains("some-mcp"));
        // published under the manifest alias.
        Assert.Contains(runner.Calls, c => c.Contains("publish") && c.Contains("agnes-baseline"));
    }

    [Fact]
    public async Task Missing_host_binary_is_skipped_not_fatal()
    {
        var runner = new RecordingRunner();
        var manifest = new SandboxImageManifest
        {
            AptPackages = [],
            Node = false,
            NpmGlobals = [],
            Agents = [new SandboxImageAgent("codex", "copy:definitely-not-a-real-binary-xyz")],
        };

        // The binary isn't on PATH, so no file-push happens — but the bake still completes and publishes.
        await Provider(runner).BuildImageAsync(manifest);

        Assert.DoesNotContain(runner.Calls, c => c.Contains("push") && c.Any(a => a.Contains("definitely-not-a-real-binary-xyz")));
        Assert.Contains(runner.Calls, c => c.Contains("publish"));
    }

    [Fact]
    public void Manifest_defaults_bake_claude_and_codex_with_node_and_core_tools()
    {
        var m = new SandboxImageManifest();
        Assert.True(m.Node);
        Assert.Contains("git", m.AptPackages);
        Assert.Contains("build-essential", m.AptPackages);
        Assert.Contains(m.Agents, a => a.AdapterId == "claude-code-native" && a.Source == "copy:claude");
        Assert.Contains(m.Agents, a => a.AdapterId == "codex" && a.Source == "copy:codex");
    }

    [Fact]
    public void Fingerprint_changes_when_the_manifest_changes()
    {
        var a = new SandboxImageManifest();
        var b = a with { NpmGlobals = ["new-server"] };
        Assert.NotEqual(a.Fingerprint(), b.Fingerprint());
        Assert.Equal(a.Fingerprint(), (a with { }).Fingerprint());
    }
}
