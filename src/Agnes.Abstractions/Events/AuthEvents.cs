namespace Agnes.Abstractions.Events;

// Device-authentication audit events. These are observe-only: authentication has already succeeded (or a
// device has already been revoked) by the time they fire, and vetoing an auth decision belongs to the auth
// method itself, not a general-purpose plugin. They exist so an audit/notification plugin can react to the
// device fleet changing. One file per domain, consistent with the other host-event files.

/// <summary>After a new device has been granted a token (paired via code, GitHub SSO, or keypair).
/// <see cref="Kind"/> is the method ("pairing", "github", "keypair"); <see cref="Subject"/> records who
/// it belongs to (e.g. "github:alice"). The token itself is never carried here.</summary>
public sealed class DevicePairedEvent(string deviceId, string deviceName, string kind, string subject) : IAgnesEvent
{
    public string DeviceId { get; } = deviceId;
    public string DeviceName { get; } = deviceName;
    public string Kind { get; } = kind;
    public string Subject { get; } = subject;
}

/// <summary>After a device's token has been revoked (observe-only).</summary>
public sealed class DeviceRevokedEvent(string deviceId) : IAgnesEvent
{
    public string DeviceId { get; } = deviceId;
}
