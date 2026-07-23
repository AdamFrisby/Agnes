namespace Agnes.Host.Notifications;

/// <summary>
/// The tiny seam between <see cref="FcmPushChannel"/>'s payload-mapping logic and the actual Firebase Admin
/// SDK call. Kept deliberately minimal (one send method over primitives) so the channel is fully unit-testable
/// with a fake, and the real <see cref="FirebaseFcmSender"/> — which touches a live <c>FirebaseApp</c> — is only
/// ever constructed when a service-account credential is present in host settings. Nothing about FirebaseAdmin
/// leaks across this boundary.
/// </summary>
public interface IFcmSender
{
    /// <summary>Delivers a single message to one device's FCM registration token. <paramref name="title"/>/
    /// <paramref name="body"/> populate the visible notification; <paramref name="data"/> carries the routing
    /// fields (session id, trigger) the app reads to deep-link. Throws on a transport/credential failure — the
    /// channel is responsible for logging + swallowing so delivery stays independent per device.</summary>
    Task SendAsync(
        string registrationToken,
        string title,
        string body,
        IReadOnlyDictionary<string, string> data,
        CancellationToken ct);
}
