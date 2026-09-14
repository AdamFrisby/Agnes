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
public sealed record CodeyBoxConfig(string BaseUrl = "", string ApiKey = "")
{
    private const string File = "codeybox.json";

    /// <summary>
    /// What the address field suggests before anything is typed: the port CodeyBox serves on, behind a
    /// LAN address the person has to supply. Deliberately not <c>localhost</c> — that is the desktop
    /// plugin's default and on a phone it addresses the phone.
    /// </summary>
    public const string ExampleBaseUrl = "http://192.168.1.10:5836";

    /// <summary>Both halves present. A URL with no key reaches an orchestrator that answers 401 to
    /// everything, which on screen is indistinguishable from a broken one.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>The address as the client wants it: no trailing slash, since it appends its own.</summary>
    public string NormalizedUrl => BaseUrl.Trim().TrimEnd('/');

    public CodeyBoxOptions ToOptions() => new(NormalizedUrl, ApiKey.Trim());

    public static CodeyBoxConfig Load() => JsonStore.Load(File, new CodeyBoxConfig());

    public void Save() => JsonStore.Save(File, this);

    /// <summary>Where the overview keeps its sparkline samples on this device — the app's own private
    /// directory, not the plugin's desktop default under <c>%APPDATA%</c>.</summary>
    public static string HistoryPath => JsonStore.PathFor("codeybox-overview-history.json");
}
