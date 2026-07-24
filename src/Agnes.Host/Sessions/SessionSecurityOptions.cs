using System.Text.Json.Serialization;

namespace Agnes.Host.Sessions;

/// <summary>
/// Host-level guardrails on <b>where</b> and <b>how</b> sessions may run, bound from <c>Agnes:Security:*</c>.
/// Both knobs are opt-in and default to today's behaviour: an empty <see cref="AllowedSessionRoots"/> means a
/// session may run in any directory, and <see cref="RequireSandbox"/> off means a session may run directly on
/// the host. They are defence-in-depth for a shared / multi-tenant host — the sandbox is still the primary
/// isolation boundary; these just stop a client from side-stepping it or reaching outside its lane.
/// </summary>
public sealed record SessionSecurityOptions
{
    /// <summary>
    /// If non-empty, every session's working directory must canonicalise to a location inside one of these
    /// roots (the root itself, or a descendant of it). Empty = unrestricted (the default). Relative entries
    /// are resolved against the host process's current directory when the check runs.
    /// </summary>
    public IReadOnlyList<string> AllowedSessionRoots { get; init; } = [];

    /// <summary>
    /// When true the host refuses to launch any session that would run outside a sandbox: a request with
    /// sandboxing turned off — or on a host with no sandbox provider configured at all — fails loud instead
    /// of silently running the agent directly on the host filesystem.
    /// </summary>
    public bool RequireSandbox { get; init; }

    /// <summary>
    /// When true the host refuses any autonomous / <c>--dangerously-skip-permissions</c> session: every tool
    /// call must be prompted. The strongest of the autonomy guardrails — overrides
    /// <see cref="AllowUnsandboxedSkipPermissions"/>.
    /// </summary>
    public bool RequirePermissionPrompts { get; init; }

    /// <summary>
    /// Whether an autonomous / skip-permissions session may run <b>outside</b> a sandbox. Defaults to
    /// <c>false</c>: dangerous autonomous mode is confined to a sandbox unless an operator explicitly opts in.
    /// Ignored (moot) when <see cref="RequirePermissionPrompts"/> forbids autonomy outright.
    /// </summary>
    public bool AllowUnsandboxedSkipPermissions { get; init; }

    /// <summary>
    /// If non-empty, an allowlist (by MCP server <em>name</em>, case-insensitive) of the only servers permitted
    /// to run with <c>RunAt=Host</c> — i.e. execute a command on the host, outside any sandbox. A host-run
    /// server whose name isn't listed is silently dropped from a session's MCP set (a notice is surfaced), on
    /// both the unsandboxed-direct and the sandboxed host-forward paths. Empty = unrestricted (the default):
    /// any configured host server runs. Sandbox-run servers are unaffected.
    /// </summary>
    public IReadOnlyList<string> AllowedHostMcpServers { get; init; } = [];

    /// <summary>
    /// How sessions are scoped to callers on a shared host. <see cref="SessionIsolation.Shared"/> (default) is
    /// today's behaviour (the host owner sees all; others need an explicit share). <see cref="SessionIsolation.PerUser"/>
    /// additionally lets a caller always reach the sessions they own (across their own devices).
    /// <see cref="SessionIsolation.PerGroup"/> further lets members of a session's group reach it (via an
    /// <c>IGroupProvider</c> — the shipped one is GitHub repo write-access). These are additive grants layered on
    /// top of explicit shares; the host owner remains an admin super-user in every mode.
    /// </summary>
    public SessionIsolation SessionIsolation { get; init; } = SessionIsolation.Shared;

    /// <summary>
    /// When true, host-wide configuration mutations — the sandbox image manifest, project config, and the MCP
    /// server registry — are restricted to the host owner rather than any paired device. Defaults to false
    /// (today's behaviour: any valid device may edit them). Recommended on a shared host.
    /// </summary>
    public bool RestrictConfigToOwner { get; init; }

    /// <summary>True when <see cref="AllowedSessionRoots"/> actually constrains anything.</summary>
    [JsonIgnore]
    public bool RestrictsDirectories => AllowedSessionRoots.Count > 0;

    /// <summary>True when <see cref="AllowedHostMcpServers"/> actually constrains host MCP servers.</summary>
    [JsonIgnore]
    public bool RestrictsHostMcpServers => AllowedHostMcpServers.Count > 0;

