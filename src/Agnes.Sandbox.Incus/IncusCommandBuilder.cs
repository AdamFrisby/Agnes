using System.Globalization;

namespace Agnes.Sandbox.Incus;

/// <summary>
/// Builds validated <c>incus</c> argv (never a shell string). Ported from CodeyBox's
/// IncusCommandBuilder/IncusInputValidation — every identity/path is validated so a malicious
/// working directory or instance name can't inject arguments.
/// </summary>
internal static class IncusCommandBuilder
{
    internal static List<string> Prefix(IncusOptions o, params string[] command)
    {
        IncusInputValidation.ValidateOptions(o);
        var result = new List<string>(command.Length + 3) { o.BinaryPath, "--project", o.ProjectName };
        result.AddRange(command);
        return result;
    }

    internal static IReadOnlyList<string> BuildInit(IncusOptions o, string image, string name, SandboxResourceLimits limits)
    {
        IncusInputValidation.ValidateOpaque(image, nameof(image), 4096);
        IncusInputValidation.ValidateInstanceName(name);
        if (limits.CpuCount <= 0 || limits.MemoryBytes <= 0 || limits.DiskBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "CPU/memory/disk limits must be positive.");
        }

        var r = Prefix(o, "init");
        r.Add(image);
        r.Add(name);
        r.Add("--vm");
        r.Add("--storage");
        r.Add(o.StoragePoolName);
        r.Add("--no-profiles");
        r.Add("--config");
        r.Add($"limits.cpu={limits.CpuCount.ToString(CultureInfo.InvariantCulture)}");
        r.Add("--config");
        r.Add($"limits.memory={limits.MemoryBytes.ToString(CultureInfo.InvariantCulture)}B");
        r.Add("--device");
        r.Add($"root,size={limits.DiskBytes.ToString(CultureInfo.InvariantCulture)}B");
        r.Add("--config");
        r.Add("user.agnes.managed=true");
        return r;
    }

    internal static IReadOnlyList<string> BuildNicAdd(IncusOptions o, string instance, string bridge)
    {
        IncusInputValidation.ValidateInstanceName(instance);
        IncusInputValidation.ValidateBridge(bridge);
        var r = Prefix(o, "config", "device", "add", instance, "agnes-net", "nic");
        r.Add("nictype=bridged");
        r.Add($"parent={bridge}");
        r.Add("name=eth0");
        return r;
    }

    internal static IReadOnlyList<string> BuildDiskAdd(IncusOptions o, string instance, string device, string hostSource, string guestPath, bool readOnly)
    {
        IncusInputValidation.ValidateInstanceName(instance);
        IncusInputValidation.ValidateIdentifier(device, nameof(device), allowDotUnderscore: true);
        IncusInputValidation.ValidateAbsoluteHostPath(hostSource);
        IncusInputValidation.ValidateAbsoluteGuestPath(guestPath);
        var r = Prefix(o, "config", "device", "add", instance, device, "disk");
        r.Add($"source={hostSource}");
        r.Add($"path={guestPath}");
        r.Add("io.bus=virtiofs");
        if (readOnly)
        {
            r.Add("readonly=true");
        }

        return r;
    }

    /// <summary>Sets a config key with the value read from stdin (used for cloud-init user-data).</summary>
    internal static IReadOnlyList<string> BuildConfigSetStdin(IncusOptions o, string instance, string key)
    {
        IncusInputValidation.ValidateInstanceName(instance);
        IncusInputValidation.ValidateIdentifier(key, nameof(key), allowDotUnderscore: true);
        return Prefix(o, "config", "set", instance, $"{key}=-");
    }

    internal static IReadOnlyList<string> BuildStart(IncusOptions o, string instance)
    {
        IncusInputValidation.ValidateInstanceName(instance);
        return Prefix(o, "start", instance);
    }

    internal static IReadOnlyList<string> BuildStop(IncusOptions o, string instance, int timeoutSeconds, bool stateful)
    {
        IncusInputValidation.ValidateInstanceName(instance);
        var r = Prefix(o, "stop", instance, "--timeout", timeoutSeconds.ToString(CultureInfo.InvariantCulture));
        if (stateful)
        {
            r.Add("--stateful");
        }

        return r;
    }

    internal static IReadOnlyList<string> BuildPause(IncusOptions o, string instance)
    {
        IncusInputValidation.ValidateInstanceName(instance);
        return Prefix(o, "pause", instance);
    }

    internal static IReadOnlyList<string> BuildDelete(IncusOptions o, string instance)
    {
        IncusInputValidation.ValidateInstanceName(instance);
        return Prefix(o, "delete", instance, "--force");
    }

    internal static IReadOnlyList<string> BuildFilePush(IncusOptions o, string instance, string guestPath, string mode, int uid, int gid)
    {
        IncusInputValidation.ValidateInstanceName(instance);
        IncusInputValidation.ValidateAbsoluteGuestPath(guestPath);
        return Prefix(o, "file", "push", "-", $"{instance}{guestPath}",
            $"--mode={mode}",
            $"--uid={uid.ToString(CultureInfo.InvariantCulture)}",
            $"--gid={gid.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>Exec a command inside the instance as the given uid/gid.</summary>
    internal static IReadOnlyList<string> BuildExec(IncusOptions o, string instance, IReadOnlyList<string> command, string? workingDirectory, bool asUser)
    {
        ArgumentNullException.ThrowIfNull(command);
        IncusInputValidation.ValidateInstanceName(instance);
        if (command.Count == 0 || string.IsNullOrEmpty(command[0]))
        {
            throw new ArgumentException("An exec command must not be empty.", nameof(command));
        }

        for (var i = 0; i < command.Count; i++)
        {
            if (command[i].Contains('\0'))
            {
                throw new ArgumentException($"Command argument {i} contains NUL.", nameof(command));
            }
        }

        var r = Prefix(o, "exec", instance);
        if (workingDirectory is not null)
        {
            IncusInputValidation.ValidateAbsoluteGuestPath(workingDirectory);
            r.Add("--cwd");
            r.Add(workingDirectory);
        }

        if (asUser)
        {
            r.Add("--user");
            r.Add(o.GuestUserId.ToString(CultureInfo.InvariantCulture));
            r.Add("--group");
            r.Add(o.GuestGroupId.ToString(CultureInfo.InvariantCulture));
        }

        r.Add("--");
        r.AddRange(command);
        return r;
    }

    internal static IReadOnlyList<string> BuildListJson(IncusOptions o)
        => Prefix(o, "list", "--format=json", "user.agnes.managed=true");
}
