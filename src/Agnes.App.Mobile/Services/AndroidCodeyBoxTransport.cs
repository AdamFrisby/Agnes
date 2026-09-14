using Agnes.Plugins.CodeyBox;

namespace Agnes.App.Mobile.Services;

/// <summary>
/// How this device talks to a CodeyBox.
/// </summary>
/// <remarks>
/// <para><b>Why this exists at all.</b> The app bans cleartext for everything
/// (<c>Resources/xml/network_security_config.xml</c>), and .NET for Android's default
/// <c>HttpClient</c> goes through the Java stack, which enforces that ban — so a plain-http CodeyBox on
/// the operator's LAN fails before any of our code runs, with an error that reads exactly like an
/// unreachable address. That ban is right for Agnes's own traffic and is staying; what was missing is
/// that Android cannot express the one exception this needs, because its
/// <c>&lt;domain&gt;</c> entries are hostnames and the address in question is whatever private IP the
/// operator's machine happens to have.</para>
///
/// <para>So the exception is stated where it can be: <see cref="CodeyBoxConfig.IsPrivateCleartext"/>
/// decides, and only a private, cleartext address gets a managed <see cref="SocketsHttpHandler"/>, which
/// does not consult the platform policy. Everything else — an <c>https</c> CodeyBox, or a plain-http one
/// on a public address — gets the default client and therefore the platform's policy and trust anchors,
/// which for the public-cleartext case means it is refused. A bearer key does not cross the internet in
/// the clear on our account.</para>
///
/// <para>Android-only on purpose: it is a statement about a platform policy, and the headless preview
/// harness (which links this project's platform-neutral half) has no such policy to work around.</para>
/// </remarks>
internal static class AndroidCodeyBoxTransport
{
    /// <summary>The client factory the shell hands to the fleet's view model.</summary>
    internal static CodeyBoxClient Create(CodeyBoxEndpoint endpoint)
        => endpoint switch
        {
            // Through a paired host: the same pin the hub connection trusts, or nothing to pin on plain http.
            { Fingerprint: { Length: > 0 } pin } => new CodeyBoxClient(endpoint.Options, Agnes.Client.PinnedTls.CreateHandler(pin)),
            { Options.BaseUrl: var url } when CodeyBoxConfig.IsPrivateCleartext(url) => new CodeyBoxClient(endpoint.Options, new SocketsHttpHandler()),
            _ => new CodeyBoxClient(endpoint.Options),
        };
}
