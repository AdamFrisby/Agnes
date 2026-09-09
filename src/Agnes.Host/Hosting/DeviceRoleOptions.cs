using Agnes.Protocol;
using Microsoft.Extensions.Configuration;

namespace Agnes.Host.Hosting;

/// <summary>
/// Which admission methods entitle a device to <see cref="DeviceRole.Owner"/> on this host.
/// <para>
/// The principle: a device admitted with the <em>operator's own secret</em> is an Owner, and a device
/// vouched for by somebody else is a Member until an Owner says otherwise. The pairing code is read off the
/// host's console; a pairing grant is minted by a device that already has the run of the host; an authorized
/// key was put into <c>authorized_keys</c> by whoever administers the machine. Those are the operator. A
/// federated identity (GitHub, OIDC, Cloudflare Access, mTLS) proves who somebody <em>is</em>, not that they
/// administer this host — so those are Members unless the operator names them in an owners list.
/// </para>
/// </summary>
public sealed class DeviceRoleOptions
{
    /// <summary>What an authorized key is admitted as. Owner by default — an operator edited
    /// <c>authorized_keys</c> to put it there — but a host that hands keys to a team can set
    /// <c>Agnes:Auth:Keypair:Role=Member</c> and promote individually.</summary>
    public DeviceRole KeypairRole { get; init; } = DeviceRole.Owner;

    /// <summary>GitHub logins admitted as Owner (<c>Agnes:Auth:GitHub:Owners</c>).</summary>
    public IReadOnlyList<string> GitHubOwners { get; init; } = [];

    /// <summary>OIDC subjects (or the login/email the token carried) admitted as Owner.</summary>
    public IReadOnlyList<string> OidcOwners { get; init; } = [];

    /// <summary>Cloudflare Access identities admitted as Owner.</summary>
    public IReadOnlyList<string> CloudflareOwners { get; init; } = [];

    /// <summary>Client-certificate subjects admitted as Owner.</summary>
    public IReadOnlyList<string> MtlsOwners { get; init; } = [];

    public static DeviceRoleOptions FromConfiguration(IConfiguration configuration) => new()
    {
        KeypairRole = ParseRole(configuration["Agnes:Auth:Keypair:Role"], DeviceRole.Owner),
        GitHubOwners = List(configuration, "Agnes:Auth:GitHub:Owners"),
        OidcOwners = List(configuration, "Agnes:Auth:Oidc:Owners"),
        CloudflareOwners = List(configuration, "Agnes:Auth:CloudflareAccess:Owners"),
        MtlsOwners = List(configuration, "Agnes:Auth:Mtls:Owners"),
    };

    public DeviceRole ForGitHub(string? login) => Match(GitHubOwners, login);

    public DeviceRole ForOidc(string? subject) => Match(OidcOwners, subject);

    /// <summary>Cloudflare Access identifies a person by both an opaque subject and an email; an operator
    /// will realistically write the email in the owners list, so either matching is enough.</summary>
    public DeviceRole ForCloudflare(string? subject, string? email)
        => Match(CloudflareOwners, subject) == DeviceRole.Owner ? DeviceRole.Owner : Match(CloudflareOwners, email);

    public DeviceRole ForMtls(string? subject) => Match(MtlsOwners, subject);

    private static DeviceRole Match(IReadOnlyList<string> owners, string? identity)
        => identity is { Length: > 0 } && owners.Any(o => string.Equals(o, identity, StringComparison.OrdinalIgnoreCase))
            ? DeviceRole.Owner
            : DeviceRole.Member;

    private static IReadOnlyList<string> List(IConfiguration configuration, string key)
        => configuration.GetSection(key).Get<string[]>() ?? [];

    private static DeviceRole ParseRole(string? value, DeviceRole fallback)
        => Enum.TryParse<DeviceRole>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
}
