using Agnes.Sandbox;
using Agnes.Sandbox.Incus;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agnes.Sandbox.Tests;

public class IncusCommandTests
{
    private static readonly IncusOptions Options = new();

    [Fact]
    public void Resolve_bridge_selects_by_profile_then_falls_back()
    {
        var o = Options with
        {
            Bridge = "incusbr0",
            NetworkProfiles = new Dictionary<string, string> { ["locked"] = "cb-locked", ["internet"] = "cb-internet" },
            DefaultNetworkProfile = "locked",
        };

        Assert.Equal("cb-internet", o.ResolveBridge(explicitBridge: null, profile: "internet")); // requested profile
        Assert.Equal("cb-locked", o.ResolveBridge(explicitBridge: null, profile: null));          // default profile
        Assert.Equal("br-explicit", o.ResolveBridge(explicitBridge: "br-explicit", profile: "internet")); // explicit wins
        Assert.Equal("incusbr0", o.ResolveBridge(explicitBridge: null, profile: "unknown"));       // unknown → plain bridge

        // No profiles configured at all → always the plain bridge.
        Assert.Equal("incusbr0", (Options with { Bridge = "incusbr0" }).ResolveBridge(null, "locked"));
    }

    [Fact]
    public void Nic_add_is_open_by_default_and_attaches_acls_when_configured()
    {
        var open = IncusCommandBuilder.BuildNicAdd(Options, "agnes-abc", "incusbr0");
        Assert.Contains("nictype=bridged", open);
        Assert.DoesNotContain(open, a => a.StartsWith("security.acls=", StringComparison.Ordinal)); // open egress

        var restricted = IncusCommandBuilder.BuildNicAdd(
            Options with { NetworkAcls = ["agnes-egress", "allow-registries"] }, "agnes-abc", "incusbr0");
        Assert.Contains("security.acls=agnes-egress,allow-registries", restricted); // operator's egress ACLs attached
    }

    [Fact]
    public void Build_init_produces_a_vm_with_limits_and_ownership()
    {
        var argv = IncusCommandBuilder.BuildInit(Options, "images:ubuntu/24.04/cloud", "agnes-abc", new SandboxResourceLimits());
        Assert.Equal("incus", argv[0]);
        Assert.Contains("--project", argv);
        Assert.Contains("agnes", argv);
        Assert.Contains("init", argv);
        Assert.Contains("--vm", argv);
        Assert.Contains("--no-profiles", argv);
        Assert.Contains(argv, a => a.StartsWith("limits.cpu=", StringComparison.Ordinal));
        Assert.Contains(argv, a => a.StartsWith("limits.memory=", StringComparison.Ordinal) && a.EndsWith('B'));
        Assert.Contains("user.agnes.managed=true", argv);
    }

    [Fact]
    public void Cloud_init_always_creates_the_work_directory()
    {
        // /work must exist even without a bind-mount, or `incus exec --cwd /work` fails (127) and
        // the agent never starts (breaking the first prompt's stdin pipe).
        var cloudInit = IncusGuest.CloudInit(Options);
        Assert.Contains("mkdir, -p, /work", cloudInit);
    }

    [Fact]
    public void Build_publish_and_image_verbs()
    {
        Assert.Equal(["incus", "--project", "agnes", "publish", "agnes-abc", "--alias", "agnes-baseline", "--reuse"],
            IncusCommandBuilder.BuildPublish(Options, "agnes-abc", "agnes-baseline"));
        Assert.Equal(["incus", "--project", "agnes", "image", "info", "agnes-baseline"],
            IncusCommandBuilder.BuildImageInfo(Options, "agnes-baseline"));
        Assert.Equal(["incus", "--project", "agnes", "image", "delete", "agnes-baseline"],
            IncusCommandBuilder.BuildImageDelete(Options, "agnes-baseline"));
    }

    [Fact]
    public void Build_file_push_file_copies_a_host_binary_into_the_guest()
    {
        var argv = IncusCommandBuilder.BuildFilePushFile(Options, "agnes-abc", "/usr/local/bin/claude", "/usr/local/bin/claude", "0755");
        Assert.Equal(["incus", "--project", "agnes", "file", "push", "/usr/local/bin/claude", "agnes-abc/usr/local/bin/claude", "--mode=0755", "--create-dirs"], argv);
    }

