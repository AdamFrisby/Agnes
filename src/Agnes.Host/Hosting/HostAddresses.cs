using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Agnes.Protocol;

namespace Agnes.Host.Hosting;

/// <summary>
/// Every address this host might be reachable at, for a client to choose between when it shows a pairing
/// QR.
///
/// <see cref="PairingReachability.Resolve"/> answers "which one address should we advertise" and is
/// necessarily a guess. It is frequently the wrong guess for a QR specifically: a host bound to loopback
/// is perfectly reachable from the desktop client running beside it and completely unreachable from the
/// phone holding the camera. Rather than make that guess smarter, this offers the alternatives — the LAN
/// interface, the Tailscale/CGNAT one, the hostname — and lets the human pick the one their phone can
/// actually route to.
/// </summary>
public static class HostAddresses
{
    /// <summary>
    /// Candidates, best first: the operator's override, then whatever the transport advertises, then this
    /// machine's own interfaces. Loopback is always last, whichever of those produced it, because it is
    /// the one address that cannot work for the device being paired. Duplicates are removed keeping the
    /// earliest position.
    /// </summary>
    public static IReadOnlyList<string> Candidates(
        string? publicUrlOverride,
        TransportEndpoint? endpoint,
        IReadOnlyList<string>? boundAddresses,
        Func<IEnumerable<IPAddress>>? localAddresses = null)
    {
        var ordered = new List<string>();

        if (!string.IsNullOrWhiteSpace(publicUrlOverride))
        {
            ordered.Add(publicUrlOverride.Trim());
        }

        foreach (var advertised in endpoint?.ClientAddresses ?? [])
        {
            if (!string.IsNullOrWhiteSpace(advertised))
            {
                ordered.Add(advertised.Trim());
            }
        }

        // The interfaces need a scheme and port, which only the binding knows. A wildcard binding
        // (0.0.0.0 / [::] / +) tells us nothing about where to reach it, which is exactly the case worth
        // expanding into real addresses.
        if (ResolveBinding(boundAddresses) is { } binding)
        {
            var locals = (localAddresses ?? LocalAddresses)()
                .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Where(a => !IPAddress.IsLoopback(a))
                .Where(a => !a.IsIPv6LinkLocal && !a.IsIPv6Multicast)
                .Distinct()
                .OrderBy(Rank)
                .ThenBy(a => a.ToString(), StringComparer.Ordinal);

            foreach (var address in locals)
            {
                ordered.Add(Format(binding, address));
            }

            var hostName = SafeHostName();
            if (hostName is not null)
            {
                ordered.Add($"{binding.Scheme}://{hostName}:{binding.Port}");
            }

            // Last: useful only to a client on this same machine, which is the desktop but never the phone.
            ordered.Add($"{binding.Scheme}://127.0.0.1:{binding.Port}");
        }

        foreach (var bound in boundAddresses ?? [])
        {
            if (!string.IsNullOrWhiteSpace(bound) && !IsWildcard(bound))
            {
                ordered.Add(bound.Trim());
            }
        }

        // Loopback sinks to the bottom wherever it came from — including from the transport, which on a
        // host bound only to 127.0.0.1 advertises exactly that. It stays in the list because a client on
        // the same machine can use it, but it must never be the first thing offered for a QR.
        var unique = ordered.Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return unique.Where(a => !IsLoopback(a)).Concat(unique.Where(IsLoopback)).ToArray();
    }

    /// <summary>
    /// A routable candidate to prefer when the resolved address is loopback. A QR encoding loopback can
    /// never be scanned successfully, so falling back to something routable is strictly better than
    /// showing a code that cannot work.
    /// </summary>
    public static string? FirstRoutable(IReadOnlyList<string> candidates)
        => candidates.FirstOrDefault(c => !IsLoopback(c));

    public static bool IsLoopback(string address)
        => Uri.TryCreate(address, UriKind.Absolute, out var uri)
           && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    /// <summary>Ordering within the interface addresses: a private LAN address is the common case, a
    /// CGNAT one is how Tailscale presents, and anything else is rarer still.</summary>
    private static int Rank(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return 3;
        }

        var octets = address.GetAddressBytes();
        var isPrivate = octets[0] == 10
            || (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31)
            || (octets[0] == 192 && octets[1] == 168);
        var isCarrierGrade = octets[0] == 100 && octets[1] >= 64 && octets[1] <= 127; // Tailscale et al.

        return isPrivate ? 0 : isCarrierGrade ? 1 : 2;
    }

    private static string Format(Binding binding, IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"{binding.Scheme}://[{address}]:{binding.Port}"
            : $"{binding.Scheme}://{address}:{binding.Port}";

    private static Binding? ResolveBinding(IReadOnlyList<string>? boundAddresses)
    {
        foreach (var bound in boundAddresses ?? [])
        {
            // "http://+:5099" is legal for Kestrel but not for Uri; normalise the wildcards it uses.
            var normalised = (bound ?? string.Empty).Replace("+", "0.0.0.0", StringComparison.Ordinal)
                .Replace("*", "0.0.0.0", StringComparison.Ordinal);

            if (Uri.TryCreate(normalised, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                return new Binding(uri.Scheme, uri.Port);
            }
        }

        return null;
    }

    private static bool IsWildcard(string address)
        => address.Contains("0.0.0.0", StringComparison.Ordinal)
           || address.Contains("[::]", StringComparison.Ordinal)
           || address.Contains("+:", StringComparison.Ordinal)
           || address.Contains("*:", StringComparison.Ordinal);

    private static IEnumerable<IPAddress> LocalAddresses()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var info in nic.GetIPProperties().UnicastAddresses)
            {
                yield return info.Address;
            }
        }
    }

    private static string? SafeHostName()
    {
        try
        {
            var name = Dns.GetHostName();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            // A machine with no resolvable name still has usable interface addresses.
            return null;
        }
    }

    private readonly record struct Binding(string Scheme, int Port);
}
