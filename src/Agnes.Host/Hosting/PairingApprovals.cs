using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Agnes.Protocol;

namespace Agnes.Host.Hosting;

/// <summary>
/// Pairing by approval: a new device asks, an already-paired device says yes.
///
/// This is the path for when scanning a QR isn't possible — no camera, two screens that can't see each
/// other, a headless client. It replaces "type this short secret" with "compare these digits", which is
/// a materially different security property: the digits are a <em>comparison value</em>, not a
/// credential. Knowing them grants nothing. They exist so that if an attacker races a request in at the
/// same moment, the human notices the number on their new device doesn't match the one being approved.
///
/// The code is derived from the requesting device's public key and the request id, so the requesting
/// device computes the same digits locally rather than being told them — otherwise a substituted key
/// would show matching numbers and the comparison would prove nothing.
///
/// Requests live in memory with a short TTL, for the same reason grants do.
/// </summary>
public sealed class PairingApprovals
{
    /// <summary>How long an unanswered request stays live. Long enough to walk to another machine.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>How many requests may be outstanding at once, so an unauthenticated endpoint can't be
    /// used to flood the approver's screen (or the host's memory).</summary>
    private const int MaxOutstanding = 10;

    private readonly ConcurrentDictionary<string, PendingRequest> _requests = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _now;

    public PairingApprovals(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    private sealed class PendingRequest
    {
        public required string Id { get; init; }
        public required string DeviceName { get; init; }
        public required string PublicKey { get; init; }
        public required string VerificationCode { get; init; }
        public required DateTimeOffset RequestedAt { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
        public PairApprovalState State { get; set; } = PairApprovalState.Pending;
        public string? DeviceId { get; set; }
        public string? Token { get; set; }
    }

    /// <summary>
    /// Records a new device's request to be vouched for. Returns null when too many are already
    /// outstanding, so this unauthenticated endpoint can't be used to spam an operator into approving
    /// something by accident.
    /// </summary>
    public PairApprovalPending? Open(string publicKey, string deviceName)
    {
        Sweep();
        if (_requests.Count >= MaxOutstanding)
        {
            return null;
        }

        var id = PairingGrants.Base64Url(RandomNumberGenerator.GetBytes(16));
        var request = new PendingRequest
        {
            Id = id,
            DeviceName = string.IsNullOrWhiteSpace(deviceName) ? "New device" : deviceName.Trim(),
            PublicKey = publicKey,
            VerificationCode = DeriveVerificationCode(publicKey, id),
            RequestedAt = _now(),
            ExpiresAt = _now() + Lifetime,
        };

        _requests[id] = request;
        return new PairApprovalPending(id, request.VerificationCode, request.ExpiresAt);
    }

    /// <summary>What's waiting for a human, newest first.</summary>
    public IReadOnlyList<PendingPairApproval> Pending()
    {
        Sweep();
        return _requests.Values
            .Where(r => r.State == PairApprovalState.Pending)
            .OrderByDescending(r => r.RequestedAt)
            .Select(r => new PendingPairApproval(r.Id, r.DeviceName, r.VerificationCode, r.RequestedAt, r.ExpiresAt))
            .ToList();
    }

    /// <summary>
    /// Approves a request, minting the device's token via <paramref name="issue"/>. The token is held
    /// until the requesting device next polls, because that device is the only one that should ever see
    /// it — it is never returned to the approver.
    /// </summary>
    public bool Approve(string requestId, Func<string, string, PairingResult> issue)
    {
        Sweep();
        if (!_requests.TryGetValue(requestId, out var request) || request.State != PairApprovalState.Pending)
        {
            return false;
        }

        var result = issue(request.DeviceName, request.PublicKey);
        request.DeviceId = result.DeviceId;
        request.Token = result.Token;
        request.State = PairApprovalState.Approved;
        return true;
    }

    public bool Deny(string requestId)
    {
        Sweep();
        if (!_requests.TryGetValue(requestId, out var request) || request.State != PairApprovalState.Pending)
        {
            return false;
        }

        request.State = PairApprovalState.Denied;
        return true;
    }

    /// <summary>
    /// Polled by the requesting device. The token is handed over exactly once and then dropped, so a
    /// replayed poll (or an observer who learns the request id later) gets nothing.
    ///
    /// An unknown id and an expired one are deliberately reported the same way, so polling can't be used
    /// to enumerate live requests.
    /// </summary>
    public PairApprovalStatus Poll(string requestId)
    {
        Sweep();
        if (!_requests.TryGetValue(requestId, out var request))
        {
            return new PairApprovalStatus(PairApprovalState.Unknown);
        }

        if (request.State != PairApprovalState.Approved)
        {
            return new PairApprovalStatus(request.State);
        }

        var token = request.Token;
        request.Token = null;
        _requests.TryRemove(requestId, out _);
        return new PairApprovalStatus(PairApprovalState.Approved, request.DeviceId, token);
    }

    /// <summary>The six digits both screens must show — the shared derivation, so the host and the
    /// requesting device can never disagree about them.</summary>
    public static string DeriveVerificationCode(string publicKey, string requestId)
        => PairVerification.Derive(publicKey, requestId);

    private void Sweep()
    {
        var now = _now();
        foreach (var (id, request) in _requests)
        {
            if (request.ExpiresAt <= now)
            {
                _requests.TryRemove(id, out _);
            }
        }
    }
}
