using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;

namespace Agnes.Host.Notifications;

/// <summary>
/// The real <see cref="IFcmSender"/> over Google's official Firebase Admin SDK. It initializes a
/// <see cref="FirebaseApp"/> from this deployment's own service-account JSON (bring-your-own credential, from
/// host settings — never a shared project-run relay) exactly once, lazily on first send, and delivers via
/// <see cref="FirebaseMessaging.SendAsync(Message, CancellationToken)"/>.
/// <para>
/// Constructed only when a credential is actually present, so a deployment with no FCM setup never touches
/// FirebaseAdmin. It is exercised end-to-end only against a configured credential (a real Firebase project);
/// the offline unit tests use a fake <see cref="IFcmSender"/> instead — see the notifications tests.
/// </para>
/// </summary>
public sealed class FirebaseFcmSender : IFcmSender
{
    // A dedicated, named app so it never collides with FirebaseAdmin's process-global [DEFAULT] instance.
    private const string AppName = "agnes-fcm";

    private readonly Lazy<FirebaseMessaging> _messaging;

    /// <summary>Creates a sender bound to the given service-account JSON. The <see cref="FirebaseApp"/> itself
    /// is not created until the first <see cref="SendAsync"/>, so construction is cheap and side-effect-free.</summary>
    public FirebaseFcmSender(string serviceAccountJson)
    {
        if (string.IsNullOrWhiteSpace(serviceAccountJson))
        {
            throw new ArgumentException("An FCM service-account credential (JSON) is required.", nameof(serviceAccountJson));
        }

        _messaging = new Lazy<FirebaseMessaging>(() =>
        {
            var app = FirebaseApp.Create(
                new AppOptions { Credential = GoogleCredential.FromJson(serviceAccountJson) },
                AppName);
            return FirebaseMessaging.GetMessaging(app);
        });
    }

    public async Task SendAsync(
        string registrationToken,
        string title,
        string body,
        IReadOnlyDictionary<string, string> data,
        CancellationToken ct)
    {
        var message = new Message
        {
            Token = registrationToken,
            Notification = new Notification { Title = title, Body = body },
            Data = new Dictionary<string, string>(data, StringComparer.Ordinal),
        };

        await _messaging.Value.SendAsync(message, ct).ConfigureAwait(false);
    }
}
