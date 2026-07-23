using System.Text;

namespace Agnes.Relay.Protocol;

/// <summary>
/// The exact bytes a host signs (ECDSA P-256 / SHA-256, DER) to prove a per-host key controls a
/// host-id during registration: <c>UTF8(nonce + "\n" + hostId)</c>. Lives in the shared protocol
/// project so the relay (verifier) and the host transport (signer) derive it from one source of truth
/// — a byte-for-byte mismatch would silently fail every registration.
/// </summary>
public static class RelayChallenge
{
    /// <summary>Builds the challenge payload bound to the exact host-id being claimed.</summary>
    public static byte[] Payload(string nonce, string hostId) =>
        Encoding.UTF8.GetBytes(nonce + "\n" + hostId);
}