    /// <summary>Whether a host-run MCP server with this name is permitted (always true when no allowlist is set).</summary>
    public bool IsHostMcpServerAllowed(string serverName)
        => AllowedHostMcpServers.Count == 0
        || AllowedHostMcpServers.Any(a => string.Equals(a, serverName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>How sessions are scoped to callers on a shared host — see <see cref="SessionSecurityOptions.SessionIsolation"/>.</summary>
public enum SessionIsolation
{
    /// <summary>Today's behaviour: host owner sees all, others need an explicit share.</summary>
    Shared,

    /// <summary>Also: a caller always reaches the sessions they own (matched across their devices).</summary>
    PerUser,

    /// <summary>Also: members of a session's group (via an <c>IGroupProvider</c>) reach it.</summary>
    PerGroup,
}

/// <summary>
/// Thrown when a session is refused by a <see cref="SessionSecurityOptions"/> guardrail (working directory
/// outside the allowlist, or an unsandboxed session on a sandbox-required host). Derives from
/// <see cref="InvalidOperationException"/> so existing "opening a session failed" handling still applies,
/// while a distinct type lets callers and tests recognise a policy rejection specifically.
/// </summary>
public sealed class SessionSecurityException(string message) : InvalidOperationException(message);

/// <summary>
/// Pure containment check for the session-directory allowlist
/// (<see cref="SessionSecurityOptions.AllowedSessionRoots"/>). Kept side-effect-free and static so it is
/// trivially unit-testable and reusable wherever a caller-supplied directory is accepted.
/// </summary>
public static class SessionDirectoryPolicy
{
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Whether <paramref name="candidateDirectory"/> resolves to a location within one of
    /// <paramref name="allowedRoots"/>. An empty root list is "unrestricted" and always allows. Matching is
    /// on a canonicalised, directory-<em>boundary</em>-aware basis, so <c>/srv/work</c> admits
    /// <c>/srv/work/a</c> but not the sibling <c>/srv/work-evil</c>, and any <c>..</c> segments are collapsed
    /// first (so <c>/srv/work/../etc</c> is rejected). Symlinks in the existing portion of the path are
    /// resolved to their final target, so an allowlisted symlink can't be used to escape the root.
    /// </summary>
    public static bool IsWithinAllowedRoots(string? candidateDirectory, IReadOnlyList<string> allowedRoots)
    {
        if (allowedRoots.Count == 0)
        {
            return true; // no allowlist configured — unrestricted.
        }

        if (string.IsNullOrWhiteSpace(candidateDirectory))
        {
            return false;
        }

        var candidate = Canonicalize(candidateDirectory);
        foreach (var root in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var normalizedRoot = Canonicalize(root);
            if (candidate.Equals(normalizedRoot, PathComparison)
                || candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, PathComparison))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Absolute, separator-trimmed form of <paramref name="path"/> with the deepest <em>existing</em> ancestor
    /// resolved through any symlinks. The non-existent remainder (e.g. a working dir about to be created) is
    /// appended lexically — it can't itself be a symlink because it doesn't exist yet.
    /// </summary>
    private static string Canonicalize(string path)
    {
        var full = Path.GetFullPath(path);
        var real = ResolveExistingPrefix(full) ?? full;
        return real.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string? ResolveExistingPrefix(string fullPath)
    {
        try
        {
            var remainder = new Stack<string>();
            var cursor = fullPath;
            while (!Directory.Exists(cursor) && !File.Exists(cursor))
            {
                var parent = Path.GetDirectoryName(cursor);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, cursor, PathComparison))
                {
                    return null; // reached the volume root without finding an existing ancestor.
                }

                remainder.Push(Path.GetFileName(cursor));
                cursor = parent;
            }

            // ResolveLinkTarget returns null when the entry isn't a symlink — fall back to the entry itself.
            var resolved = Directory.ResolveLinkTarget(cursor, returnFinalTarget: true)?.FullName ?? cursor;
            if (remainder.Count == 0)
            {
                return resolved;
            }

            var parts = new string[remainder.Count + 1];
            parts[0] = resolved;
            remainder.CopyTo(parts, 1); // LIFO copy == shallowest-first, which is the correct path order.
            return Path.Combine(parts);
        }
        catch (IOException)
        {
            return null; // best-effort: fall back to the lexical form on any FS error.
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
