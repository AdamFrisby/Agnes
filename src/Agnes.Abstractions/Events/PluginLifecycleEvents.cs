namespace Agnes.Abstractions.Events;

// Plugin management commands — install/update, enable/disable, uninstall of NuGet-distributed plugins.
// These are the governance hooks: a policy/allow-list plugin can veto installing an untrusted package or
// disabling a required one. Same taxonomy (Before* vetoable, *edEvent observe-only); one file per domain.

/// <summary>Before a plugin package is installed or updated. Interceptors may veto (the install returns a
/// "blocked" outcome to the caller). <see cref="Version"/> is null when the caller didn't pin one.</summary>
public sealed class BeforePluginInstallEvent(string packageId, string? version) : CancelableEvent
{
    public string PackageId { get; } = packageId;
    public string? Version { get; } = version;
}

/// <summary>After a plugin has been installed or updated (observe-only).</summary>
public sealed class PluginInstalledEvent(string pluginId, string version) : IAgnesEvent
{
    public string PluginId { get; } = pluginId;
    public string Version { get; } = version;
}

/// <summary>Before a plugin is enabled or disabled. Veto keeps its current state.</summary>
public sealed class BeforePluginEnableChangeEvent(string pluginId, bool enabled) : CancelableEvent
{
    public string PluginId { get; } = pluginId;

    /// <summary>The requested target state — true to enable, false to disable.</summary>
    public bool Enabled { get; } = enabled;
}

/// <summary>After a plugin's enabled state changed (observe-only).</summary>
public sealed class PluginEnableChangedEvent(string pluginId, bool enabled) : IAgnesEvent
{
    public string PluginId { get; } = pluginId;
    public bool Enabled { get; } = enabled;
}

/// <summary>Before a plugin is uninstalled. Veto keeps it installed.</summary>
public sealed class BeforePluginUninstallEvent(string pluginId) : CancelableEvent
{
    public string PluginId { get; } = pluginId;
}

/// <summary>After a plugin has been uninstalled (observe-only).</summary>
public sealed class PluginUninstalledEvent(string pluginId) : IAgnesEvent
{
    public string PluginId { get; } = pluginId;
}
