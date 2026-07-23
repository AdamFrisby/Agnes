namespace Agnes.Client;

/// <summary>
/// Which client transport reaches a host, chosen from its address scheme. This mirrors the host-side
/// <c>ITransportProvider.Id</c> values (<c>direct</c> / <c>agnes-relay</c> / <c>tailscale</c>) so a host
/// added by a LAN URL, an <c>agnes-relay://</c> address, or a tailnet <c>*.ts.net</c> name is routed the
/// same way — the client never has to be told which transport to use, it reads it off the address.
/// </summary>
public enum ClientTransportKind
{
    /// <summary>A plain <c>http(s)://</c> URL to the host's own bound listener (today's default).</summary>
    Direct,

    /// <summary>An <c>agnes-relay://</c> address tunnelled end-to-end through a blind relay.</summary>
    Relay,

    /// <summary>A Tailscale MagicDNS <c>*.ts.net</c> name — reached directly over the tailnet.</summary>
    Tailscale,
}

/// <summary>
/// Address → transport routing for the client pool. A single rule set decides, from an address alone, which
/// transport a host uses, so <see cref="AgnesClient"/> and <see cref="HostConnection"/> stay address-agnostic
/// (multi-server support, <c>connectivity/02</c>).
/// </summary>
public static class ClientTransport
{
    /// <summary>The MagicDNS suffix that identifies a tailnet name.</summary>
    public const string TailnetSuffix = ".ts.net";

    /// <summary>Classifies a host address into the transport that reaches it.</summary>
    public static ClientTransportKind Classify(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return ClientTransportKind.Direct;
        }

        if (RelayClientTransport.IsRelayAddress(address))
        {
            return ClientTransportKind.Relay;
        }

        if (Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
            && uri.Host.EndsWith(TailnetSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return ClientTransportKind.Tailscale;
        }

        return ClientTransportKind.Direct;
    }

    /// <summary>The host-side <c>ITransportProvider.Id</c> a transport kind corresponds to.</summary>
    public static string ProviderId(ClientTransportKind kind) => kind switch
    {
        ClientTransportKind.Relay => "agnes-relay",
        ClientTransportKind.Tailscale => "tailscale",
        _ => "direct",
    };
}
