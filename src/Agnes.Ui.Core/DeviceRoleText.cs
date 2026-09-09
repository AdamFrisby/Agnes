using Agnes.Protocol;

namespace Agnes.Ui.Core;

/// <summary>
/// The words every head uses for device roles and admission, in one place.
///
/// A member device that can see none of the host's sessions is indistinguishable from a broken host
/// unless something says so, and the sentence that says so appears in at least four places (two empty
/// states, two Devices pages) across two heads. Wording that drifts between them is wording a user
/// can't learn, so it is stated once here and asserted in tests rather than retyped per view.
/// </summary>
public static class DeviceRoleText
{
    /// <summary>The chip label. Deliberately a plain noun, not a status hue: a role is a fact about a
    /// device, not something wrong with it.</summary>
    public static string Chip(DeviceRole role) => role == DeviceRole.Owner ? "Owner" : "Member";

    /// <summary>
    /// How a device got in, in words. Null for a kind this client doesn't recognise (a newer host, or
    /// one that predates the field) — a view shows nothing rather than a raw token.
    /// </summary>
    public static string? Admission(string? kind) => (kind ?? string.Empty).ToLowerInvariant() switch
    {
        "pairing" or "code" or "bootstrap" => "paired with code",
        "grant" or "qr" => "paired with a QR link",
        "approval" or "vouch" => "vouched for by a device",
        "keypair" or "key" => "authorized key",
        "github" or "sso" => "GitHub",
        "oidc" => "single sign-on",
        "mtls" => "client certificate",
        _ => null,
    };

    /// <summary>What a Member is allowed to do, said once so the two empty states agree.</summary>
    public const string MemberScope =
        "It sees the sessions it starts and any shared with it.";

    /// <summary>Shown where a session list is empty and this device is a Member — the answer to the
    /// question an empty list otherwise leaves unanswered.</summary>
    public static string EmptyStateForMember(string hostName, string settingsPath = "Settings › Devices")
        => $"This device is a member on {hostName}. {MemberScope} "
           + $"An owner can make it an owner in {settingsPath}.";

    /// <summary>The status line after a successful pairing or sign-in, which is the first and best
    /// moment to say what the device just became.</summary>
    public static string Paired(DeviceRole role) => role == DeviceRole.Owner
        ? "Paired as owner"
        : "Paired as member — ask an owner to promote this device if you need to see everything";

    /// <summary>The line a non-owner sees under the Devices list, in place of the buttons.</summary>
    public const string OnlyOwnersManage = "Only an owner can change roles.";

    /// <summary>Why a demote button is disabled on the one remaining owner.</summary>
    public const string LastOwnerTooltip =
        "A host needs at least one owner — promote another device first.";

    /// <summary>The confirmation for a prune, which names the count rather than asking blind.</summary>
    public static string ConfirmPrune(int count, int days) => count == 1
        ? $"Remove 1 device not seen for {days} days?"
        : $"Remove {count} devices not seen for {days} days?";

    /// <summary>What a prune button offers. The window is stated so the button is not a mystery.</summary>
    public static string PruneAction(int days) => $"Remove devices unused for {days} days";
}