    [Fact]
    public void Build_exec_wraps_with_cwd_and_argv_separator()
    {
        var argv = IncusCommandBuilder.BuildExec(Options, "agnes-abc", ["claude", "--print"], "/work", asUser: false);
        Assert.Equal(["incus", "--project", "agnes", "exec", "agnes-abc", "--cwd", "/work", "--", "claude", "--print"], argv);
    }

    [Fact]
    public void Build_file_push_targets_the_instance_path_with_mode()
    {
        var argv = IncusCommandBuilder.BuildFilePush(Options, "agnes-abc", "/run/agnes/agent-env", "0600", 0, 0);
        Assert.Contains("file", argv);
        Assert.Contains("push", argv);
        Assert.Contains("-", argv);
        Assert.Contains("agnes-abc/run/agnes/agent-env", argv);
        Assert.Contains("--mode=0600", argv);
    }

    [Fact]
    public void Pause_and_delete_build_the_right_verbs()
    {
        Assert.Contains("pause", IncusCommandBuilder.BuildPause(Options, "agnes-abc"));
        var del = IncusCommandBuilder.BuildDelete(Options, "agnes-abc");
        Assert.Contains("delete", del);
        Assert.Contains("--force", del);
    }

    [Fact]
    public void Snapshot_and_cow_copy_build_the_fork_clone_verbs()
    {
        Assert.Equal(["incus", "--project", "agnes", "snapshot", "create", "agnes-src", "fork1234"],
            IncusCommandBuilder.BuildSnapshotCreate(Options, "agnes-src", "fork1234"));
        Assert.Equal(["incus", "--project", "agnes", "snapshot", "delete", "agnes-src", "fork1234"],
            IncusCommandBuilder.BuildSnapshotDelete(Options, "agnes-src", "fork1234"));

        var copy = IncusCommandBuilder.BuildCopyFromSnapshot(Options, "agnes-src", "fork1234", "agnes-dst");
        Assert.Equal(["incus", "--project", "agnes", "copy", "agnes-src/fork1234", "agnes-dst", "--storage", Options.StoragePoolName], copy);
    }

    [Fact]
    public void Device_remove_targets_the_named_device()
        => Assert.Equal(["incus", "--project", "agnes", "config", "device", "remove", "agnes-dst", "agnes-work"],
            IncusCommandBuilder.BuildDeviceRemove(Options, "agnes-dst", "agnes-work"));

    [Theory]
    [InlineData("a; rm -rf /")]
    [InlineData("-x")]
    [InlineData("a b")]
    [InlineData("")]
    public void Instance_name_validation_rejects_injection(string bad)
        => Assert.Throws<ArgumentException>(() => IncusCommandBuilder.BuildStart(Options, bad));

    [Theory]
    [InlineData("bad snap")]
    [InlineData("snap/slash")]
    [InlineData("")]
    public void Snapshot_name_validation_rejects_injection(string bad)
        => Assert.Throws<ArgumentException>(() => IncusCommandBuilder.BuildSnapshotCreate(Options, "agnes-src", bad));

    [Theory]
    [InlineData("relative/path")]
    [InlineData("/has/../traversal")]
    [InlineData("//double")]
    [InlineData("/trailing/")]
    public void Guest_path_validation_rejects_non_canonical(string bad)
        => Assert.Throws<ArgumentException>(() => IncusCommandBuilder.BuildExec(Options, "agnes-abc", ["x"], bad, asUser: false));

    [Fact]
    public void Wrap_command_runs_the_agent_via_the_run_wrapper()
    {
        var sandbox = new IncusSandbox("agnes-abc", Options, new IncusCliRunner(NullLogger.Instance), NullLogger.Instance);
        var (command, args) = sandbox.WrapCommand("claude", ["--output-format", "stream-json"], "/work");

        Assert.Equal("incus", command);
        Assert.Equal(["--project", "agnes", "exec", "agnes-abc", "--cwd", "/work", "--",
            "/usr/local/bin/agnes-run", "claude", "--output-format", "stream-json"], args);
    }
}
