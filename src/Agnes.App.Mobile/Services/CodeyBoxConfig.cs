using Agnes.Plugins.CodeyBox;

namespace Agnes.App.Mobile.Services;

/// <summary>
/// Where this phone finds the CodeyBox orchestrator, and the key it uses to talk to it.
/// </summary>
/// <remarks>
/// <para><b>Why the phone has its own copy.</b> The desktop plugin resolves CodeyBox the way CodeyBox's
/// own CLI does — the environment, then <c>~/.config/codeybox/config.json</c> — precisely so a machine
/// already set up for <c>codeybox</c> needs no second configuration. A phone has neither of those: it is
/// not the machine the orchestrator runs on, it reaches it across the LAN by address, and Android has no
/// <c>~/.config</c> to read. So this is typed in once and kept beside the app's other device-local state
/// (<see cref="JsonStore"/>), exactly like a paired host's token.</para>
///
/// <para>Plain <c>http</c> on a LAN address is the expected shape. CodeyBox is not an Agnes host: it has
/// no TLS listener and no certificate to pin, so <c>AgnesHttp.For(pin)</c> is the wrong client for it —
/// see the note on <see cref="CodeyBoxClient"/>, which carries its own for the same reason.</para>
///
/// <para>Unconfigured is the ordinary state. Almost nobody running Agnes runs CodeyBox, and until both
/// fields are filled in the app must look exactly as it did before this existed — no tab, no inbox rows,
/// no requests.</para>
/// </remarks>
public sealed record CodeyBoxConfig(string BaseUrl = "", string ApiKey = "", string HostUrl = "")
{
    /// <summary>The fleet is reached through a paired Agnes host's bounded proxy (<c>/fleet</c>), with the
    /// device's own pairing — no address to type, no orchestrator key on the phone. The way a phone should
    /// reach an orchestrator that listens on loopback beside its host.</summary>
    public bool ViaHost => !string.IsNullOrWhiteSpace(HostUrl);

    private const string File = "codeybox.json";

    /// <summary>
    /// What the address field suggests before anything is typed: the port CodeyBox serves on, behind a
    /// LAN address the person has to supply. Deliberately not <c>localhost</c> — that is the desktop
    /// plugin's default and on a phone it addresses the phone.
    /// </summary>
    public const string ExampleBaseUrl = "http://192.168.1.10:5836";

    /// <summary>Both halves present. A URL with no key reaches an orchestrator that answers 401 to
    /// everything, which on screen is indistinguishable from a broken one.</summary>
    public bool IsConfigured => ViaHost || (!string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey));

    /// <summary>The address as the client wants it: no trailing slash, since it appends its own.</summary>
    public string NormalizedUrl => BaseUrl.Trim().TrimEnd('/');

    public CodeyBoxOptions ToOptions() => new(NormalizedUrl, ApiKey.Trim());

    /// <summary>
    /// Where the client should point: through the named host's proxy with that host's pairing and
    /// certificate pin, or straight at the orchestrator. Null when nothing is configured, or when the
    /// named host is no longer paired.
    /// </summary>
    public CodeyBoxEndpoint? Resolve(HostBook hosts)
    {
        if (!ViaHost)
        {
            return IsConfigured ? new CodeyBoxEndpoint(ToOptions(), Fingerprint: null) : null;
        }

        var link = hosts.Find(HostUrl);
        return link is null
            ? null
            : new CodeyBoxEndpoint(new CodeyBoxOptions(link.Url.TrimEnd('/') + "/fleet", link.Saved.Token), link.Saved.Fingerprint);
    }

    public static CodeyBoxConfig Load() => JsonStore.Load(File, new CodeyBoxConfig());

    public void Save() => JsonStore.Save(File, this);

    /// <summary>Where the overview keeps its sparkline samples on this device — the app's own private
    /// directory, not the plugin's desktop default under <c>%APPDATA%</c>.</summary>
    public static string HistoryPath => JsonStore.PathFor("codeybox-overview-history.json");

    /// <summary>Whether this address is plain <c>http</c> to somewhere only this network can reach.</summary>
    /// <remarks>
    /// <para>This is the whole cleartext question, asked once. The app bans cleartext app-wide
    /// (<c>Resources/xml/network_security_config.xml</c>) because Agnes's own traffic carries a bearer
    /// token and session content and is TLS by design — and that ban must stay. But CodeyBox is a
    /// different service with different rules: it is the operator's own orchestrator, bound to their own
    /// machine, with no TLS listener and no certificate to pin, and the address they type is a LAN one.
    /// Android's network-security config cannot express "cleartext, but only to a private address" — its
    /// <c>&lt;domain&gt;</c> entries are hostnames, not CIDRs — so the rule is stated here instead, and
    /// <c>AndroidCodeyBoxTransport</c> is the only thing that acts on it.</para>
    ///
    /// <para>Deliberately narrow. A plain-http CodeyBox on a <em>public</em> address is refused, because
    /// that would be a bearer key crossing the internet in the clear; an <c>https</c> one goes through the
    /// platform stack with its trust anchors, exactly like an Agnes host.</para>
    /// </remarks>
    public static bool IsPrivateCleartext(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = uri.Host.Trim('[', ']');
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!System.Net.IPAddress.TryParse(host, out var ip))
        {
            // A name we cannot resolve here is not a private address we can vouch for.
            return false;
        }

        if (System.Net.IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10                                   // 10.0.0.0/8
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)    // 172.16.0.0/12
                || (b[0] == 192 && b[1] == 168)                 // 192.168.0.0/16
                || (b[0] == 169 && b[1] == 254)                 // 169.254.0.0/16, link-local
                || b[0] == 100 && b[1] >= 64 && b[1] <= 127;    // 100.64.0.0/10, CGNAT — where tailnets live
        }

        // fc00::/7 unique-local, fe80::/10 link-local.
        return ip.IsIPv6LinkLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
    }
}

/// <summary>An orchestrator client's destination: the base address and bearer, plus the certificate pin
/// when the destination is an Agnes host authenticated the way its hub connection is.</summary>
public sealed record CodeyBoxEndpoint(CodeyBoxOptions Options, string? Fingerprint);
