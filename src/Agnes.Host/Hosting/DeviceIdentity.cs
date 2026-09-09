namespace Agnes.Host.Hosting;

/// <summary>
/// The stable string behind a credential, used by <see cref="DeviceRegistry.IssueDeviceToken"/> to decide
/// whether a sign-in is a <em>new</em> device or the <em>same</em> one coming back.
/// <para>
/// A federated identity alone is not enough: one GitHub account signs in from a laptop and a phone, and
/// collapsing those onto one record would mean each sign-in silently logged the other device out. So the
/// identity is the account plus the device name the client offered — the same pair the user sees in the
/// device list. A key is different: the private key <em>is</em> the device, so its fingerprint stands alone.
/// </para>
/// </summary>
public static class DeviceIdentity
{
    /// <summary>The identity for an account-based admission (GitHub, OIDC, Cloudflare Access, mTLS).</summary>
    public static string For(string subject, string? deviceName)
        => subject + "#" + (string.IsNullOrWhiteSpace(deviceName) ? "device" : deviceName.Trim());
}
