using System.Net.Http;
using System.Net.Http.Json;
using Agnes.Protocol;

namespace Agnes.Client;

/// <summary>What happened when a client probed an address for an Agnes host.</summary>
public enum HostProbeOutcome
{
    /// <summary>An Agnes host answered and described its sign-in methods.</summary>
    Reachable,

    /// <summary>Something answered, but not with a usable method list — an older Agnes host (no
    /// <c>/auth/methods</c>) or an unrelated server. Pairing is still worth offering.</summary>
    Answered,

    /// <summary>Nothing answered: wrong address, host down, DNS failure, blocked port, bad certificate.</summary>
    Unreachable,
}

/// <summary>The result of probing an address, with the reason when it failed.</summary>
public sealed record HostProbe(HostProbeOutcome Outcome, AuthMethods Methods, string? Error = null)
{
    public bool CanReach => Outcome != HostProbeOutcome.Unreachable;
}

/// <summary>
/// Asks a host which sign-in methods it offers (<c>GET /auth/methods</c>) so a client shows only the
/// enabled ones.
/// </summary>
public static class AuthDiscovery
{
    private static readonly AuthMethods PairingOnly = new(Pairing: true, GitHub: false, GitHubClientId: null, Keypair: false);

    /// <summary>
    /// Method discovery that falls back to pairing-only, which is what every host supported before the
    /// endpoint existed. Cannot distinguish an unreachable host from a legacy one — use
    /// <see cref="ProbeAsync"/> when that difference matters to the user.
    /// </summary>
    public static async Task<AuthMethods> GetMethodsAsync(
        string hostUrl, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
        => (await ProbeAsync(hostUrl, httpClient, cancellationToken).ConfigureAwait(false)).Methods;

    /// <summary>
    /// Probes an address and reports whether anything is actually there, alongside its methods.
    ///
    /// The distinction matters: an unreachable host and a host that rejects a credential are completely
    /// different problems, and a client that collapses them ends up telling the user to check their
    /// pairing code when the real answer is that the address is wrong.
    /// </summary>
    public static async Task<HostProbe> ProbeAsync(
        string hostUrl, HttpClient? httpClient = null, CancellationToken cancellationToken = default)
    {
        var client = httpClient ?? new HttpClient();
        try
        {
            var url = hostUrl.TrimEnd('/') + "/auth/methods";
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Something is listening — an older Agnes host, or another service on that port.
                return new HostProbe(HostProbeOutcome.Answered, PairingOnly,
                    $"Answered with {(int)response.StatusCode}, but didn't describe its sign-in methods.");
            }

            var methods = await response.Content.ReadFromJsonAsync<AuthMethods>(cancellationToken).ConfigureAwait(false);
            return methods is null
                ? new HostProbe(HostProbeOutcome.Answered, PairingOnly, "Answered, but not with an Agnes method list.")
                : new HostProbe(HostProbeOutcome.Reachable, methods);
        }
        catch (OperationCanceledException)
        {
            throw; // a superseded probe, not a failure worth reporting
        }
        catch (Exception ex)
        {
            return new HostProbe(HostProbeOutcome.Unreachable, PairingOnly, DescribeFailure(ex));
        }
        finally
        {
            if (httpClient is null)
            {
                client.Dispose();
            }
        }
    }

    /// <summary>Turns a transport failure into something a person can act on. The raw exception chain
    /// ("An error occurred while sending the request") names the symptom, never the cause.</summary>
    public static string DescribeFailure(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case System.Net.Sockets.SocketException socket:
                    return socket.SocketErrorCode switch
                    {
                        System.Net.Sockets.SocketError.HostNotFound => "That name doesn't resolve.",
                        System.Net.Sockets.SocketError.ConnectionRefused => "Nothing is listening on that port.",
                        System.Net.Sockets.SocketError.TimedOut => "Timed out — the host may be asleep or firewalled.",
                        System.Net.Sockets.SocketError.NetworkUnreachable => "That network isn't reachable from here.",
                        _ => socket.Message,
                    };

                case System.Security.Authentication.AuthenticationException:
                    return "The TLS certificate wasn't trusted. Install the host's CA on this device.";

                case TimeoutException:
                    return "Timed out — the host may be asleep or firewalled.";
            }
        }

        return ex.Message;
    }
}
