using Agnes.Protocol;

namespace Agnes.Ui.Core;

/// <summary>What an <c>agnes://</c> link is asking for.</summary>
public enum AgnesLinkKind
{
    /// <summary>Enrol this device with a host. Deliberate, and may carry a one-time grant.</summary>
    Pair,

    /// <summary>Look at a session on a host you already have access to. Carries no credential.</summary>
    ViewSession,
}

/// <summary>
/// What an <c>agnes://</c> link means, decided once for every client that can receive one.
///
/// Both heads answer the same questions — which host, which session, which moment, and what (if anything) the
/// link authorises — and they must answer them identically: a link that works on the phone and stalls on the
/// desktop is a bug nobody reports because it looks like the link was bad.
///
/// The two kinds are kept rigidly apart because they carry very different risk. A <see cref="AgnesLinkKind.Pair"/>
/// link can enrol a device and hand it the whole host, so it's something you act on deliberately. A
/// <see cref="AgnesLinkKind.ViewSession"/> link is a pointer — safe in a group chat, useful only to people who
/// already have access. This type enforces that difference rather than trusting callers to remember it:
/// <see cref="Secret"/> is null for a view link no matter what the URL says, so a hand-crafted
/// <c>agnes://session?…&amp;grant=…</c> can't talk a client into a pairing prompt.
///
/// Keeping it here rather than in each head's activation code also makes it testable. On Android the link
/// arrives in <c>MainActivity</c>, which only compiles with the android workload and so is absent from the
/// solution filter CI builds; on macOS it arrives through an Avalonia protocol activation. Neither can be
/// exercised in an ordinary test. This can, and they shrink to a few lines of glue apiece.
/// </summary>
/// <param name="AutoSubmit">
/// True only for a scanned one-time <c>grant</c> on a pairing link. A grant is minted by an already-paired
/// device and shown as a QR on the host's own screen, so holding it is itself the proof the link is genuine.
/// A typed <c>code</c> carries no such proof — it gets prefilled and waits. Always false for a view link.
/// </param>
public sealed record AgnesLinkRoute(
    AgnesLinkKind Kind,
    string HostUrl,
    string? Secret,
    string? SessionId,
    long? Sequence,
    string? Fingerprint,
    bool AutoSubmit)
{
    /// <summary>
    /// Reads a link, or returns null when it names no usable host — the one case every caller handles the
    /// same way: do nothing, rather than open an empty form nobody asked for.
    /// </summary>
    public static AgnesLinkRoute? Parse(string? link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            return null;
        }

        var trimmed = link.Trim();
        var host = PairingLink.HostOf(trimmed);
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var session = PairingLink.SessionOf(trimmed);

        // A view link authorises nothing, so no secret is read from one — not even if the URL carries a
        // `grant` parameter. This is the line that keeps a shareable pointer from becoming a way to ask a
        // stranger's client to enrol with a host.
        if (SessionLink.IsSessionLink(trimmed))
        {
            return string.IsNullOrWhiteSpace(session)
                ? null // a view link with nothing to view
                : new AgnesLinkRoute(
                    AgnesLinkKind.ViewSession, host!, Secret: null, session, PairingLink.SequenceOf(trimmed),
                    PairingLink.FingerprintOf(trimmed), AutoSubmit: false);
        }

        var grant = PairingLink.GrantOf(trimmed);
        return new AgnesLinkRoute(
            AgnesLinkKind.Pair,
            host!,
            grant ?? PairingLink.CodeOf(trimmed),
            session,
            PairingLink.SequenceOf(trimmed),
            PairingLink.FingerprintOf(trimmed),
            AutoSubmit: !string.IsNullOrWhiteSpace(grant));
    }
}
