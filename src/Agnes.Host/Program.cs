using Agnes.Abstractions;
using Agnes.Acp;
using Agnes.Host.Attention;
using Agnes.Host.Channels;
using Agnes.Agents.ClaudeCode;
using Agnes.Agents.OpenCode;
using Agnes.Host.Events;
using Agnes.Host.Hosting;
using Agnes.Host.Sessions;
using Agnes.Protocol;
using Agnes.Host.Plugins;

var builder = WebApplication.CreateBuilder(args);

// Surface the real server-side exception message to the client (this is a local, single-user host, so
// there's no third party to leak internals to) — otherwise hub failures arrive as an opaque
// "An unexpected error occurred" and can't be diagnosed from the UI.
builder.Services.AddSignalR(o => o.EnableDetailedErrors = true);

// ---- transport as a built-in plugin (AC13): how the host is reachable by clients ----
// Direct (clients connect straight to this host's listener) is the only built-in and the default; a relay
// or tunnel transport can be added as a plugin. This governs reachability/address advertisement only — the
// SignalR hub binding below is unchanged.
builder.Services.AddSingleton<ITransportProvider, DirectTransportProvider>();
// Tailscale: one-click tailnet exposure via `tailscale serve` (tailnet-only, default) or `tailscale funnel`
// (public, opt-in). Registered as a plugin so Agnes:Transport:Provider=tailscale selects it; Direct stays
// the default. See .ideas/connectivity/01-relay-and-tunneling.md.
builder.Services.AddSingleton<ITailscaleCli>(_ => new TailscaleCli());
builder.Services.AddSingleton(new TailscaleTransportOptions
{
    Funnel = builder.Configuration.GetValue("Agnes:Transport:Tailscale:Funnel", false),
    HttpsPort = builder.Configuration.GetValue("Agnes:Transport:Tailscale:HttpsPort", 443),
    HubPort = builder.Configuration.GetValue<int?>("Agnes:Transport:Tailscale:HubPort", null),
});
builder.Services.AddSingleton<ITransportProvider>(sp =>
    new TailscaleTransportProvider(sp.GetRequiredService<ITailscaleCli>(), sp.GetRequiredService<TailscaleTransportOptions>()));
// Agnes relay: dial out to a self-hosted blind relay so a host behind NAT is reachable with no inbound port
// (Agnes:Transport:Provider=agnes-relay). TLS terminates at Kestrel with a pinned self-signed host cert; the
// relay and the host's loopback pump only move already-encrypted bytes. See .ideas/connectivity/01-relay-and-tunneling.md.
var agnesHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes");
var relayTransportOptions = new RelayTransportOptions
{
    Url = builder.Configuration["Agnes:Transport:Relay:Url"] ?? "",
    HostId = builder.Configuration["Agnes:Transport:Relay:HostId"] ?? "",
    HubPort = builder.Configuration.GetValue<int?>("Agnes:Transport:Relay:HubPort", null),
};
builder.Services.AddSingleton(relayTransportOptions);
builder.Services.AddSingleton<IRelayHostKey>(sp => new FileRelayHostKey(
    builder.Configuration["Agnes:Transport:Relay:KeyFile"] ?? Path.Combine(agnesHome, "relay-host-key.pem"),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<FileRelayHostKey>()));
// The host cert Kestrel presents on the relay path. Selection (Agnes:Transport:Relay:Cert): a real-CA
// Let's-Encrypt cert via DNS-01 when configured (the client validates the CA chain + hostname), else the
// persistent self-signed default (the client pins its fingerprint). An unconfigured host is unchanged (AC1).
var relayHostId = relayTransportOptions.HostId;
var acmeHostCertConfigured = HostCertificateSelection.IsAcmeConfigured(builder.Configuration, relayHostId);
builder.Services.AddSingleton<IHostCertificateProvider>(sp => HostCertificateSelection.Create(
    builder.Configuration, relayHostId, agnesHome,
    new HttpClient(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<ITransportProvider>(sp => new AgnesRelayTransportProvider(
    sp.GetRequiredService<RelayTransportOptions>(),
    sp.GetRequiredService<IRelayHostKey>(),
    sp.GetRequiredService<IHostCertificateProvider>(),
    logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger<AgnesRelayTransportProvider>()));
builder.Services.AddPluginPoint<ITransportProvider>(t => t.Id);

// Auto-renew the ACME cert before expiry (only when the real-CA path is active).
if (acmeHostCertConfigured)
{
    builder.Services.AddHostedService(sp => new HostCertificateRenewalService(
        (AcmeDns01HostCertificateProvider)sp.GetRequiredService<IHostCertificateProvider>(),
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<HostCertificateRenewalService>()));
}

// Present the host cert on the HTTPS listener via a selector (so an ACME renewal swaps the served cert without
// a restart). This is not relay-specific: a direct listener has the same problem, and it is the one that had no
// good answer. A self-hosted host has no CA, so a phone either talks cleartext — which Android forbids outright
// — or fails trust until someone hand-installs a certificate. Serving this cert everywhere lets a client pin its
// fingerprint from the pairing QR instead, so HTTPS works out of the box with nothing installed.
//
// The enterprise path is unaffected: when an ACME/real-CA cert is configured, that is what gets served here and
// clients validate it by chain and name, with no pinning involved.
builder.Services.AddOptions<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>()
    .Configure<IHostCertificateProvider>((kestrel, cert) =>
        kestrel.ConfigureHttpsDefaults(https => https.ServerCertificateSelector = (_, _) => cert.GetCertificate()));

// CORS for a browser-hosted frontend (Uno WASM) reaching the hub cross-origin. The web client
// served from this same origin needs no CORS; only configure origins when it's hosted elsewhere.
//   Agnes:AllowedOrigins  — comma/space-separated allowlist (recommended for cross-origin).
//   Agnes:AllowAllOrigins — dev only: reflect any origin (unsafe on a public network).
var allowedOrigins = (builder.Configuration["Agnes:AllowedOrigins"] ?? string.Empty)
    .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var allowAllOrigins = builder.Configuration.GetValue("Agnes:AllowAllOrigins", false);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    policy.AllowAnyHeader().AllowAnyMethod().AllowCredentials();
    if (allowAllOrigins)
    {
        policy.SetIsOriginAllowed(_ => true);
    }
    else if (allowedOrigins.Length > 0)
    {
        policy.WithOrigins(allowedOrigins);
    }
    else
    {
        // No cross-origin browsers permitted (native clients and the co-hosted web client still work).
        policy.SetIsOriginAllowed(_ => false);
    }
}));

// ---- host identity ----
var displayName = builder.Configuration["Agnes:DisplayName"] ?? Environment.MachineName;
builder.Services.AddSingleton(new HostIdentity(
    HostId: Guid.NewGuid().ToString("n"),
    DisplayName: displayName,
    Version: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.1.0"));

// ---- auth: per-device tokens + a pairing code (see DeviceRegistry) ----
// NB: appsettings.json ships "DevicesFile": "" — an empty string isn't null, so `?? default` wouldn't
// apply and the registry would try to persist to an empty path. Treat blank as unset.
var devicesFile = builder.Configuration["Agnes:DevicesFile"] is { Length: > 0 } configuredDevicesFile
    ? configuredDevicesFile
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "devices.json");
builder.Services.AddSingleton(sp => new DeviceRegistry(
    builder.Configuration["Agnes:PairingToken"], devicesFile,
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<DeviceRegistry>(),
    pairingEnabled: builder.Configuration.GetValue("Agnes:Auth:Pairing:Enabled", true),
    // The typed code closes once a device is paired; an operator who genuinely needs it back (a lab
    // host that is re-paired constantly, say) can opt out of the lockout.
    allowCodeAfterFirstDevice: builder.Configuration.GetValue("Agnes:Auth:Pairing:AllowCodeAfterFirstDevice", false)));
builder.Services.AddSingleton<PairingGrants>();
builder.Services.AddSingleton<PairingApprovals>();

// ---- GitHub SSO (OAuth device flow) — optional strong auth for internet-facing hosts ----
var gitHubAuth = new GitHubAuthOptions
{
    Enabled = builder.Configuration.GetValue("Agnes:Auth:GitHub:Enabled", false),
    ClientId = builder.Configuration["Agnes:Auth:GitHub:ClientId"],
    AllowedUsers = builder.Configuration.GetSection("Agnes:Auth:GitHub:AllowedUsers").Get<string[]>() ?? [],
    AllowedOrgs = builder.Configuration.GetSection("Agnes:Auth:GitHub:AllowedOrgs").Get<string[]>() ?? [],
};
builder.Services.AddSingleton(gitHubAuth);
builder.Services.AddSingleton<IGitHubUserLookup>(_ => new GitHubUserLookup(new HttpClient()));
builder.Services.AddSingleton(sp => new GitHubIdentity(
    sp.GetRequiredService<IGitHubUserLookup>(), gitHubAuth,
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<GitHubIdentity>()));

// ---- keypair auth (SSH authorized_keys style) — optional offline strong auth ----
var keypairAuthOptions = new KeypairAuthOptions
{
    Enabled = builder.Configuration.GetValue("Agnes:Auth:Keypair:Enabled", false),
    AuthorizedKeysFile = builder.Configuration["Agnes:Auth:Keypair:AuthorizedKeysFile"]
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "authorized_keys"),
};
builder.Services.AddSingleton(sp => new KeypairAuth(
    keypairAuthOptions, sp.GetRequiredService<ILoggerFactory>().CreateLogger<KeypairAuth>()));

// ---- OIDC token validation (enterprise) — optional; validates an issuer-signed JWT and mints a device
// token. Fail-closed: enabled-but-incomplete config leaves IsUsable false, so it isn't advertised and the
// exchange endpoint 400s. The interactive authorization-code redirect is out of scope (token-validation
// core only); see .ideas/security/02-enterprise-auth.md. ----
var oidcOptions = new OidcOptions
{
    Enabled = builder.Configuration.GetValue("Agnes:Auth:Oidc:Enabled", false),
    Issuer = builder.Configuration["Agnes:Auth:Oidc:Issuer"],
    Audience = builder.Configuration["Agnes:Auth:Oidc:Audience"],
    JwksJson = builder.Configuration["Agnes:Auth:Oidc:JwksJson"],
    JwksUri = builder.Configuration["Agnes:Auth:Oidc:JwksUri"],
    DisplayName = builder.Configuration["Agnes:Auth:Oidc:DisplayName"] ?? "OIDC",
    // Interactive authorization-code (PKCE) redirect flow (optional; the exchange path needs none of these).
    ClientId = builder.Configuration["Agnes:Auth:Oidc:ClientId"],
    ClientSecret = builder.Configuration["Agnes:Auth:Oidc:ClientSecret"],
    RedirectUri = builder.Configuration["Agnes:Auth:Oidc:RedirectUri"],
    Scopes = builder.Configuration["Agnes:Auth:Oidc:Scopes"] ?? "openid profile email",
    AuthorizationEndpoint = builder.Configuration["Agnes:Auth:Oidc:AuthorizationEndpoint"],
    TokenEndpoint = builder.Configuration["Agnes:Auth:Oidc:TokenEndpoint"],
};
builder.Services.AddSingleton(sp => new OidcIdentity(
    oidcOptions, new HttpClient(), sp.GetRequiredService<ILoggerFactory>().CreateLogger<OidcIdentity>()));
// The interactive authorization-code + PKCE redirect flow that *obtains* the token the validation core
// above consumes. Reuses OidcIdentity for verification and DeviceRegistry for minting; holds its short-lived
// PKCE/nonce state in an in-memory store keyed by the CSRF `state`.
builder.Services.AddSingleton<IOidcStateStore>(sp => new InMemoryOidcStateStore(TimeProvider.System));
builder.Services.AddSingleton(sp => new OidcRedirectFlow(
    sp.GetRequiredService<OidcIdentity>(),
    sp.GetRequiredService<DeviceRegistry>(),
    new HttpClient(),
    sp.GetRequiredService<IOidcStateStore>(),
    TimeProvider.System,
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<OidcRedirectFlow>()));

// ---- Cloudflare Access browser assertion — optional and separate from native OIDC ----
var cloudflareAccessOptions = new CloudflareAccessOptions
{
    Enabled = builder.Configuration.GetValue("Agnes:Auth:CloudflareAccess:Enabled", false),
    TeamDomain = builder.Configuration["Agnes:Auth:CloudflareAccess:TeamDomain"],
    Audience = builder.Configuration["Agnes:Auth:CloudflareAccess:Audience"],
    JwksJson = builder.Configuration["Agnes:Auth:CloudflareAccess:JwksJson"],
    JwksUri = builder.Configuration["Agnes:Auth:CloudflareAccess:JwksUri"],
    AllowedEmailDomains = builder.Configuration.GetSection("Agnes:Auth:CloudflareAccess:AllowedEmailDomains").Get<string[]>() ?? [],
};
if (cloudflareAccessOptions.Enabled && !cloudflareAccessOptions.IsUsable)
{
    throw new InvalidOperationException(
        "Enabled Cloudflare Access requires TeamDomain, Audience, and at least one allowed email domain.");
}
builder.Services.AddSingleton(new CloudflareAccessIdentity(cloudflareAccessOptions, new HttpClient()));

// ---- mTLS client-certificate auth (enterprise) — optional; a certificate that chains to the configured
// CA or matches a pin is the sole credential. Fail-closed the same way as OIDC above. ----
var mtlsOptions = new MtlsOptions
{
    Enabled = builder.Configuration.GetValue("Agnes:Auth:Mtls:Enabled", false),
    TrustAnchorPem = builder.Configuration["Agnes:Auth:Mtls:TrustAnchorPem"],
    PinnedThumbprints = builder.Configuration.GetSection("Agnes:Auth:Mtls:PinnedThumbprints").Get<string[]>() ?? [],
    DisplayName = builder.Configuration["Agnes:Auth:Mtls:DisplayName"] ?? "Client certificate",
};
builder.Services.AddSingleton(sp => new MtlsIdentity(
    mtlsOptions, sp.GetRequiredService<ILoggerFactory>().CreateLogger<MtlsIdentity>()));

// ---- auth methods as built-in plugins (AC13): /auth/methods is driven from this registry ----
builder.Services.AddSingleton<IAuthMethodProvider>(sp => new PairingAuthMethodProvider(sp.GetRequiredService<DeviceRegistry>()));
builder.Services.AddSingleton<IAuthMethodProvider>(sp => new GitHubAuthMethodProvider(sp.GetRequiredService<GitHubIdentity>()));
builder.Services.AddSingleton<IAuthMethodProvider>(sp => new KeypairAuthMethodProvider(sp.GetRequiredService<KeypairAuth>()));
builder.Services.AddSingleton<IAuthMethodProvider>(sp => new OidcAuthMethodProvider(sp.GetRequiredService<OidcIdentity>()));
builder.Services.AddSingleton<IAuthMethodProvider>(sp => new MtlsAuthMethodProvider(sp.GetRequiredService<MtlsIdentity>()));
builder.Services.AddPluginPoint<IAuthMethodProvider>(m => m.MethodId);

// Holds the reachable address the active transport exposed at startup, so the pairing QR/deep-link
// endpoint advertises that (relay/tunnel/public) address rather than a bound LAN one.
builder.Services.AddSingleton<HostReachability>();

// ---- rate limiting: cap the auth bootstrap endpoints per-IP and globally ----
var authRateLimit = new AuthRateLimitOptions
{
    Enabled = builder.Configuration.GetValue("Agnes:Auth:RateLimit:Enabled", true),
    PerIpPerMinute = builder.Configuration.GetValue("Agnes:Auth:RateLimit:PerIpPerMinute", 10),
    GlobalPerMinute = builder.Configuration.GetValue("Agnes:Auth:RateLimit:GlobalPerMinute", 100),
    TrustForwardedFor = builder.Configuration.GetValue("Agnes:Auth:RateLimit:TrustForwardedFor", false),
};
builder.Services.AddRateLimiter(o => AuthRateLimit.Configure(o, authRateLimit));

// ---- MCP server registry (configured from the UI, persisted to ~/.agnes/mcp.json) ----
var mcpFile = builder.Configuration["Agnes:McpFile"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "mcp.json");
builder.Services.AddSingleton(sp => new McpRegistry(
    mcpFile, sp.GetRequiredService<ILoggerFactory>().CreateLogger<McpRegistry>()));

// ---- Local model provider (Copilot BYOK), configured from the UI ----
var localProviderFile = builder.Configuration["Agnes:LocalProviderFile"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "local-provider.json");
builder.Services.AddSingleton(sp => new Agnes.Host.Hosting.LocalProviderRegistry(
    localProviderFile, sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Hosting.LocalProviderRegistry>()));

// Strict vs lenient MCP startup resolution (default lenient): an unresolvable enabled server is either
// skipped-with-a-warning (lenient) or fails the session start naming the server (strict).
builder.Services.AddSingleton(new McpOptions(builder.Configuration.GetValue("Agnes:Mcp:Strict", false)));

// ---- MCP catalogues as plugins (AC13): where installable MCP servers come from ----
// The curated handful is built in and needs no network. Breadth comes from registry plugins, shipped in-tree
// the way the agent packages are: each is its own assembly implementing IMcpCatalogProvider, and each can be
// turned off by an operator who doesn't want that egress (Agnes:Registries:<id>:Enabled=false).
builder.Services.AddSingleton<IMcpCatalogProvider, CuratedMcpCatalogProvider>();
if (RegistryEnabled(builder.Configuration, "mcp-official"))
{
    builder.Services.AddSingleton<IMcpCatalogProvider>(
        new Agnes.Registries.McpRegistry.OfficialMcpRegistryProvider(new HttpClient()));
}

builder.Services.AddPluginPoint<IMcpCatalogProvider>(p => p.Id);

// ---- projects: per-repo bundles (sandbox + MCP + GitHub account + defaults) a session inherits ----
var projectsFile = builder.Configuration["Agnes:ProjectsFile"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "projects.json");
builder.Services.AddSingleton(sp => new Agnes.Host.Projects.ProjectStore(
    projectsFile, sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Projects.ProjectStore>()));

// ---- checkouts: this host's on-disk clones/worktrees + their lifecycle (multi-machine workspace model,
//      connectivity/05). A separate store from projects (working copies vs. per-repo session config); the
//      manager reuses GitService's clone/worktree/branch/status primitives rather than reinventing git. ----
var checkoutsFile = builder.Configuration["Agnes:CheckoutsFile"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "checkouts.json");
builder.Services.AddSingleton<Agnes.Host.Git.GitService>();
builder.Services.AddSingleton(sp => new Agnes.Host.Projects.CheckoutStore(
    checkoutsFile, sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Projects.CheckoutStore>()));
builder.Services.AddSingleton(sp => new Agnes.Host.Git.CheckoutManager(
    sp.GetRequiredService<Agnes.Host.Git.GitService>(),
    sp.GetRequiredService<Agnes.Host.Projects.CheckoutStore>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Git.CheckoutManager>()));

// ---- review comments: file+line feedback anchored to a project, durable across sessions ----
var reviewCommentsFile = builder.Configuration["Agnes:ReviewCommentsFile"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "review-comments.json");
builder.Services.AddSingleton(sp => new Agnes.Host.Projects.ReviewCommentStore(
    reviewCommentsFile, sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Projects.ReviewCommentStore>()));

// ---- prompt library: host-persisted saved prompts + slash-token templates ("stop retyping prompts") ----
var promptLibraryDir = builder.Configuration["Agnes:PromptLibraryDir"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes");
builder.Services.AddSingleton(sp => new Agnes.Host.Hosting.PromptLibrary(
    promptLibraryDir, sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Hosting.PromptLibrary>()));

// ---- launch profiles (providers/04): named, reusable new-session launch configs ----
var launchProfilesDir = builder.Configuration["Agnes:LaunchProfilesDir"] ?? promptLibraryDir;
builder.Services.AddSingleton(sp => new Agnes.Host.Hosting.LaunchProfileStore(
    launchProfilesDir, sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Hosting.LaunchProfileStore>()));

// ---- skill bundles + external registries (extensibility/02): a SKILL.md + supporting files as one unit ----
// The library owns managed copies (source of truth); registries are explicit, tracked import sources.
var skillLibraryDir = builder.Configuration["Agnes:SkillLibraryDir"] ?? promptLibraryDir;
builder.Services.AddSingleton(sp => new Agnes.Host.Hosting.SkillLibrary(
    skillLibraryDir, sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Hosting.SkillLibrary>()));

// External skill registries as a plugin point. A configured local directory / local-git checkout is the
// offline reference source (NO network — Agnes reads the working tree). The network-backed registries are
// separate plugin assemblies: the official anthropics/skills repository over GitHub, and skillshub.wtf's
// index across thousands of published skills. Each is opt-out per operator
// (Agnes:Registries:<id>:Enabled=false) since each reaches the internet.
var skillRegistryDir = builder.Configuration["Agnes:SkillRegistryDir"];
if (!string.IsNullOrWhiteSpace(skillRegistryDir))
{
    builder.Services.AddSingleton<IPromptRegistryProvider>(new Agnes.Host.Hosting.LocalDirectoryRegistryProvider(skillRegistryDir));
}

// A github.com token, when the host has one linked, only raises GitHub's anonymous API rate limit; both
// registries work without it. Resolved through the same broker a NuGet-installed plugin would be handed.
static Agnes.Registries.GitHub.GitHubSkillBundles GitHubBundles(IServiceProvider sp)
{
    var sources = sp.GetService<Agnes.Host.Hosting.CredentialSourceRegistry>();
    var broker = sources is null ? null : new Agnes.Host.Plugins.HostCredentialBroker(sources);
    return new Agnes.Registries.GitHub.GitHubSkillBundles(
        new HttpClient(),
        broker is null ? null : ct => broker.ResolveAsync("github.com", ct));
}

if (RegistryEnabled(builder.Configuration, "github:anthropics/skills"))
{
    builder.Services.AddSingleton<IPromptRegistryProvider>(sp =>
        new Agnes.Registries.GitHub.GitHubSkillsRegistryProvider(GitHubBundles(sp)));
}

if (RegistryEnabled(builder.Configuration, "skillshub"))
{
    builder.Services.AddSingleton<IPromptRegistryProvider>(sp =>
        new Agnes.Registries.SkillsHub.SkillsHubRegistryProvider(new HttpClient(), GitHubBundles(sp)));
}

builder.Services.AddPluginPoint<IPromptRegistryProvider>(p => p.Id);

// ---- connected services: named, multi-profile provider credentials (.ideas/providers/02) ----
// A parallel surface to the sandbox git-credential broker (NOT a replacement): a user connects a provider
// account once, names it as a ConnectedServiceProfile, and any host can materialise a short-lived credential
// for it just-in-time. The template provider is a harmless placeholder stub (no network) that proves the
// plugin point end-to-end; a real provider (GitHub/Linear/…) is added as another IConnectedServiceProvider
// with NO change to the broker. The profile store holds identity/routing only — never a secret.
var connectedServicesDir = builder.Configuration["Agnes:ConnectedServicesDir"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes");
builder.Services.AddSingleton(sp => new Agnes.Host.Hosting.ConnectedServiceProfileStore(
    connectedServicesDir, sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Hosting.ConnectedServiceProfileStore>()));
var templateServiceSecret = builder.Configuration["Agnes:ConnectedServices:Template:Token"];
builder.Services.AddSingleton<IConnectedServiceProvider>(_ =>
    new Agnes.Host.Hosting.TemplateConnectedServiceProvider(
        secretLookup: _ => templateServiceSecret ?? "template-placeholder-token"));

// The first REAL connected-service provider + quota reporter (.ideas/providers/03): Claude usage via
// Anthropic's OAuth usage endpoint, using the host's existing ~/.claude/.credentials.json access token
// (never the refresh token). Config-gated: usable when a Claude credential is present on the host, or when
// explicitly forced on. A profile with ProviderId "claude" resolves to it; the template stub still backs
// other/unknown profiles.
var claudeHomeDir = builder.Configuration["Agnes:Quota:Claude:HomeDir"]
    ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var claudeCredsPath = Path.Combine(claudeHomeDir, ".claude", ".credentials.json");
var claudeQuotaEnabled = builder.Configuration.GetValue<bool?>("Agnes:Quota:Claude:Enabled")
    ?? File.Exists(claudeCredsPath);
if (claudeQuotaEnabled)
{
    builder.Services.AddSingleton<IConnectedServiceProvider>(sp =>
    {
        var tokens = new Agnes.Host.Hosting.ClaudeOAuthTokenSource(claudeHomeDir);
        return new Agnes.Host.Hosting.ClaudeQuotaProvider(
            new HttpClient(),
            tokens.ReadAccessTokenAsync,
            sp.GetRequiredService<TimeProvider>(),
            logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Hosting.ClaudeQuotaProvider>());
    });
}

builder.Services.AddPluginPoint<IConnectedServiceProvider>(p => p.Id);
builder.Services.AddSingleton(sp => new Agnes.Host.Hosting.ConnectedServiceBroker(
    sp.GetRequiredService<Agnes.Host.Hosting.ConnectedServiceProfileStore>(),
    sp.GetRequiredService<IPluginRegistry<IConnectedServiceProvider>>()));
// Quota/usage monitoring (.ideas/providers/03): a caching layer over the OPTIONAL IQuotaReportingProvider
// capability. A provider that exposes usage (the template stub does) is read at most once per staleness
// window and served from cache; one that doesn't implement the capability reports quota as "unavailable".
var quotaStalenessSeconds = builder.Configuration.GetValue("Agnes:Quota:StalenessSeconds", 300);
builder.Services.AddSingleton(sp => new Agnes.Host.Hosting.QuotaService(
    sp.GetRequiredService<Agnes.Host.Hosting.ConnectedServiceProfileStore>(),
    sp.GetRequiredService<IPluginRegistry<IConnectedServiceProvider>>(),
    sp.GetRequiredService<TimeProvider>(),
    TimeSpan.FromSeconds(quotaStalenessSeconds),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Hosting.QuotaService>()));

// ---- external attention requests (extensibility/06): the generic human-in-the-loop webhook API ----
// A public REST surface ( /v1/attention-requests ) lets any external system create a "please ask a human"
// entry that surfaces in the SAME approvals inbox as internal session permissions; the answer is delivered
// back via a callback POST (retried with bounded backoff) and/or polling. Persisted so answers survive a
// restart; the timeout sweeper (below) expires unanswered ones. TimeProvider.System is injected so the
// clock is a seam under test.
builder.Services.AddSingleton(TimeProvider.System);
var attentionFile = builder.Configuration["Agnes:AttentionRequestsFile"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "attention-requests.json");
builder.Services.AddSingleton(sp => new Agnes.Host.Attention.AttentionRequestStore(
    attentionFile, sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Attention.AttentionRequestStore>()));
builder.Services.AddSingleton(sp => new Agnes.Host.Attention.AttentionCallbackPoster(
    new HttpClient(),
    maxAttempts: builder.Configuration.GetValue("Agnes:Attention:CallbackMaxAttempts", 5),
    logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Attention.AttentionCallbackPoster>()));
builder.Services.AddSingleton(sp => new Agnes.Host.Attention.AttentionRequestService(
    sp.GetRequiredService<Agnes.Host.Attention.AttentionRequestStore>(),
    sp.GetRequiredService<Agnes.Host.Attention.AttentionCallbackPoster>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Attention.AttentionRequestService>()));
builder.Services.AddHostedService(sp => new Agnes.Host.Attention.AttentionTimeoutSweeper(
    sp.GetRequiredService<Agnes.Host.Attention.AttentionRequestService>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Attention.AttentionTimeoutSweeper>()));

// ---- generic approval-gated actions (notifications/02 tier 2) ----
// Consequential actions (a git commit, a brokered credential share) invoked from a gated surface become
// durable ApprovalRequests unioned into the same inbox as tier 1. Persisted so an open request survives a
// restart. The gating table is built from config, defaulting to EMPTY — i.e. every surface is ungated and
// existing commit/credential behaviour is unchanged until a gate is explicitly configured. Config shape:
//   "Agnes:Approvals:Gated": [ { "ActionId": "git.commit", "Surface": "SessionAgent" }, ... ]
var approvalsFile = builder.Configuration["Agnes:ApprovalRequestsFile"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "approval-requests.json");
builder.Services.AddSingleton(sp => new Agnes.Host.Approvals.ApprovalRequestStore(
    approvalsFile, sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Approvals.ApprovalRequestStore>()));
builder.Services.AddSingleton(sp =>
{
    var gated = builder.Configuration.GetSection("Agnes:Approvals:Gated")
        .Get<List<Agnes.Host.Approvals.GatedSurfaceConfig>>() ?? [];
    return new Agnes.Host.Approvals.ApprovalGate(gated
        .Where(g => !string.IsNullOrWhiteSpace(g.ActionId))
        .Select(g => (g.ActionId!, g.Surface)));
});
builder.Services.AddSingleton(sp => new Agnes.Host.Approvals.ApprovalGateService(
    sp.GetRequiredService<Agnes.Host.Approvals.ApprovalGate>(),
    sp.GetRequiredService<Agnes.Host.Approvals.ApprovalRequestStore>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Approvals.ApprovalGateService>()));
// ---- collaborators & social + explicit access grants (collaboration/01) ----
// A GitHub-verified collaborator directory, live eligibility (shared org/team OR explicit collaborator, recomputed on
// every check — never cached as trust), and explicit, revocable access grants enforced by ICollaboratorAuthorizer.
// Reuses the security/02 GitHub identity/membership lookup for all live checks. The grant + authorizer pair
// is the seam collaboration/02 session-sharing consumes. See .ideas/collaboration/01-collaborators-and-social.md.
var socialDir = builder.Configuration["Agnes:SocialDir"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes");
builder.Services.AddSingleton(sp => new Agnes.Host.Social.CollaboratorStore(
    socialDir, sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Social.CollaboratorStore>()));
builder.Services.AddSingleton(sp => new Agnes.Host.Social.GrantStore(
    socialDir, sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Social.GrantStore>()));
builder.Services.AddSingleton(sp => new Agnes.Host.Social.CollaboratorEligibilityService(
    sp.GetRequiredService<Agnes.Host.Social.CollaboratorStore>(),
    sp.GetRequiredService<IGitHubUserLookup>(),
    sp.GetRequiredService<GitHubAuthOptions>()));
builder.Services.AddSingleton<Agnes.Host.Social.ICollaboratorAuthorizer>(sp => new Agnes.Host.Social.CollaboratorAuthorizer(
    sp.GetRequiredService<Agnes.Host.Social.GrantStore>(),
    sp.GetRequiredService<IGitHubUserLookup>()));
builder.Services.AddSingleton(sp => new Agnes.Host.Social.CollaboratorService(
    sp.GetRequiredService<Agnes.Host.Social.CollaboratorStore>(),
    sp.GetRequiredService<Agnes.Host.Social.GrantStore>(),
    sp.GetRequiredService<Agnes.Host.Social.CollaboratorEligibilityService>(),
    sp.GetRequiredService<IGitHubUserLookup>(),
    sp.GetRequiredService<TimeProvider>()));

// ---- session sharing & public links (collaboration/02) ----
// Two deliberately-separate stores persisted alongside the social ledger; a share IS a session-scoped grant,
// a public link is always view-only by construction. Enforcement is host-side (AgnesHub), never UI-only.
builder.Services.AddSingleton(sp => new Agnes.Host.Sharing.SessionShareStore(
    socialDir, sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Sharing.SessionShareStore>()));
builder.Services.AddSingleton(sp => new Agnes.Host.Sharing.PublicLinkStore(
    socialDir, sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Sharing.PublicLinkStore>()));
builder.Services.AddSingleton<Agnes.Host.Sharing.ISessionActivityProbe>(sp =>
    new Agnes.Host.Sharing.SessionManagerActivityProbe(sp.GetRequiredService<Agnes.Host.Sessions.SessionManager>()));
builder.Services.AddSingleton(sp => new Agnes.Host.Sharing.SessionSharingService(
    sp.GetRequiredService<Agnes.Host.Sharing.SessionShareStore>(),
    sp.GetRequiredService<Agnes.Host.Sharing.PublicLinkStore>(),
    sp.GetRequiredService<Agnes.Host.Sharing.ISessionActivityProbe>(),
    Uri.TryCreate(builder.Configuration["Agnes:PublicBaseUrl"], UriKind.Absolute, out var publicBase) ? publicBase : null));
builder.Services.AddSingleton(sp => new Agnes.Host.Sharing.SessionAccessAuthorizer(
    sp.GetRequiredService<Agnes.Host.Sharing.SessionShareStore>(),
    sp.GetService<Agnes.Host.Groups.GroupMembershipService>(),
    sp.GetService<Agnes.Host.Sessions.SessionSecurityOptions>()));
builder.Services.AddSingleton<Agnes.Host.Sharing.PublicViewerTracker>();

// ---- managed-sandbox registry: persisted so stopped/closed VMs stay visible (resume/delete) across restarts ----
var sandboxesFile = builder.Configuration["Agnes:SandboxesFile"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "sandboxes.json");
builder.Services.AddSingleton(sp => new Agnes.Host.Sessions.SandboxRegistry(
    sandboxesFile, sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Sessions.SandboxRegistry>()));

// ---- event store: selected from a registry of built-in providers (AC13) ----
// The backends are registered as built-in IEventStoreProvider plugins; the host picks one by name.
//
// Deployment topology (ops/03): the store choice IS the topology choice.
//   * Default / single-node: SQLite (a file on disk) — or in-memory when no database path is set. This is
//     the right shape for one host = one daemon on one machine: durable, ordered, single-writer, zero
//     operational overhead. A zero-config deployment behaves exactly as it always has.
//   * Optional scaled / shared-database: Postgres, selected with Agnes:Storage:EventStore=postgres and a
//     Agnes:Storage:Postgres:ConnectionString. This lets the event log live in a shared server (e.g. so a
//     scaled/multi-instance host, or a future relay, can point at one logical store) without any storage
//     code changing. v1 keeps a single logical store — no sharding.
//
// Selection is per-store, so the memory-index (and any other durable store) could later gain a Postgres
// backing through the same seam without touching core. Name resolution + provider set live in one testable
// helper (EventStoreSelection) so Program.cs and the tests share a single source of truth.
var databasePath = builder.Configuration["Agnes:Database"];
var eventStoreName = EventStoreSelection.ResolveName(builder.Configuration);
foreach (var provider in EventStoreSelection.BuildProviders(builder.Configuration))
{
    builder.Services.AddSingleton(provider);
}
builder.Services.AddSingleton<IPluginRegistry<IEventStoreProvider>>(sp =>
    new PluginRegistry<IEventStoreProvider>(sp.GetServices<IEventStoreProvider>(), p => p.Name));

// ---- memory search index (see .ideas/ops/02-memory-search.md) ----
// A per-host index over every session's transcript, exposed as a plugin point so an alternative provider can
// be added later without touching core. The SQLite provider shares the event store's SQLite file, so it only
// exists when a durable database is configured (an in-memory host has no file to index). When present, the
// event store is wrapped so every append is indexed.
//
// Two tiers, one provider: FTS5 keyword search is always on; an optional embedding-backed SEMANTIC tier
// switches on only when Agnes:Search:Embeddings:Provider is openai|local (default none = FTS5-only, exactly
// as before). The embedding generator is built through Microsoft.Extensions.AI's provider-neutral
// IEmbeddingGenerator seam and is only ever constructed when embeddings are enabled, so the default host
// never loads the OpenAI connector. With embeddings on, keyword + semantic hits are fused (RRF).
if (!string.IsNullOrWhiteSpace(databasePath))
{
    var embeddingGenerator = EmbeddingSelection.Build(builder.Configuration);
    builder.Services.AddSingleton<IMemoryIndexProvider>(new SqliteMemoryIndexProvider(databasePath, embeddingGenerator));
}
builder.Services.AddPluginPoint<IMemoryIndexProvider>(p => p.Id);

builder.Services.AddSingleton<IEventStore>(sp =>
{
    var registry = sp.GetRequiredService<IPluginRegistry<IEventStoreProvider>>();
    var provider = registry.Find(eventStoreName)
        ?? throw new InvalidOperationException(
            $"No event-store provider named '{eventStoreName}' is registered (have: {string.Join(", ", registry.All.Select(p => p.Name))}).");
    var store = provider.Create();

    var memoryIndex = sp.GetRequiredService<IPluginRegistry<IMemoryIndexProvider>>().All.FirstOrDefault();
    return memoryIndex is null
        ? store
        : new IndexingEventStore(store, memoryIndex, sp.GetRequiredService<ILoggerFactory>().CreateLogger<IndexingEventStore>());
});

// ---- event spine (see .ideas/00d-event-spine-and-ui-extensibility.md) ----
// One host bus. Plugin event bindings are applied to it via the merger below, so a plugin can observe or
// intercept/cancel host actions (e.g. veto a prompt); unbinding happens on plugin disable/uninstall.
builder.Services.AddSingleton<Agnes.Abstractions.Events.IEventBus>(sp =>
    new Agnes.Abstractions.Events.EventBus(ex =>
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("Agnes.Events").LogError(ex, "An event observer threw.")));
builder.Services.AddSingleton<Agnes.Host.Plugins.IPluginPointMerger>(sp =>
    new Agnes.Host.Plugins.EventBindingMerger(sp.GetRequiredService<Agnes.Abstractions.Events.IEventBus>()));

// ---- broadcast + session manager ----
builder.Services.AddSingleton<Agnes.Host.Hosting.ClientCapabilityStore>();
builder.Services.AddSingleton<ISessionBroadcaster, SignalRBroadcaster>();
// The real host-side CLI-fallback: a genuine pseudo-terminal (Porta.Pty) backing the embedded terminal and
// provider-login flows (platform/03). SessionManager resolves it as its optional ICliFallback.
builder.Services.AddSingleton<ICliFallback, Agnes.Host.Sessions.PortaPtyCliFallback>();

// ---- session security guardrails (Agnes:Security:*) ----
// Opt-in defence-in-depth for a shared / multi-tenant host: confine every session's working directory to a set
// of allowed roots, and/or refuse to run any session outside a sandbox. Both default off (today's behaviour).
// Enforced centrally in SessionManager's open path, so every entry (new / fork / cross-host handoff) is covered
// regardless of what a client sends.
builder.Services.AddSingleton(new Agnes.Host.Sessions.SessionSecurityOptions
{
    EnforceIsolationPolicy = !builder.Environment.IsDevelopment(),
    WorkloadTrust = Enum.TryParse<Agnes.Host.Sessions.WorkloadTrust>(
        builder.Configuration["Agnes:Security:WorkloadTrust"], ignoreCase: true, out var workloadTrust)
            ? workloadTrust
            : builder.Environment.IsDevelopment()
                ? Agnes.Host.Sessions.WorkloadTrust.Trusted
                : Agnes.Host.Sessions.WorkloadTrust.Untrusted,
    AcknowledgeSharedKernelRisk = builder.Configuration.GetValue("Agnes:Security:AcknowledgeSharedKernelRisk", false),
    AllowedSessionRoots = builder.Configuration.GetSection("Agnes:Security:AllowedSessionRoots").Get<string[]>() ?? [],
    RequireSandbox = builder.Configuration.GetValue("Agnes:Security:RequireSandbox", false),
    RequirePermissionPrompts = builder.Configuration.GetValue("Agnes:Security:RequirePermissionPrompts", false),
    AllowUnsandboxedSkipPermissions = builder.Configuration.GetValue("Agnes:Security:AllowUnsandboxedSkipPermissions", false),
    AllowedHostMcpServers = builder.Configuration.GetSection("Agnes:Security:AllowedHostMcpServers").Get<string[]>() ?? [],
    HostMcpPolicy = Enum.TryParse<Agnes.Host.Sessions.HostMcpPolicy>(
        builder.Configuration["Agnes:Security:HostMcpPolicy"], ignoreCase: true, out var hostMcpPolicy)
            ? hostMcpPolicy
            : Agnes.Host.Sessions.HostMcpPolicy.Legacy,
    SessionIsolation = Enum.TryParse<Agnes.Host.Sessions.SessionIsolation>(
        builder.Configuration["Agnes:Security:SessionIsolation"], ignoreCase: true, out var iso) ? iso : Agnes.Host.Sessions.SessionIsolation.Shared,
    RestrictConfigToOwner = builder.Configuration.GetValue("Agnes:Security:RestrictConfigToOwner", false),
    MaxConcurrentSandboxes = builder.Configuration.GetValue("Agnes:Security:MaxConcurrentSandboxes", 0),
    TranscriptRetentionDays = builder.Configuration.GetValue("Agnes:Security:TranscriptRetentionDays", 0),
});
// ---- stalled-turn auto-continue (Agnes:AutoContinue:*) ----
// Some agents end a turn reporting a normal completion while having produced nothing actionable — no
// assistant message, no tool call, just reasoning (observed with OpenCode against a weak model: its agent
// loop exits without error, and ACP has no way to say "I gave up"). The stall is ALWAYS surfaced as a
// notice; this only controls whether the host also re-prompts, and how many times before it gives up.
builder.Services.AddSingleton(new Agnes.Host.Sessions.AutoContinueOptions
{
    Enabled = builder.Configuration.GetValue("Agnes:AutoContinue:Enabled", true),
    MaxAttempts = builder.Configuration.GetValue("Agnes:AutoContinue:MaxAttempts", 2),
    Prompt = builder.Configuration["Agnes:AutoContinue:Prompt"] is { Length: > 0 } p
        ? p
        : new Agnes.Host.Sessions.AutoContinueOptions().Prompt,
});
builder.Services.AddHostedService<Agnes.Host.Sessions.UsageReporter>();
builder.Services.AddHostedService<Agnes.Host.Events.TranscriptRetentionService>();
builder.Services.AddSingleton<SessionManager>();

// ---- Agnes AS an MCP server (see .ideas/voice/01-voice-assistant.md) ----
// The reverse of Agnes's MCP *management* feature (where Agnes consumes other MCP servers): here Agnes exposes
// its OWN actions as MCP tools over Streamable HTTP, so the OpenAI Realtime voice endpoint — or any MCP client
// — can drive it. Every tool is a thin wrapper over the SAME SessionManager path a paired client uses and is
// gated on a valid Agnes device token per call, so voice/MCP carries no new authority. Always available to an
// authenticated client; the OpenAI realtime service below only activates once a key is configured.
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<Agnes.Host.Mcp.TranscriptPrivacyFilter>();
builder.Services.AddSingleton<Agnes.Host.Mcp.IAgnesMcpBackend>(sp => new Agnes.Host.Mcp.SessionManagerMcpBackend(
    sp.GetRequiredService<SessionManager>(),
    sp.GetRequiredService<Agnes.Host.Mcp.TranscriptPrivacyFilter>(),
    sp.GetRequiredService<SessionGoalManager>()));
// ---- Agnes's own MCP endpoint, offered to sandboxed agents over the sandbox bridge ----
// Plain HTTP on purpose: the address is the bridge gateway, unreachable from anywhere but the sandboxes,
// which is the same containment the credential broker and MCP forward already rely on. Terminating TLS here
// would mean trusting a self-signed host certificate inside every guest. Opt-in: with no bind address there
// is no extra listener and no agnes server is offered to any agent.
var guestMcpBind = builder.Configuration["Agnes:Sandbox:GuestMcpBindUrl"];
var guestMcpUrl = builder.Configuration["Agnes:Sandbox:GuestMcpUrl"];
if (!string.IsNullOrWhiteSpace(guestMcpBind))
{
    // Append rather than replace: calling UseUrls with only this address would drop the main TLS listener.
    var existing = builder.Configuration["urls"]
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
    builder.WebHost.UseUrls(Agnes.Host.Mcp.GuestMcpEndpoint.CombineUrls(existing, guestMcpBind));
}

builder.Services.AddSingleton(new Agnes.Host.Sessions.GuestMcpOptions
{
    Url = string.IsNullOrWhiteSpace(guestMcpBind) ? null : guestMcpUrl,
    BindUrl = guestMcpBind,
});
builder.Services.AddSingleton<Agnes.Host.Mcp.SessionMcpTokens>();

builder.Services.AddSingleton<Agnes.Host.Mcp.IMcpDeviceAuthenticator>(sp =>
    new Agnes.Host.Mcp.DeviceRegistryMcpAuthenticator(sp.GetRequiredService<DeviceRegistry>()));
builder.Services.AddSingleton<Agnes.Host.Mcp.IMcpCallerTokenSource, Agnes.Host.Mcp.HttpContextMcpTokenSource>();
builder.Services
    .AddMcpServer()
    // Stateless: each tool call is its own HTTP POST, so the device token is re-authenticated on EVERY call
    // (matching the SignalR hub's per-connection check but at per-request granularity).
    .WithHttpTransport(o => o.Stateless = true)
    .WithTools<Agnes.Host.Mcp.AgnesMcpTools>();

// The OpenAI Realtime voice service: assembles the realtime session config pointing the model at THIS host's
// Agnes MCP endpoint. Config-gated — usable only when Agnes:Voice:OpenAI:ApiKey is set (the operator plugs in
// their own key later). The API key stays a secret: it's never logged and never placed in the session config.
builder.Services.AddSingleton(sp => Agnes.Host.Mcp.VoiceRealtimeOptions.FromConfiguration(
    builder.Configuration,
    defaultMcpEndpointUrl: builder.Configuration["Agnes:Voice:OpenAI:McpEndpointUrl"] is { Length: > 0 } url
        ? url
        : Agnes.Host.Mcp.AgnesMcpEndpoints.Path));
builder.Services.AddSingleton<Agnes.Host.Mcp.OpenAiRealtimeVoiceService>();

// ---- channel bridges (see .ideas/extensibility/04-channel-bridges.md) ----
// A bridge (Slack/Discord/WhatsApp/…) is a plugin. The notifier observes the spine to push permission requests
// to linked chats; the router funnels an authorized inbound "allow" through the same approval path a paired
// client uses. Both are eagerly resolved at startup so their subscriptions bind. A concrete IChannelBridge
// (with its transport) registers as an AddSingleton<IChannelBridge> before the plugin point below and needs no
// change to any of this. Each real bridge is CONFIG-GATED: it is only registered when its credential block is
// present under Agnes:Channels:<Bridge>:*, so an unconfigured bridge simply doesn't exist (its webhook 404s).
// All tokens/secrets come from host settings — see appsettings / FromConfiguration on each options record.
if (Agnes.Host.Channels.SlackBridgeOptions.FromConfiguration(builder.Configuration) is { } slackOptions)
{
    builder.Services.AddSingleton<IChannelBridge>(sp => new Agnes.Host.Channels.SlackBridge(
        new HttpClient(), slackOptions, sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Channels.SlackBridge>()));
}

if (Agnes.Host.Channels.DiscordBridgeOptions.FromConfiguration(builder.Configuration) is { } discordOptions)
{
    builder.Services.AddSingleton<IChannelBridge>(sp => new Agnes.Host.Channels.DiscordBridge(
        new HttpClient(), discordOptions, sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Channels.DiscordBridge>()));
}

if (Agnes.Host.Channels.WhatsAppBridgeOptions.FromConfiguration(builder.Configuration) is { } whatsAppOptions)
{
    builder.Services.AddSingleton<IChannelBridge>(sp => new Agnes.Host.Channels.WhatsAppBridge(
        new HttpClient(), whatsAppOptions,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Channels.WhatsAppBridge>()));
}

builder.Services.AddPluginPoint<IChannelBridge>(b => b.Id);
var channelLinksFile = builder.Configuration["Agnes:ChannelLinksFile"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "channel-links.json");
builder.Services.AddSingleton(sp => new Agnes.Host.Channels.ChannelLinkStore(
    channelLinksFile, sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Channels.ChannelLinkStore>()));
builder.Services.AddSingleton<Agnes.Host.Channels.ChannelPromptTracker>();
builder.Services.AddSingleton(sp => new Agnes.Host.Channels.ChannelBridgeNotifier(
    sp.GetRequiredService<Agnes.Abstractions.Events.IEventBus>(),
    sp.GetRequiredService<IPluginRegistry<IChannelBridge>>(),
    sp.GetRequiredService<Agnes.Host.Channels.ChannelLinkStore>(),
    sp.GetRequiredService<Agnes.Host.Channels.ChannelPromptTracker>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Channels.ChannelBridgeNotifier>()));
builder.Services.AddSingleton(sp => new Agnes.Host.Channels.ChannelBridgeRouter(
    sp.GetRequiredService<IPluginRegistry<IChannelBridge>>(),
    sp.GetRequiredService<Agnes.Host.Channels.ChannelLinkStore>(),
    sp.GetRequiredService<Agnes.Host.Channels.ChannelPromptTracker>(),
    sp.GetRequiredService<SessionManager>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Channels.ChannelBridgeRouter>()));

// ---- push notifications (notifications/01) ----
// A host-side INotificationChannel plugin point: the "reach a device that isn't connected" surface, sitting
// alongside the channel bridges (they consume the same trigger set). Two channels ship in-box — a "desktop"
// channel wrapping the existing OS-notifier path, and a "mobile-push" TEMPLATE stub (no-op; wire FCM/APNs
// there). The dispatcher observes the spine (mirroring ChannelBridgeNotifier) and fans a minimized payload out
// to eligible devices; the push registration store is keyed by device id and dropped when a pairing is revoked
// (it observes DeviceRevokedEvent). The action router is the untrusted-host guard for interactive taps.
builder.Services.AddSingleton<Agnes.Host.Notifications.IDesktopNotificationSink>(sp =>
    new Agnes.Host.Notifications.LoggingDesktopNotificationSink(
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Notifications.LoggingDesktopNotificationSink>()));
builder.Services.AddSingleton<INotificationChannel>(sp =>
    new Agnes.Host.Notifications.DesktopNotificationChannel(sp.GetRequiredService<Agnes.Host.Notifications.IDesktopNotificationSink>()));
// The mobile channel: a REAL FCM sender ("fcm") when this deployment supplies its own service-account
// credential in settings (bring-your-own — no shared relay), otherwise the no-op TEMPLATE stub ("mobile-push")
// so there is always a mobile channel for dev/tests. The credential comes from Agnes:Push:Fcm:ServiceAccountJson
// (inline) or Agnes:Push:Fcm:ServiceAccountFile (a path to the JSON). The FirebaseFcmSender is constructed only
// when a credential is present, so a deployment with no FCM setup never touches FirebaseAdmin. NOTE: the app-side
// (Android/iOS) FCM SDK registration is the client half and is out of scope here.
var fcmServiceAccountJson = builder.Configuration["Agnes:Push:Fcm:ServiceAccountJson"];
var fcmServiceAccountFile = builder.Configuration["Agnes:Push:Fcm:ServiceAccountFile"];
if (string.IsNullOrWhiteSpace(fcmServiceAccountJson)
    && !string.IsNullOrWhiteSpace(fcmServiceAccountFile)
    && File.Exists(fcmServiceAccountFile))
{
    fcmServiceAccountJson = File.ReadAllText(fcmServiceAccountFile);
}

if (!string.IsNullOrWhiteSpace(fcmServiceAccountJson))
{
    var fcmJson = fcmServiceAccountJson;
    builder.Services.AddSingleton<INotificationChannel>(sp =>
        new Agnes.Host.Notifications.FcmPushChannel(
            new Agnes.Host.Notifications.FirebaseFcmSender(fcmJson),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Notifications.FcmPushChannel>()));
}
else
{
    builder.Services.AddSingleton<INotificationChannel>(sp =>
        new Agnes.Host.Notifications.TemplateMobilePushChannel(
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Notifications.TemplateMobilePushChannel>()));
}

builder.Services.AddPluginPoint<INotificationChannel>(c => c.Id);
var pushRegistrationsFile = builder.Configuration["Agnes:PushRegistrationsFile"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "push-registrations.json");
builder.Services.AddSingleton(sp => new Agnes.Host.Notifications.PushRegistrationStore(
    pushRegistrationsFile,
    sp.GetRequiredService<Agnes.Abstractions.Events.IEventBus>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Notifications.PushRegistrationStore>()));
builder.Services.AddSingleton<Agnes.Host.Notifications.ActiveSessionViewTracker>();
builder.Services.AddSingleton(sp => new Agnes.Host.Notifications.PushNotificationDispatcher(
    sp.GetRequiredService<Agnes.Abstractions.Events.IEventBus>(),
    sp.GetRequiredService<IPluginRegistry<INotificationChannel>>(),
    sp.GetRequiredService<Agnes.Host.Notifications.PushRegistrationStore>(),
    sp.GetRequiredService<Agnes.Host.Notifications.ActiveSessionViewTracker>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Notifications.PushNotificationDispatcher>()));
builder.Services.AddSingleton(sp => new Agnes.Host.Notifications.PushActionRouter(
    sp.GetRequiredService<DeviceRegistry>(),
    sp.GetRequiredService<SessionManager>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Notifications.PushActionRouter>()));

// ---- scheduled / background tasks + inbox ----
// Automation triggers are built-in plugins (AC13): interval + cron ship in-box, with a merger so a
// webhook (or other) trigger can be added as a plugin without touching the scheduler.
builder.Services.AddSingleton<IAutomationTrigger, IntervalAutomationTrigger>();
builder.Services.AddSingleton<IAutomationTrigger, CronAutomationTrigger>();
builder.Services.AddPluginPoint<IAutomationTrigger>(t => t.Kind);
var scheduledTasksFile = builder.Configuration["Agnes:ScheduledTasksFile"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "scheduled-tasks.json");
builder.Services.AddSingleton(sp => new ScheduledTaskManager(
    sp.GetRequiredService<IPluginRegistry<IAutomationTrigger>>(),
    sp.GetRequiredService<Agnes.Abstractions.Events.IEventBus>(),
    scheduledTasksFile));
builder.Services.AddHostedService<ScheduledRunner>();

// ---- standing goals: nudge a session that goes quiet with work still owed ----
// Idle-triggered rather than scheduled, so a working agent is never talked over and a stopped one is picked
// up quickly. Bounded by a nudge budget plus an optional expiry, because an agent able to arm unbounded
// self-prompting is a runaway. Persisted, so an armed goal survives a host restart.
builder.Services.AddSingleton(sp => new SessionGoalManager(
    builder.Configuration["Agnes:GoalsFile"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "session-goals.json")));
builder.Services.AddHostedService<GoalWatcher>();

// ---- liveness watchdog: say when a turn stops getting anywhere ----
// Agnes could previously only tell a live agent from a dead one. A process that stays up with its stream
// open but stops producing looks exactly like one hard at work — forever. This reports (never restarts):
// a wedged turn may still recover, and throwing away visible work is worse than saying so.
builder.Services.AddSingleton(new LivenessOptions
{
    Enabled = builder.Configuration.GetValue("Agnes:Liveness:Enabled", true),
    QuietLimit = TimeSpan.FromMinutes(
        builder.Configuration.GetValue("Agnes:Liveness:QuietMinutes", (int)SessionLiveness.DefaultQuietLimit.TotalMinutes)),
});
builder.Services.AddHostedService<LivenessWatchdog>();

// ---- agent adapters (plugins) ----
builder.Services.AddSingleton<IAgentAdapter>(sp =>
{
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var options = new ClaudeCodeOptions
    {
        Command = builder.Configuration["Agnes:ClaudeCode:Command"] ?? "npx",
        Arguments = builder.Configuration.GetSection("Agnes:ClaudeCode:Args").Get<string[]>()
                    ?? [],
    };
    return ClaudeCodeAgent.Create(loggerFactory, options);
});

// OpenCode ships native ACP (`opencode acp`) — no bridge needed.
builder.Services.AddSingleton<IAgentAdapter>(sp =>
{
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var options = new OpenCodeOptions
    {
        Command = builder.Configuration["Agnes:OpenCode:Command"] ?? "opencode",
        Arguments = builder.Configuration.GetSection("Agnes:OpenCode:Args").Get<string[]>() ?? ["acp"],
    };
    return OpenCodeAgent.Create(loggerFactory, options);
});

// OpenCode via its NATIVE HTTP server, offered alongside the ACP adapter — the same both-paths shape
// Claude Code has. The native surface reports failures and retries the ACP one cannot express, but it is
// OpenCode's internal API rather than a versioned spec, so ACP stays the default.
builder.Services.AddSingleton<IAgentAdapter>(sp => Agnes.Agents.OpenCode.Native.OpenCodeNativeAgent.Create(
    sp.GetRequiredService<ILoggerFactory>(),
    new Agnes.Agents.OpenCode.Native.OpenCodeNativeOptions
    {
        Command = builder.Configuration["Agnes:OpenCodeNative:Command"] ?? "opencode",
    }));

// Claude Code via its NATIVE SDK (stream-json), offered alongside the ACP adapter.
builder.Services.AddSingleton<IAgentAdapter>(sp => Agnes.Agents.Native.ClaudeCodeNative.Create(
    sp.GetRequiredService<ILoggerFactory>(),
    builder.Configuration["Agnes:ClaudeCodeNative:Command"],
    builder.Configuration.GetSection("Agnes:ClaudeCodeNative:Args").Get<string[]>()));

// Codex via its NATIVE app-server (persistent JSON-RPC stdio), the interactive Codex fit.
builder.Services.AddSingleton<IAgentAdapter>(sp => Agnes.Agents.Codex.CodexAppServer.Create(
    sp.GetRequiredService<ILoggerFactory>(),
    builder.Configuration["Agnes:Codex:Command"],
    builder.Configuration.GetSection("Agnes:Codex:Args").Get<string[]>(),
    builder.Configuration.GetValue("Agnes:Codex:EnableUserInput", false)));

// GitHub Copilot CLI, which ships native ACP (`copilot --acp`) — a launch descriptor, no bridge.
// BYOK is environment-only on Copilot's side, so the provider block is read here and rendered to the
// COPILOT_PROVIDER_* variables by the adapter; without a base URL it stays inert and Copilot uses
// GitHub's own model routing.
builder.Services.AddSingleton<IAgentAdapter>(sp =>
{
    var provider = builder.Configuration.GetSection("Agnes:Copilot:Provider");
    var options = new Agnes.Agents.Copilot.CopilotOptions
    {
        Command = builder.Configuration["Agnes:Copilot:Command"] ?? "copilot",
        Arguments = builder.Configuration.GetSection("Agnes:Copilot:Args").Get<string[]>() ?? ["--acp"],
        FleetMode = builder.Configuration.GetValue("Agnes:Copilot:FleetMode", false),
        SubagentNames = builder.Configuration.GetSection("Agnes:Copilot:SubagentNames").Get<string[]>()
            ?? Agnes.Agents.Copilot.CopilotSubagentSettings.ModelPinningAgents,
        // Withheld tools. When a custom provider is configured this defaults to the recommended set
        // rather than empty: apply_patch is offered as an OpenAI *custom* tool with a Lark grammar, and a
        // server that implements only function tools rejects the entire request, so the default that
        // "works" against GitHub's own models is the default that cannot start against a local one.
        // "Agnes:Copilot:ExcludedTools": [] switches it off explicitly.
        ExcludedTools = sp.GetRequiredService<Agnes.Host.Hosting.LocalProviderRegistry>().ExcludedTools() is { Count: > 0 } fromUi
            ? fromUi
            : builder.Configuration.GetSection("Agnes:Copilot:ExcludedTools").Get<string[]>()
                ?? (provider["BaseUrl"] is { Length: > 0 }
                    ? Agnes.Agents.Copilot.CopilotLocalCompatibility.RecommendedExcludedTools
                    : []),
        // No GitHub at all: no authentication, telemetry, web tools, GitHub MCP or auto-update. Ignored
        // unless a provider is configured, since Copilot needs something to infer against.
        Offline = sp.GetRequiredService<Agnes.Host.Hosting.LocalProviderRegistry>().Offline
            || builder.Configuration.GetValue("Agnes:Copilot:Offline", false),
        // The UI-configured provider wins over appsettings, and is read on each launch rather than at
        // host start, so changing it in settings takes effect on the next session without a restart.
        Provider = sp.GetRequiredService<Agnes.Host.Hosting.LocalProviderRegistry>().ProviderOptions()
            ?? (provider["BaseUrl"] is { Length: > 0 } baseUrl
            ? new Agnes.Agents.Copilot.CopilotProviderOptions
            {
                BaseUrl = baseUrl,
                Type = provider.GetValue("Type", Agnes.Agents.Copilot.CopilotProviderType.OpenAi),
                ApiKey = provider["ApiKey"],
                BearerToken = provider["BearerToken"],
                WireApi = provider.GetValue("WireApi", Agnes.Agents.Copilot.CopilotWireApi.Completions),
                Transport = provider.GetValue("Transport", Agnes.Agents.Copilot.CopilotTransport.Http),
                AzureApiVersion = provider["AzureApiVersion"],
                Headers = provider.GetSection("Headers").Get<string[]>() ?? [],
                Model = provider["Model"],
                ModelId = provider["ModelId"],
                WireModel = provider["WireModel"],
                MaxPromptTokens = provider.GetValue<int?>("MaxPromptTokens"),
                MaxOutputTokens = provider.GetValue<int?>("MaxOutputTokens"),
            }
            : null),
    };
    return Agnes.Agents.Copilot.CopilotAgent.Create(sp.GetRequiredService<ILoggerFactory>(), options);
});

// Pi via its RPC mode (`pi --mode rpc`), a bidirectional JSONL protocol — Pi ships no ACP. Chosen for
// unattended work: Pi retries a failed provider call at the agent-turn level rather than dropping the
// turn. It has no permission protocol at all, so the adapter refuses an attended session outright.
builder.Services.AddSingleton<IAgentAdapter>(sp => Agnes.Agents.Pi.PiAgent.Create(
    sp.GetRequiredService<ILoggerFactory>(),
    new Agnes.Agents.Pi.PiOptions
    {
        Command = builder.Configuration["Agnes:Pi:Command"] ?? "pi",
        Arguments = builder.Configuration.GetSection("Agnes:Pi:Args").Get<string[]>() ?? ["--mode", "rpc"],
    }));

// Antigravity (`agy`) via its stream-json print mode — Google ships no ACP for it. Unlike CodeyBox's
// one-shot `agy --print` per prompt, this holds ONE process open and feeds it a JSON line per turn, so
// the conversation stays in the CLI's memory between turns rather than being reconstructed by --continue.
// Autonomous only, and the adapter enforces that: without --dangerously-skip-permissions the CLI does not
// ask, it silently writes to a scratch directory and claims success.
builder.Services.AddSingleton<IAgentAdapter>(sp => Agnes.Agents.Antigravity.AntigravityAgent.Create(
    sp.GetRequiredService<ILoggerFactory>(),
    new Agnes.Agents.Antigravity.AntigravityOptions
    {
        Command = builder.Configuration["Agnes:Antigravity:Command"] ?? "agy",
        Arguments = builder.Configuration.GetSection("Agnes:Antigravity:Args").Get<string[]>()
            ?? ["--input-format", "stream-json", "--output-format", "stream-json"],
        PrintTimeout = TimeSpan.FromSeconds(
            builder.Configuration.GetValue<int?>("Agnes:Antigravity:PrintTimeoutSeconds") ?? 1800),
    }));

// User-configured extra ACP backends (Agnes:CustomBackends): a "bring your own ACP CLI" path so a
// host operator can point Agnes at any ACP-speaking binary from config alone — no new package. Each
// valid entry becomes a generic AcpAgentAdapter joining the same registry as the built-ins. Fail-closed
// per entry: a malformed/invalid/duplicate entry is skipped (logged) and the host still starts.
using (var customBackendsLog = LoggerFactory.Create(lb =>
    lb.AddConfiguration(builder.Configuration.GetSection("Logging")).AddConsole()))
{
    Agnes.Host.CustomBackends.Register(builder.Services, builder.Configuration,
        customBackendsLog.CreateLogger("Agnes.Host.CustomBackends"));
}

// A typed, DI-resolvable registry over every IAgentAdapter above — the plugin-point pattern every
// new provider interface in this backlog follows (see .ideas/00-plugin-architecture.md). Built once
// all the AddSingleton<IAgentAdapter> registrations above have run; consumed by SessionManager
// (Find-by-id instead of a hand-rolled dictionary) and by capability negotiation (GetCapabilities).
// Registered once as the concrete type, then exposed under both the read-only and mutable interfaces
// so IPluginInstaller can merge a NuGet-installed plugin's adapters into the exact same instance
// SessionManager already resolves — no separate "plugin adapters" list to keep in sync.
builder.Services.AddPluginPoint<IAgentAdapter>(a => a.Descriptor.Id);

// ---- sandboxing (opt-in) ----
// When Agnes:Sandbox:Provider=incus, agents run inside per-session Incus VMs with their
// credentials materialised in. Off by default: no behaviour change unless configured.
if (string.Equals(builder.Configuration["Agnes:Sandbox:Provider"], "incus", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton(sp => new Agnes.Sandbox.Incus.IncusSandboxProvider(
        new Agnes.Sandbox.Incus.IncusOptions
        {
            BinaryPath = builder.Configuration["Agnes:Sandbox:Incus:Binary"] ?? "incus",
            ProjectName = builder.Configuration["Agnes:Sandbox:Incus:Project"] ?? "agnes",
            StoragePoolName = builder.Configuration["Agnes:Sandbox:Incus:StoragePool"] ?? "default",
            DefaultImage = builder.Configuration["Agnes:Sandbox:Incus:Image"] ?? "images:ubuntu/24.04/cloud",
            Bridge = builder.Configuration["Agnes:Sandbox:Incus:Bridge"] ?? "incusbr0",
            // Default VM resource caps, overridable per project. Config is in friendly units (CPU cores,
            // RAM in GiB, disk in GiB); unset keeps the 2 / 12 / 16 defaults.
            DefaultLimits = SandboxLimitsFromConfig(builder.Configuration),
            NetworkAcls = builder.Configuration.GetSection("Agnes:Sandbox:Incus:NetworkAcls").Get<string[]>() ?? [],
            NetworkProfiles = builder.Configuration.GetSection("Agnes:Sandbox:Incus:NetworkProfiles").Get<Dictionary<string, string>>() ?? new(),
            DefaultNetworkProfile = builder.Configuration["Agnes:Sandbox:Incus:NetworkProfile"],
        },
        sp.GetRequiredService<ILoggerFactory>()));
    builder.Services.AddSingleton<Agnes.Sandbox.ISandboxProvider>(
        sp => sp.GetRequiredService<Agnes.Sandbox.Incus.IncusSandboxProvider>());

    builder.Services.AddSingleton<Agnes.Sandbox.Credentials.ClaudeCredentialProvider>();
    builder.Services.AddSingleton<Agnes.Sandbox.Credentials.IAgentCredentialProvider>(
        sp => sp.GetRequiredService<Agnes.Sandbox.Credentials.ClaudeCredentialProvider>());

    // OpenCode's provider key. Without it a sandboxed OpenCode can only reach the credential-free
    // providers and, rather than saying so, silently streams from a model the user did not choose.
    builder.Services.AddSingleton<Agnes.Sandbox.Credentials.IAgentCredentialProvider>(
        sp => new Agnes.Sandbox.Credentials.OpenCodeCredentialProvider(
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Sandbox.Credentials.OpenCodeCredentialProvider>()));

    builder.Services.AddSingleton<Agnes.Sandbox.Credentials.ClaudeTokenRotationPusher>();

    // MCP forward proxy: lets a sandboxed agent reach RunAt=Host MCP servers running on this host,
    // over the sandbox bridge (token-gated, bound only to the bridge address).
    builder.Services.AddSingleton<Agnes.Host.Hosting.McpForwardRegistry>();
    var mcpBridge = builder.Configuration["Agnes:Sandbox:Incus:Bridge"] ?? "incusbr0";
    var mcpHostAddress = ResolveBridgeAddress(builder.Configuration["Agnes:Sandbox:Incus:HostAddress"], mcpBridge);
    if (mcpHostAddress is not null)
    {
        var mcpPort = int.TryParse(builder.Configuration["Agnes:Sandbox:Incus:McpPort"], System.Globalization.CultureInfo.InvariantCulture, out var configuredPort) ? configuredPort : 0;
        builder.Services.AddSingleton(sp =>
        {
            var listener = new Agnes.Host.Hosting.McpForwardListener(
                sp.GetRequiredService<Agnes.Host.Hosting.McpForwardRegistry>(),
                mcpHostAddress, mcpPort, mcpHostAddress.ToString(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Hosting.McpForwardListener>());
            listener.Start();
            return listener;
        });
    }

    // Git credential broker: lets a sandboxed agent authenticate a git push without holding any
    // secret — its git helper calls back to this listener (over the bridge, token-gated), which mints
    // a short-lived, repo-scoped credential on the host. Same bridge-bound shape as the MCP forward.
    builder.Services.AddSingleton<Agnes.Host.Hosting.CredentialBrokerRegistry>();
    if (mcpHostAddress is not null)
    {
        var gitPort = int.TryParse(builder.Configuration["Agnes:Sandbox:Incus:GitPort"], System.Globalization.CultureInfo.InvariantCulture, out var configuredGitPort) ? configuredGitPort : 0;
        builder.Services.AddSingleton(sp =>
        {
            var listener = new Agnes.Host.Hosting.CredentialBrokerListener(
                sp.GetRequiredService<Agnes.Host.Hosting.CredentialBrokerRegistry>(),
                sp.GetRequiredService<Agnes.Host.Hosting.CredentialSourceRegistry>(),
                mcpHostAddress, gitPort, mcpHostAddress.ToString(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Hosting.CredentialBrokerListener>());
            listener.Start();
            return listener;
        });
    }

    // Baked baseline image: the daemon bakes it (packages + agent CLIs) so sandboxed sessions start
    // complete. Auto-baked on first use if missing; rebuilt when the UI manifest is saved.
    builder.Services.AddSingleton<Agnes.Sandbox.ISandboxImageBuilder>(
        sp => sp.GetRequiredService<Agnes.Sandbox.Incus.IncusSandboxProvider>());
    var imageManifestFile = builder.Configuration["Agnes:Sandbox:ImageManifest"]
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "sandbox-image.json");
    builder.Services.AddSingleton(sp => new Agnes.Host.Sessions.SandboxImageManager(
        sp.GetRequiredService<Agnes.Sandbox.ISandboxImageBuilder>(), imageManifestFile,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Sessions.SandboxImageManager>()));
}

// A typed, DI-resolvable registry over every ISandboxProvider configured above (today: zero or one —
// "Agnes:Sandbox:Provider" selects which backend is active). Registered unconditionally so the plugin
// pattern (and capability negotiation) holds even when no sandbox backend is configured (AC2/AC3).
builder.Services.AddPluginPoint<Agnes.Sandbox.ISandboxProvider>(p => p.Name);

// Credential sources + the Connect-GitHub flow are always available (a user can link GitHub before
// they ever open a sandbox); the broker above only consumes what's registered here.
builder.Services.AddSingleton<Agnes.Host.Hosting.CredentialSourceRegistry>();
builder.Services.AddSingleton(_ => new Agnes.Host.Hosting.GitHubAppStore());
builder.Services.AddSingleton(sp => new Agnes.Host.Hosting.GitHubConnectFlow(
    sp.GetRequiredService<Agnes.Host.Hosting.GitHubAppStore>(),
    sp.GetRequiredService<Agnes.Host.Hosting.CredentialSourceRegistry>(),
    new HttpClient(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Hosting.GitHubConnectFlow>()));

// ---- GitHub connected-service provider (.ideas/providers/02, the first REAL provider) ----
// Registered alongside (and as unconditionally as) the GitHub credential infrastructure above — it is the
// connected-services analogue of that always-available machinery and self-gates at resolve time, minting a
// short-lived credential ONLY when a GitHub App is actually linked or a token configured, and otherwise
// failing loud. It reuses the SAME sources as the sandbox git path (no new GitHub OAuth flow): a linked App
// mints a short-lived installation token; a stored PAT/OAuth token (from config or the runtime
// /credentials/token endpoint) is the fallback. The App private key and any refresh material stay inside
// those sources and NEVER cross into the resolved credential. The template provider stays registered too.
var gitHubConnectedServiceToken = builder.Configuration["Agnes:ConnectedServices:GitHub:Token"];
var gitHubConnectedServiceHttp = new HttpClient();
builder.Services.AddSingleton<IConnectedServiceProvider>(sp =>
{
    var appStore = sp.GetRequiredService<Agnes.Host.Hosting.GitHubAppStore>();
    var registry = sp.GetRequiredService<Agnes.Host.Hosting.CredentialSourceRegistry>();
    return new Agnes.Host.Hosting.GitHubConnectedServiceProvider(
        appSource: () => appStore.List().Any(a => a.InstallationId != 0)
            ? new Agnes.Host.Hosting.GitHubAppCredentialSource(() => appStore.List(), gitHubConnectedServiceHttp)
            : null,
        storedSource: () =>
        {
            if (!string.IsNullOrWhiteSpace(gitHubConnectedServiceToken))
            {
                return new Agnes.Host.Hosting.StoredTokenCredentialSource(
                    Agnes.Host.Hosting.GitHubConnectedServiceProvider.GitHubHost, gitHubConnectedServiceToken);
            }

            // A token stored at runtime via /credentials/token lives only in the registry; use it only when it
            // is specifically a stored-token source (the App source, if present, was already tried above).
            return registry.For(Agnes.Host.Hosting.GitHubConnectedServiceProvider.GitHubHost)
                is Agnes.Host.Hosting.StoredTokenCredentialSource storedSource
                ? storedSource
                : null;
        });
});

// ---- git forges as built-in plugins (AC13): GitHub today, GitLab/Bitbucket addable as plugins ----
// The GitHub provider lists PRs anonymously for public repos, and mints a repo-scoped token via the linked
// GitHub App (through the credential source) when one is available for private repos / higher rate limits.
builder.Services.AddSingleton<IGitHostProvider>(sp =>
{
    var sources = sp.GetService<Agnes.Host.Hosting.CredentialSourceRegistry>();
    return new GitHubGitHostProvider(
        new HttpClient(),
        async (host, repo, ct) =>
        {
            var source = sources?.For(host);
            if (source is null)
            {
                return null;
            }

            var credential = await source.ResolveAsync(
                new Agnes.Sandbox.Credentials.CredentialRequest("https", host, repo, "get"), ct).ConfigureAwait(false);
            return credential?.Password;
        });
});
builder.Services.AddPluginPoint<IGitHostProvider>(p => p.Id);

// ---- group membership as a plugin point (session isolation, PerGroup) ----
// Shipped backend: GitHub repo write-access == group membership, where a session's group id is its repo key.
// Other backends (LDAP, SSO teams, a static roster) register as additional IGroupProvider plugins without
// touching core. The write-access check reuses the linked GitHub App to mint a token and query the
// collaborator-permission API; with no App linked it simply grants no group access.
var groupWriteAccessHttp = new HttpClient();
builder.Services.AddSingleton<Agnes.Host.Groups.IGitHubRepoWriteAccess>(sp =>
{
    var appStore = sp.GetService<Agnes.Host.Hosting.GitHubAppStore>();
    var app = appStore is not null
        ? new Agnes.Host.Hosting.GitHubAppCredentialSource(() => appStore.List(), groupWriteAccessHttp)
        : null;
    return new Agnes.Host.Groups.GitHubApiRepoWriteAccess(
        groupWriteAccessHttp,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Groups.GitHubApiRepoWriteAccess>(),
        app);
});
builder.Services.AddSingleton<Agnes.Host.Groups.IGroupProvider, Agnes.Host.Groups.GitHubRepoGroupProvider>();
builder.Services.AddPluginPoint<Agnes.Host.Groups.IGroupProvider>(p => p.Id);
builder.Services.AddSingleton<Agnes.Host.Groups.GroupMembershipService>();

// ---- owner-only diagnostics: recent-log ring + crash/error telemetry (see .ideas/ops/01-...) ----
// Agnes keeps no on-disk log to scrape, so a bounded in-memory ring captures the tail of the host log for the
// owner-only, opt-in diagnostic bundle (and nothing else). The provider tees the same lines the console shows
// into the ring. A second bounded ring records recent errors, fed by the event spine (AgentErrorEvent) and by
// the process-level exception handlers wired after the app is built.
var hostLogBuffer = new Agnes.Host.Ops.HostLogRingBuffer(
    builder.Configuration.GetValue("Agnes:BugReports:LogBufferLines", 500));
builder.Services.AddSingleton(hostLogBuffer);
builder.Logging.AddProvider(new Agnes.Host.Ops.RingBufferLoggerProvider(hostLogBuffer));
builder.Services.AddSingleton(_ => new Agnes.Host.Ops.ErrorTelemetryStore(
    builder.Configuration.GetValue("Agnes:BugReports:ErrorBufferSize", 100)));

// ---- bug-report sinks as built-in plugins (see .ideas/ops/01-bug-reports-and-diagnostics.md) ----
// GitHubIssueSink is always available (prefilled browser fallback with no token; API create/duplicate-search
// with one); CustomEndpointSink is added only when a self-hoster configures an endpoint. The default sink is
// selected by which config is present, unless pinned by Agnes:BugReports:Sink.
// The owner-only host-log DiagnosticPayload attachment is gated by DiagnosticAttachmentPolicy below; by
// default (AttachDiagnostics off) reports still carry a null payload.
var bugReportRepo = builder.Configuration["Agnes:BugReports:Repo"] ?? "AdamFrisby/Agnes";
var bugReportToken = builder.Configuration["Agnes:BugReports:GitHubToken"] ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
var bugReportEndpoint = builder.Configuration["Agnes:BugReports:Endpoint"];
var bugReportMaxBytes = builder.Configuration.GetValue("Agnes:BugReports:MaxPayloadBytes", 1024L * 1024L);
builder.Services.AddSingleton<IBugReportSink>(sp => new Agnes.Host.Ops.GitHubIssueSink(
    new HttpClient(), bugReportRepo, bugReportToken,
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Ops.GitHubIssueSink>()));
if (!string.IsNullOrWhiteSpace(bugReportEndpoint))
{
    builder.Services.AddSingleton<IBugReportSink>(sp => new Agnes.Host.Ops.CustomEndpointSink(
        new HttpClient(), bugReportEndpoint, bugReportMaxBytes, TimeSpan.FromSeconds(30),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Ops.CustomEndpointSink>()));
}

builder.Services.AddPluginPoint<IBugReportSink>(s => s.Id);
var defaultBugSink = builder.Configuration["Agnes:BugReports:Sink"]
    ?? (string.IsNullOrWhiteSpace(bugReportEndpoint) ? "github-issue" : "custom-endpoint");

// Diagnostic collector: assembles the owner-only bundle (host/runtime metadata, adapter list, recent errors,
// recent host log), capped at the same byte budget the sinks enforce.
builder.Services.AddSingleton(sp => new Agnes.Host.Ops.DiagnosticCollector(
    sp.GetRequiredService<Agnes.Host.Ops.HostLogRingBuffer>(),
    sp.GetRequiredService<Agnes.Host.Ops.ErrorTelemetryStore>(),
    sp.GetRequiredService<HostIdentity>(),
    () => sp.GetRequiredService<SessionManager>().ListAgents().Select(a => a.AdapterId).ToArray(),
    bugReportMaxBytes));

// Attachment policy: the sensitive host-log bundle is attached only when the operator enabled the capability
// (Agnes:BugReports:AttachDiagnostics, off by default) AND the submitting caller is the host owner.
var attachDiagnosticsEnabled = builder.Configuration.GetValue("Agnes:BugReports:AttachDiagnostics", false);
builder.Services.AddSingleton(sp => new Agnes.Host.Ops.DiagnosticAttachmentPolicy(
    attachDiagnosticsEnabled,
    callerId => sp.GetRequiredService<DeviceRegistry>().IsOwner(callerId)));

builder.Services.AddSingleton(sp => new Agnes.Host.Ops.BugReportRouter(
    sp.GetRequiredService<IPluginRegistry<IBugReportSink>>(), defaultBugSink,
    sp.GetRequiredService<Agnes.Host.Ops.DiagnosticCollector>(),
    sp.GetRequiredService<Agnes.Host.Ops.DiagnosticAttachmentPolicy>()));

// ---- plugin installer: NuGet-packaged third-party plugins (see .ideas/00-plugin-architecture.md) ----
// The scoped service a plugin gets when it declares (and is granted) the "credentials" capability.
builder.Services.AddSingleton<ICredentialBroker>(sp =>
    new Agnes.Host.Plugins.HostCredentialBroker(sp.GetRequiredService<Agnes.Host.Hosting.CredentialSourceRegistry>()));
builder.Services.AddSingleton(sp => new Agnes.Host.Plugins.PluginCapabilityService(
    PluginCapabilityIds.Credentials,
    (pluginServices, hostServices) => pluginServices.AddSingleton(hostServices.GetRequiredService<ICredentialBroker>())));

var pluginTrustOptions = new PluginTrustOptions();
builder.Configuration.GetSection("Agnes:Plugins").Bind(pluginTrustOptions);
var pluginTrustPolicy = builder.Environment.IsDevelopment()
    ? PluginTrustPolicy.Development
    : PluginTrustPolicy.CreateProduction(pluginTrustOptions);
var pluginSources = pluginTrustOptions.Sources;
builder.Services.AddSingleton(pluginTrustPolicy);
builder.Services.AddSingleton<Agnes.Host.Plugins.INuGetPluginFeed>(
    _ => new Agnes.Host.Plugins.NuGetProtocolFeed(pluginSources));

var allowUnsignedPlugins = builder.Configuration.GetValue("Agnes:Plugins:AllowUnsignedPackages", false);
if (!builder.Environment.IsDevelopment() && allowUnsignedPlugins)
{
    throw new InvalidOperationException(
        "Agnes:Plugins:AllowUnsignedPackages may only be enabled in Development.");
}
builder.Services.AddSingleton<Agnes.Host.Plugins.IPluginPackageVerifier>(sp =>
    new Agnes.Host.Plugins.NuGetSignatureVerifier(allowUnsignedPlugins,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Plugins.NuGetSignatureVerifier>()));

var pluginsRoot = builder.Configuration["Agnes:Plugins:Directory"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "plugins");
var pluginStateFile = builder.Configuration["Agnes:Plugins:StateFile"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "plugins.json");
builder.Services.AddSingleton(sp => new Agnes.Host.Plugins.PluginStateStore(
    pluginStateFile, sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Plugins.PluginStateStore>()));

builder.Services.AddSingleton<Agnes.Host.Plugins.PluginInstaller>(sp => new Agnes.Host.Plugins.PluginInstaller(
    sp.GetRequiredService<Agnes.Host.Plugins.INuGetPluginFeed>(),
    sp.GetRequiredService<Agnes.Host.Plugins.IPluginPackageVerifier>(),
    sp.GetRequiredService<Agnes.Host.Plugins.PluginStateStore>(),
    pluginsRoot,
    sp,
    sp.GetServices<Agnes.Host.Plugins.IPluginPointMerger>(),
    sp.GetServices<Agnes.Host.Plugins.PluginCapabilityService>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Plugins.PluginInstaller>(),
    sp.GetRequiredService<PluginTrustPolicy>()));
builder.Services.AddSingleton<IPluginInstaller>(sp => sp.GetRequiredService<Agnes.Host.Plugins.PluginInstaller>());

// The wire-facing adapter the SignalR hub delegates to (DTO mapping + consent-exception → typed outcome).
builder.Services.AddSingleton(sp => new Agnes.Host.Plugins.PluginManagementService(
    sp.GetRequiredService<IPluginInstaller>(),
    sp.GetRequiredService<Agnes.Abstractions.Events.IEventBus>()));


var app = builder.Build();

// FIRST in the pipeline on purpose. This port is plaintext and reachable from every sandbox, so the
// path restriction has to run before anything else can respond on it: registered later, authentication
// answers /agnes with a 401 instead, which both leaks that the hub is there and would serve it outright
// to anyone holding a device token. Everything but the MCP endpoint is refused here.
if (Agnes.Host.Mcp.GuestMcpEndpoint.TryGetPort(guestMcpBind) is { } guestPort)
{
    app.Use(async (ctx, next) =>
    {
        if (ctx.Connection.LocalPort == guestPort
            && !Agnes.Host.Mcp.GuestMcpEndpoint.IsAllowedPath(ctx.Request.Path.Value))
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next();
    });
}

// Eagerly instantiate the rotation pusher (its FileSystemWatcher starts in the ctor) so live
// sandboxes get refreshed credentials when the host claude CLI rotates its OAuth token.
_ = app.Services.GetService<Agnes.Sandbox.Credentials.ClaudeTokenRotationPusher>();

// Eagerly start the credential broker listener (Start() binds the socket) and the GitHub connect
// flow (its ctor re-registers the minting source if an App is already linked + installed).
_ = app.Services.GetService<Agnes.Host.Hosting.CredentialBrokerListener>();
_ = app.Services.GetService<Agnes.Host.Hosting.GitHubConnectFlow>();
// Start the MCP forward listener (if sandboxing + a bridge address are configured).
_ = app.Services.GetService<Agnes.Host.Hosting.McpForwardListener>();

// Restore the session catalogue so sessions (and their history) survive a host restart.
await app.Services.GetRequiredService<SessionManager>().RestoreAsync();

// Eagerly bind the channel-bridge notifier (spine observer) and router (inbound subscriptions) so a linked
// bridge starts delivering/accepting immediately, not lazily on first use.
_ = app.Services.GetRequiredService<Agnes.Host.Channels.ChannelBridgeNotifier>();
_ = app.Services.GetRequiredService<Agnes.Host.Channels.ChannelBridgeRouter>();

// Eagerly bind the push registration store (subscribes to DeviceRevokedEvent) and the dispatcher (spine
// observer) so a revoked pairing drops its push registration and triggers start paging immediately.
_ = app.Services.GetRequiredService<Agnes.Host.Notifications.PushRegistrationStore>();
_ = app.Services.GetRequiredService<Agnes.Host.Notifications.PushNotificationDispatcher>();

// Restore and verify enabled plugins before exposing any transport. In Production this rebuilds from
// locally cached, SHA-512-pinned package archives and fails startup if provenance is incomplete.
await app.Services.GetRequiredService<Agnes.Host.Plugins.PluginInstaller>().RestoreEnabledPluginsAsync();

var tokens = app.Services.GetRequiredService<DeviceRegistry>();
// The host event spine — auth endpoints emit observe-only audit events (device paired/revoked) on it so a
// plugin can react (notify, log) without the security-critical DeviceRegistry taking an async dependency.
var authEvents = app.Services.GetRequiredService<Agnes.Abstractions.Events.IEventBus>();

// Bind crash/error telemetry: observe agent/adapter faults on the spine and capture process-level failures
// (unhandled exceptions, unobserved task exceptions). Recorded in-memory only — nothing is sent anywhere; the
// ring merely feeds the owner-only, opt-in diagnostic bundle. The Observe handle lives for the app's lifetime.
var errorTelemetry = app.Services.GetRequiredService<Agnes.Host.Ops.ErrorTelemetryStore>();
_ = authEvents.Observe(errorTelemetry);
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    errorTelemetry.Record("unhandled", (e.ExceptionObject as Exception)?.Message ?? "unknown unhandled exception");
TaskScheduler.UnobservedTaskException += (_, e) =>
    errorTelemetry.Record("unobserved-task", e.Exception.Message);

// Optionally serve a web frontend (e.g. the Uno WASM build) from the same origin as
// the hub — avoids cross-origin setup and lets a browser reach both on one port.
var webRoot = builder.Configuration["Agnes:WebRoot"];
Microsoft.Extensions.FileProviders.IFileProvider? webFiles =
    string.IsNullOrEmpty(webRoot) ? null : new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(webRoot));

app.UseCors();

if (webFiles is not null)
{
    // The WASM client ships framework assets with extensions the default MIME map doesn't know
    // (.dat ICU data, .blat, .dll). Map the known ones and serve the rest as binary so the app boots.
    var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
    contentTypes.Mappings[".wasm"] = "application/wasm";
    contentTypes.Mappings[".dat"] = "application/octet-stream";
    contentTypes.Mappings[".blat"] = "application/octet-stream";
    contentTypes.Mappings[".dll"] = "application/octet-stream";
    contentTypes.Mappings[".pdb"] = "application/octet-stream";
    contentTypes.Mappings[".webmanifest"] = "application/manifest+json";

    var staticOptions = new StaticFileOptions
    {
        FileProvider = webFiles,
        ContentTypeProvider = contentTypes,
        ServeUnknownFileTypes = true,
        DefaultContentType = "application/octet-stream",
        // Always revalidate the HTML shell and the two bootstrap modules. If an Access session expires,
        // a cached shell would keep trying to load protected framework assets and surface misleading
        // CORS/service-worker errors instead of taking the user through the gateway again. The bootstrap
        // modules are deliberately not immutable: Uno keeps their package directory stable, so a browser
        // must not reuse an older PWA-enabled config after the server changes it for an Access-gated UI.
        OnPrepareResponse = context =>
        {
            if (string.Equals(context.File.Name, "index.html", StringComparison.OrdinalIgnoreCase)
                || string.Equals(context.File.Name, "uno-bootstrap.js", StringComparison.OrdinalIgnoreCase)
                || string.Equals(context.File.Name, "uno-config.js", StringComparison.OrdinalIgnoreCase))
            {
                context.Context.Response.Headers.CacheControl = "no-store";
            }
        },
    };
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = webFiles });
    app.UseStaticFiles(staticOptions);
}

// Throttle the auth bootstrap endpoints (per-IP + global); every other request is unlimited.
app.UseRateLimiter();

// Reject unauthorized clients at the negotiate level so the connection never establishes.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments(WireProtocol.HubPath))
    {
        var token = context.Request.Query[WireProtocol.TokenParameter].ToString();
        if (!tokens.IsValid(token))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }

    // Agnes-as-MCP-server endpoint: gate every request (each tool call is its own POST in stateless mode) on a
    // valid device token, read from an Authorization: Bearer header (the OpenAI Realtime MCP connector) or the
    // access_token query. The tools re-resolve the caller identity from the same token; this is the outer wall.
    if (context.Request.Path.StartsWithSegments(Agnes.Host.Mcp.AgnesMcpEndpoints.Path))
    {
        var mcpToken = Agnes.Host.Mcp.HttpContextMcpTokenSource.ExtractToken(context);
        if (!tokens.IsValid(mcpToken))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }

    await next();
});

app.MapHub<AgnesHub>(WireProtocol.HubPath);

// Map the Agnes MCP server (Streamable HTTP). Authenticated by the middleware above; tools authorize per call.
app.MapMcp(Agnes.Host.Mcp.AgnesMcpEndpoints.Path);

// ---- device auth + management ----
// Advertise which bootstrap methods this host offers, so a client shows only the enabled ones. All
// public info — the GitHub client id is an OAuth *public* client id, not a secret.
// Sourced from the auth-method plugin registry (AC13): the same AuthMethods wire shape, but the set of
// methods and their enabled state now come from registered IAuthMethodProvider built-ins.
// Also reports each enabled method's AuthFlowKind (in Flows) so the client buckets them into the right UX
// group ("add a device" / "restore access" / "authorize a headless process").
app.MapGet("/auth/methods", (IPluginRegistry<IAuthMethodProvider> methods) =>
    Results.Ok(AuthMethodsFactory.Build(methods)));

// The externally-reachable address a pairing QR/deep-link should encode. Reuses the active transport's
// TransportEndpoint (captured at startup) — or the Agnes:PublicUrl override — so a host reached only through
// a relay or reverse proxy advertises an address a client on another network can resolve, not a bound LAN
// one. The address is public (not the pairing secret), so this needs no auth.
app.MapGet("/pair/qr", (HostReachability reach, IConfiguration cfg, IHostCertificateProvider hostCert) =>
{
    var reachable = PairingReachability.Resolve(cfg["Agnes:PublicUrl"], reach.Endpoint);
    if (string.IsNullOrWhiteSpace(reachable))
    {
        return Results.Json(
            new { error = "This host has no externally-reachable address to advertise yet. Set Agnes:PublicUrl if it sits behind a reverse proxy." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var fingerprint = PinnedFingerprint(hostCert);
    return Results.Ok(new PairingInfo(
        reachable, PairingReachability.BuildDeepLink(reachable, fingerprint: fingerprint), fingerprint));
});

// The fingerprint a client should pin, or null when this host serves a CA-issued certificate and the client
// should validate the chain and name instead. Not a secret — it is a hash of a certificate the host hands to
// anyone who connects — but it only means anything delivered out of band, which is what the QR is for.
static string? PinnedFingerprint(IHostCertificateProvider provider)
    => provider.CaValidatedHostName is { Length: > 0 } ? null : provider.Fingerprint;

// Pair a new device with the current code; returns a durable per-device token (shown once).
app.MapPost("/pair", async (PairRequest request, PairingGrants grants) =>
{
    if (!tokens.PairingEnabled)
    {
        return Results.Json(new { error = "Pairing-code sign-in is disabled on this host." }, statusCode: StatusCodes.Status400BadRequest);
    }

    // The same field carries either a QR grant (256-bit, minted by an already-paired device) or the
    // typed bootstrap code. Try the strong one first: a grant is unguessable, so accepting it costs
    // nothing, while the code is closed as soon as the host has a device that could have vouched.
    if (grants.TryRedeem(request.Code, out _))
    {
        var granted = tokens.IssueDeviceToken(request.DeviceName, subject: "pairing", kind: "pairing-grant");
        await authEvents.DispatchAsync(new Agnes.Abstractions.Events.DevicePairedEvent(
            granted.DeviceId, granted.DeviceName, "pairing-grant", "pairing-grant"));
        return Results.Ok(new PairResponse(granted.DeviceId, granted.DeviceName, granted.Token));
    }

    var result = tokens.TryPair(request.Code, request.DeviceName);
    if (result is null)
    {
        return Results.Json(
            new
            {
                error = tokens.HasPairedDevice
                    ? "This host already has a paired device, so the typed code is closed. "
                      + "Scan a QR from a paired device, or ask one to approve this device."
                    : "Invalid or expired pairing code.",
            },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    await authEvents.DispatchAsync(new Agnes.Abstractions.Events.DevicePairedEvent(result.DeviceId, result.DeviceName, "pairing", "pairing"));
    return Results.Ok(new PairResponse(result.DeviceId, result.DeviceName, result.Token));
});

// Exchange a GitHub user access token (obtained by the client via the device flow) for an Agnes device
// token, if the identity is on the host's allowlist. The GitHub token is verified then discarded.
app.MapPost("/auth/github/exchange", async (GitHubExchangeRequest request, GitHubIdentity github, CancellationToken ct) =>
{
    if (!github.Options.IsUsable)
    {
        return Results.Json(new { error = "GitHub sign-in is not enabled on this host." }, statusCode: StatusCodes.Status400BadRequest);
    }

    var verified = await github.VerifyDetailedAsync(request.Token, ct);
    if (verified.Outcome == GitHubAuthOutcome.NotAllowlisted)
    {
        // Distinguishable from a bad token (AC3): the account authenticated but is gated out by the org/team allowlist.
        return Results.Json(
            new { error = $"GitHub account '{verified.Login}' is not a member of an organization or team allowed on this host.", code = "org_not_allowed" },
            statusCode: StatusCodes.Status403Forbidden);
    }

    if (verified.Outcome != GitHubAuthOutcome.Allowed || verified.Login is null)
    {
        return Results.Json(new { error = "This GitHub account isn't allowed to connect to this host." }, statusCode: StatusCodes.Status403Forbidden);
    }

    var login = verified.Login;
    var result = tokens.IssueDeviceToken(request.DeviceName, subject: "github:" + login, kind: "github");
    await authEvents.DispatchAsync(new Agnes.Abstractions.Events.DevicePairedEvent(result.DeviceId, result.DeviceName, "github", "github:" + login));
    return Results.Ok(new PairResponse(result.DeviceId, result.DeviceName, result.Token));
});

// Exchange an OIDC-issued token (validated against the configured issuer's JWKS + audience) for an Agnes
// device token. Token-validation core only — the interactive authorization-code redirect is out of scope.
app.MapPost("/auth/oidc/exchange", async (OidcExchangeRequest request, OidcIdentity oidc, CancellationToken ct) =>
{
    if (!oidc.Options.IsUsable)
    {
        return Results.Json(new { error = "OIDC sign-in is not enabled on this host." }, statusCode: StatusCodes.Status400BadRequest);
    }

    var validated = await oidc.ValidateAsync(request.Token, ct);
    if (!validated.Ok || validated.Subject is null)
    {
        return Results.Json(new { error = validated.Reason ?? "The OIDC token is invalid." }, statusCode: StatusCodes.Status403Forbidden);
    }

    var result = tokens.IssueDeviceToken(request.DeviceName, subject: "oidc:" + validated.Subject, kind: "oidc");
    await authEvents.DispatchAsync(new Agnes.Abstractions.Events.DevicePairedEvent(result.DeviceId, result.DeviceName, "oidc", "oidc:" + validated.Subject));
    return Results.Ok(new PairResponse(result.DeviceId, result.DeviceName, result.Token));
});

// Browser clients that have already passed Cloudflare Access can exchange the signed assertion which
// cloudflared forwards for a normal, individually revocable Agnes device token. The edge email header is
// intentionally ignored; only the issuer/audience/signature-validated JWT is accepted.
app.MapPost("/auth/cloudflare-access/exchange", async (
    CloudflareAccessExchangeRequest request,
    HttpContext context,
    CloudflareAccessIdentity cloudflareAccess,
    DeviceRegistry tokens,
    CancellationToken ct) =>
{
    if (!cloudflareAccess.Options.IsUsable)
    {
        return Results.Json(new { error = "Cloudflare Access sign-in is not enabled on this host." }, statusCode: StatusCodes.Status400BadRequest);
    }

    var assertion = context.Request.Headers[CloudflareAccessIdentity.AssertionHeaderName].FirstOrDefault();
    var validated = await cloudflareAccess.ValidateAsync(assertion, ct);
    if (!validated.Ok || validated.Email is null || validated.Subject is null)
    {
        return Results.Json(new { error = "Cloudflare Access authentication failed." }, statusCode: StatusCodes.Status403Forbidden);
    }

    var result = tokens.IssueDeviceToken(request.DeviceName, subject: "cloudflare:" + validated.Subject, kind: "cloudflare-access");
    await authEvents.DispatchAsync(new Agnes.Abstractions.Events.DevicePairedEvent(
        result.DeviceId, result.DeviceName, "cloudflare-access", "cloudflare:" + validated.Email));
    return Results.Ok(new PairResponse(result.DeviceId, result.DeviceName, result.Token));
});

// Begin the interactive OIDC authorization-code + PKCE redirect flow: the host generates the PKCE verifier,
// CSRF state and replay nonce (stashed server-side), and returns the issuer's authorization URL for the
// client to open in a browser. Public info only — the challenge, not the verifier, leaves the host.
app.MapGet("/auth/oidc/start", async (string? deviceName, OidcRedirectFlow flow, CancellationToken ct) =>
{
    if (!flow.IsConfigured)
    {
        return Results.Json(new { error = "OIDC sign-in is not enabled on this host." }, statusCode: StatusCodes.Status400BadRequest);
    }

    var start = await flow.StartAsync(deviceName, ct);
    return start is null
        ? Results.Json(new { error = "Could not reach the issuer to begin sign-in." }, statusCode: StatusCodes.Status502BadGateway)
        : Results.Ok(start);
});

// The issuer redirects the browser here with `code` + `state`. Validate the state (CSRF), exchange the code
// for an id_token at the token endpoint (PKCE verifier + optional secret), validate it (reusing the OIDC
// validation core, incl. nonce), and mint the same per-device token the other mechanisms produce. Returns an
// HTML page so the browser tab shows a human-readable result; the device token is embedded for a client that
// drives the browser to read back, and is shown once.
app.MapGet("/auth/oidc/callback", async (string? code, string? state, OidcRedirectFlow flow, CancellationToken ct) =>
{
    if (!flow.IsConfigured)
    {
        return Results.Content(OidcCallbackPage("Sign-in unavailable", "OIDC sign-in is not enabled on this host.", null), "text/html");
    }

    var result = await flow.HandleCallbackAsync(code, state, ct);
    if (!result.Ok || result.Pairing is null)
    {
        return Results.Content(OidcCallbackPage("Couldn't sign in", result.Reason ?? "The sign-in could not be completed.", null), "text/html");
    }

    await authEvents.DispatchAsync(new Agnes.Abstractions.Events.DevicePairedEvent(
        result.Pairing.DeviceId, result.Pairing.DeviceName, "oidc", "oidc:redirect"));
    return Results.Content(OidcCallbackPage("Signed in ✓", "You can return to Agnes. This device is now paired.", result.Pairing), "text/html");
});

// mTLS: the client certificate presented on the TLS connection is the credential. Validate it against the
// configured trust anchor / pin allowlist and mint a device token. (Requires the listener to request a
// client certificate; when TLS is terminated upstream this endpoint isn't reachable with a cert.)
app.MapPost("/auth/mtls", async (MtlsPairRequest request, HttpContext ctx, MtlsIdentity mtls, CancellationToken ct) =>
{
    if (!mtls.Options.IsUsable)
    {
        return Results.Json(new { error = "Client-certificate sign-in is not enabled on this host." }, statusCode: StatusCodes.Status400BadRequest);
    }

    var clientCert = await ctx.Connection.GetClientCertificateAsync(ct);
    var validated = mtls.Validate(clientCert);
    if (!validated.Ok || validated.Subject is null)
    {
        return Results.Json(new { error = validated.Reason ?? "The client certificate is not trusted." }, statusCode: StatusCodes.Status403Forbidden);
    }

    var result = tokens.IssueDeviceToken(request.DeviceName, subject: "mtls:" + validated.Subject, kind: "mtls");
    await authEvents.DispatchAsync(new Agnes.Abstractions.Events.DevicePairedEvent(result.DeviceId, result.DeviceName, "mtls", "mtls:" + validated.Subject));
    return Results.Ok(new PairResponse(result.DeviceId, result.DeviceName, result.Token));
});

// Keypair auth: a single-use challenge nonce the client signs with its private key.
app.MapGet("/auth/keypair/challenge", (KeypairAuth keypair) =>
    keypair.IsUsable
        ? Results.Ok(new KeypairChallenge(keypair.IssueChallenge()))
        : Results.Json(new { error = "Keypair sign-in is not enabled on this host." }, statusCode: StatusCodes.Status400BadRequest));

// Verify a signed challenge against the authorized keys and issue a device token.
app.MapPost("/auth/keypair", async (KeypairAuthRequest request, KeypairAuth keypair) =>
{
    if (!keypair.IsUsable)
    {
        return Results.Json(new { error = "Keypair sign-in is not enabled on this host." }, statusCode: StatusCodes.Status400BadRequest);
    }

    var label = keypair.Verify(request.PublicKey, request.Nonce, request.Signature);
    if (label is null)
    {
        return Results.Json(new { error = "Key not authorized, or the signed challenge was invalid/expired." }, statusCode: StatusCodes.Status403Forbidden);
    }

    var result = tokens.IssueDeviceToken(request.DeviceName, subject: "key:" + label, kind: "keypair");
    await authEvents.DispatchAsync(new Agnes.Abstractions.Events.DevicePairedEvent(result.DeviceId, result.DeviceName, "keypair", "key:" + label));
    return Results.Ok(new PairResponse(result.DeviceId, result.DeviceName, result.Token));
});

// The host's IPv4 address on the sandbox bridge — where the MCP forward listener binds and what the
// guest dials. Prefer the configured value; otherwise read it off the bridge interface. Null → the
// forward proxy stays off (host MCP servers just won't reach sandboxes).
// Builds the default sandbox resource caps from host config (friendly units), keeping the built-in
// 2 CPU / 12 GiB / 16 GiB where a key is absent.
static Agnes.Sandbox.SandboxResourceLimits SandboxLimitsFromConfig(IConfiguration config)
{
    var defaults = new Agnes.Sandbox.SandboxResourceLimits();
    var cpu = config.GetValue<int?>("Agnes:Sandbox:Incus:Cpu");
    var memGiB = config.GetValue<int?>("Agnes:Sandbox:Incus:MemoryGiB");
    var diskGiB = config.GetValue<int?>("Agnes:Sandbox:Incus:DiskGiB");
    const long giB = 1024L * 1024 * 1024;
    return new Agnes.Sandbox.SandboxResourceLimits
    {
        CpuCount = cpu is > 0 ? cpu.Value : defaults.CpuCount,
        MemoryBytes = memGiB is > 0 ? memGiB.Value * giB : defaults.MemoryBytes,
        DiskBytes = diskGiB is > 0 ? diskGiB.Value * giB : defaults.DiskBytes,
    };
}

static System.Net.IPAddress? ResolveBridgeAddress(string? configured, string bridge)
{
    if (!string.IsNullOrWhiteSpace(configured) && System.Net.IPAddress.TryParse(configured, out var ip))
    {
        return ip;
    }

    try
    {
        var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => string.Equals(n.Name, bridge, StringComparison.Ordinal));
        return nic?.GetIPProperties().UnicastAddresses
            .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.Address;
    }
    catch
    {
        return null;
    }
}

// Whether a network-backed catalogue plugin should be registered. On by default — a registry nobody can see
// is a registry nobody uses — and switched off per provider by an operator who doesn't want that egress:
// Agnes:Registries:skillshub:Enabled=false.
static bool RegistryEnabled(IConfiguration configuration, string id)
    => configuration.GetValue($"Agnes:Registries:{id}:Enabled", true);

// The bearer token a request presents, from the header or the query parameter the hub transport uses.
static string RequestToken(HttpContext ctx)
{
    var header = ctx.Request.Headers.Authorization.ToString();
    return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? header["Bearer ".Length..]
        : ctx.Request.Query[WireProtocol.TokenParameter].ToString();
}

// List / revoke paired devices (requires a valid token).
static bool Authorized(HttpContext ctx, DeviceRegistry reg) => reg.IsValid(RequestToken(ctx));

// Authorization for host-wide CONFIG mutations (image manifest, projects, MCP registry, sandbox lifecycle):
// a valid token, plus — when Agnes:Security:RestrictConfigToOwner is set — the host owner specifically.
static bool AuthorizedForConfig(HttpContext ctx, DeviceRegistry reg)
{
    var token = RequestToken(ctx);
    if (!reg.IsValid(token))
    {
        return false;
    }

    var restrict = ctx.RequestServices.GetService<Agnes.Host.Sessions.SessionSecurityOptions>()?.RestrictConfigToOwner ?? false;
    return !restrict || reg.IsOwner(reg.ResolveCallerId(token));
}

// The human-readable page shown in the browser tab at the end of the OIDC redirect flow. On success the
// minted device token is embedded as JSON in a hidden element so a client driving an embedded browser can
// read it back (shown once); it's never placed in a URL or logged.
static string OidcCallbackPage(string title, string message, PairResponse? pairing)
{
    var safeTitle = System.Net.WebUtility.HtmlEncode(title);
    var safeMessage = System.Net.WebUtility.HtmlEncode(message);
    var payload = pairing is null
        ? string.Empty
        : $"""<script id="agnes-pairing" type="application/json">{System.Text.Json.JsonSerializer.Serialize(pairing)}</script>""";
    return $"""
        <!doctype html><html><head><meta charset="utf-8"><title>{safeTitle}</title></head>
        <body style="font-family:system-ui;padding:2rem">
        <h2>{safeTitle}</h2><p>{safeMessage}</p>{payload}
        </body></html>
        """;
}

// ---- stronger pairing: QR grants, and approval by an already-paired device ----

// Mint a one-time 256-bit grant to put in a QR. Authenticated on purpose: holding a device token IS the
// vouching, so only a device the host already trusts can invite another one.
app.MapPost("/pair/grant", (HttpContext ctx, PairingGrants grants, HostReachability reach, IConfiguration cfg,
    IHostCertificateProvider hostCert, string? session) =>
{
    if (!Authorized(ctx, tokens))
    {
        return Results.Unauthorized();
    }

    var candidates = HostAddresses.Candidates(cfg["Agnes:PublicUrl"], reach.Endpoint, reach.BoundAddresses);
    var reachable = PairingReachability.Resolve(cfg["Agnes:PublicUrl"], reach.Endpoint);

    // A QR encoding loopback is a QR no phone can ever act on, so when that is all the transport could
    // resolve, start from something routable instead. The client can still switch to any of the rest.
    if (string.IsNullOrWhiteSpace(reachable) || HostAddresses.IsLoopback(reachable))
    {
        reachable = HostAddresses.FirstRoutable(candidates) ?? reachable;
    }

    return string.IsNullOrWhiteSpace(reachable)
        ? Results.Json(
            new { error = "This host has no externally-reachable address to advertise yet. Set Agnes:PublicUrl if it sits behind a reverse proxy." },
            statusCode: StatusCodes.Status503ServiceUnavailable)
        : Results.Ok(grants.Mint(reachable, session, candidates, PinnedFingerprint(hostCert)));
});

// Drop a displayed grant early — what "hide the QR" calls, so a code that was on screen stops working
// the moment it stops being visible rather than lingering for its full lifetime.
app.MapDelete("/pair/grant/{secret}", (HttpContext ctx, PairingGrants grants, string secret) =>
{
    if (!Authorized(ctx, tokens))
    {
        return Results.Unauthorized();
    }

    grants.Revoke(secret);
    return Results.NoContent();
});

// A new device asks to be vouched for. Unauthenticated by necessity — this is the caller with no
// credential yet — so it is bounded, short-lived, and grants nothing until a human approves.
app.MapPost("/pair/request", (PairApprovalRequest request, PairingApprovals approvals) =>
{
    if (string.IsNullOrWhiteSpace(request.PublicKey))
    {
        return Results.BadRequest(new { error = "A public key is required." });
    }

    var pending = approvals.Open(request.PublicKey, request.DeviceName);
    return pending is null
        ? Results.Json(new { error = "Too many pairing requests are already waiting. Try again shortly." },
            statusCode: StatusCodes.Status429TooManyRequests)
        : Results.Ok(pending);
});

// What's waiting for a human, for an already-paired device to show and act on.
app.MapGet("/pair/pending", (HttpContext ctx, PairingApprovals approvals) =>
    Authorized(ctx, tokens) ? Results.Ok(approvals.Pending()) : Results.Unauthorized());

app.MapPost("/pair/approve/{requestId}", async (HttpContext ctx, PairingApprovals approvals, string requestId) =>
{
    if (!Authorized(ctx, tokens))
    {
        return Results.Unauthorized();
    }

    PairingResult? issued = null;
    var ok = approvals.Approve(requestId, (name, publicKey) =>
    {
        issued = tokens.IssueDeviceToken(name, subject: "approved:" + Fingerprint(publicKey), kind: "approval");
        return issued;
    });

    if (!ok)
    {
        return Results.NotFound(new { error = "That request is no longer waiting." });
    }

    await authEvents.DispatchAsync(new Agnes.Abstractions.Events.DevicePairedEvent(
        issued!.DeviceId, issued.DeviceName, "approval", "approval"));
    return Results.NoContent();
});

app.MapPost("/pair/deny/{requestId}", (HttpContext ctx, PairingApprovals approvals, string requestId) =>
{
    if (!Authorized(ctx, tokens))
    {
        return Results.Unauthorized();
    }

    return approvals.Deny(requestId) ? Results.NoContent() : Results.NotFound();
});

// Polled by the requesting device until a human decides. Unauthenticated — the request id is the only
// thing the caller has — so the token is handed over exactly once and unknown ids look like expired ones.
app.MapGet("/pair/request/{requestId}", (PairingApprovals approvals, string requestId)
    => Results.Ok(approvals.Poll(requestId)));

// A short, stable label for a device's key in the audit trail — never the key itself.
static string Fingerprint(string publicKey)
    => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(publicKey.Trim())))[..16].ToLowerInvariant();

// The caller's own token is passed through so the list can mark which row is the calling device.
app.MapGet("/devices", (HttpContext ctx) =>
    Authorized(ctx, tokens) ? Results.Ok(tokens.ListDevices(RequestToken(ctx))) : Results.Unauthorized());

app.MapDelete("/devices/{id}", async (HttpContext ctx, string id) =>
{
    if (!Authorized(ctx, tokens))
    {
        return Results.Unauthorized();
    }

    if (!tokens.Revoke(id))
    {
        return Results.NotFound();
    }

    await authEvents.DispatchAsync(new Agnes.Abstractions.Events.DeviceRevokedEvent(id));
    return Results.NoContent();
});

// ---- external attention requests: public create/poll REST API (extensibility/06) ----
// Authenticated with an Agnes device token and scoped per caller (a request is only readable via the token
// that created it). Answering happens over the hub, from the shared approvals inbox.
app.MapAttentionEndpoints(tokens, app.Services.GetRequiredService<Agnes.Host.Attention.AttentionRequestService>());

// ---- channel-bridge inbound webhooks (extensibility/04) ----
// One endpoint per real bridge (Slack events, Discord interactions, WhatsApp Cloud API). Each verifies its
// platform signature inside the bridge before raising an inbound message; an unconfigured bridge isn't in the
// registry, so its endpoint 404s. Authorization of a verified message stays in ChannelBridgeRouter.
app.MapChannelBridgeEndpoints(app.Services.GetRequiredService<IPluginRegistry<IChannelBridge>>());

// ---- MCP server management (requires a valid token; mirrors /devices) ----
var mcp = app.Services.GetRequiredService<McpRegistry>();

app.MapGet("/mcp", (HttpContext ctx) =>
    Authorized(ctx, tokens) ? Results.Ok(mcp.List()) : Results.Unauthorized());

// Curated MCP presets, aggregated from every registered IMcpPresetProvider plugin (AC13).
// The quick-install list: everything every catalogue offers up front, as ready-to-install templates. The
// curated built-in answers instantly; a registry plugin contributes its front page.
app.MapGet("/mcp/presets", async (HttpContext ctx, IPluginRegistry<IMcpCatalogProvider> catalogs, CancellationToken ct) =>
    Authorized(ctx, tokens)
        ? Results.Ok((await CatalogSearch.ListAsync(catalogs.All, ct)).Hits.Select(h => McpCatalogMapping.ToInfo(h.Entry)).ToArray())
        : Results.Unauthorized());

// The catalogues themselves, so a client can say which sources it is searching and which of them can search.
app.MapGet("/mcp/catalogs", (HttpContext ctx, IPluginRegistry<IMcpCatalogProvider> catalogs) =>
    Authorized(ctx, tokens) ? Results.Ok(CatalogSearch.Sources(catalogs.All)) : Results.Unauthorized());

// Search across every registered catalogue at once. One slow or unreachable registry degrades to "that one
// found nothing" rather than failing the whole search — see McpCatalogSearch.
app.MapGet("/mcp/catalog/search", async (HttpContext ctx, string? q, IPluginRegistry<IMcpCatalogProvider> catalogs, CancellationToken ct) =>
    Authorized(ctx, tokens)
        ? Results.Ok(await CatalogSearch.SearchAsync(catalogs.All, q ?? string.Empty, ct))
        : Results.Unauthorized());

// Install a catalogued server: the host resolves the entry and maps it into its own configuration, so the
// client never has to know how a registry describes a launch.
app.MapPost("/mcp/catalog/install", async (HttpContext ctx, McpCatalogInstallRequest request,
        IPluginRegistry<IMcpCatalogProvider> catalogs, CancellationToken ct) =>
{
    if (!AuthorizedForConfig(ctx, tokens)) return Results.Unauthorized();

    var provider = catalogs.Find(request.CatalogId);
    if (provider is null) return Results.NotFound();

    var entry = (await provider.SearchAsync(request.EntryId, ct)).FirstOrDefault(e => e.Id == request.EntryId)
                ?? (await provider.ListAsync(ct)).FirstOrDefault(e => e.Id == request.EntryId);
    return entry is null
        ? Results.NotFound()
        : Results.Ok(mcp.Add(McpCatalogMapping.ToRequest(entry, request.RunAt ?? "Host")));
});

// Effective-config preview: the merged, scope-filtered set that WOULD be active for a workspace right now —
// Agnes-managed servers unioned with any an agent CLI already has in its OWN native config (flagged read-only).
// No workspaceId => the host-wide managed view (native config is per-workspace, so it needs a directory).
app.MapGet("/mcp/effective", async (HttpContext ctx, string? workspaceId, string? agentId,
        IPluginRegistry<IAgentAdapter> agents, CancellationToken ct) =>
    Authorized(ctx, tokens)
        ? Results.Ok(await McpEffectiveConfig.PreviewAsync(mcp, agents.All, workspaceId, agentId, ct))
        : Results.Unauthorized());

// ---- Local model provider: read, save, and ask an endpoint what it serves ----
var localProvider = app.Services.GetRequiredService<Agnes.Host.Hosting.LocalProviderRegistry>();

app.MapGet("/local-provider", (HttpContext ctx) =>
    Authorized(ctx, tokens) ? Results.Ok(localProvider.Info()) : Results.Unauthorized());

app.MapPut("/local-provider", (HttpContext ctx, LocalProviderRequest request) =>
    Authorized(ctx, tokens) ? Results.Ok(localProvider.Save(request)) : Results.Unauthorized());

// Discovery is proxied through the host on purpose: the model endpoint is usually on the host's network
// rather than the client's, and this way a device never needs the provider key to populate a picker.
app.MapPost("/local-provider/models", async (HttpContext ctx, LocalProviderRequest request, CancellationToken ct) =>
{
    if (!Authorized(ctx, tokens))
    {
        return Results.Unauthorized();
    }

    // A blank key in the probe means "use the one already stored", so a settings screen can test a saved
    // provider without the client ever holding its credential.
    var stored = localProvider.ProviderOptions();
    var key = string.IsNullOrEmpty(request.ApiKey) ? stored?.ApiKey : request.ApiKey;
    var url = string.IsNullOrWhiteSpace(request.BaseUrl) ? stored?.BaseUrl : request.BaseUrl;

    var models = await Agnes.Agents.Copilot.CopilotLocalModels.ListAsync(url, key, cancellationToken: ct);
    return Results.Ok(models is null
        ? new LocalProviderModels(false, [], "Couldn't reach that endpoint, or it didn't answer with a model list.")
        : new LocalProviderModels(true, [.. models.Select(m => new LocalProviderModel(m.Id, m.DisplayName))], null));
});

app.MapPost("/mcp", (HttpContext ctx, McpServerRequest request) =>
    AuthorizedForConfig(ctx, tokens) ? Results.Ok(mcp.Add(request)) : Results.Unauthorized());

app.MapPut("/mcp/{id}", (HttpContext ctx, string id, McpServerRequest request) =>
    !AuthorizedForConfig(ctx, tokens) ? Results.Unauthorized()
    : mcp.Update(id, request) is { } updated ? Results.Ok(updated) : Results.NotFound());

app.MapDelete("/mcp/{id}", (HttpContext ctx, string id) =>
    !AuthorizedForConfig(ctx, tokens) ? Results.Unauthorized()
    : mcp.Remove(id) ? Results.NoContent() : Results.NotFound());

// ---- baked sandbox image (only when sandboxing is configured) ----
var images = app.Services.GetService<Agnes.Host.Sessions.SandboxImageManager>();

app.MapGet("/sandbox/image", (HttpContext ctx) =>
    !Authorized(ctx, tokens) ? Results.Unauthorized()
    : images is null ? Results.NotFound()
    : Results.Ok(SandboxImageMapping.View(images)));

app.MapGet("/sandbox/image/status", (HttpContext ctx) =>
    !Authorized(ctx, tokens) ? Results.Unauthorized()
    : images is null ? Results.NotFound()
    : Results.Ok(SandboxImageMapping.Status(images.Status)));

app.MapPut("/sandbox/image", (HttpContext ctx, SandboxImageDto dto) =>
{
    if (!AuthorizedForConfig(ctx, tokens)) return Results.Unauthorized();
    if (images is null) return Results.NotFound();
    _ = images.SaveAndRebuildAsync(SandboxImageMapping.ToManifest(dto)); // rebuild runs in the background
    return Results.Ok(SandboxImageMapping.Status(images.Status));
});

app.MapPost("/sandbox/image/rebuild", (HttpContext ctx) =>
{
    if (!AuthorizedForConfig(ctx, tokens)) return Results.Unauthorized();
    if (images is null) return Results.NotFound();
    _ = images.RebuildAsync();
    return Results.Ok(SandboxImageMapping.Status(images.Status));
});

// ---- managed sandboxes: list / delete / resume / reap (Settings › Sandboxes) ----
var sessionMgr = app.Services.GetService<Agnes.Host.Sessions.SessionManager>();

app.MapGet("/sandboxes", (HttpContext ctx) =>
    !Authorized(ctx, tokens) ? Results.Unauthorized()
    : sessionMgr is null ? Results.NotFound()
    : Results.Ok(sessionMgr.ListSandboxes()));

app.MapDelete("/sandboxes/{sessionId}", async (HttpContext ctx, string sessionId) =>
{
    if (!AuthorizedForConfig(ctx, tokens)) return Results.Unauthorized();
    if (sessionMgr is null) return Results.NotFound();
    await sessionMgr.DeleteSandboxAsync(sessionId);
    return Results.Ok(sessionMgr.ListSandboxes());
});

app.MapPost("/sandboxes/{sessionId}/resume", async (HttpContext ctx, string sessionId) =>
{
    if (!Authorized(ctx, tokens)) return Results.Unauthorized();
    if (sessionMgr is null) return Results.NotFound();
    try
    {
        return Results.Ok(await sessionMgr.ResumeSessionAsync(sessionId));
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.MapGet("/sandboxes/orphans", async (HttpContext ctx) =>
    !Authorized(ctx, tokens) ? Results.Unauthorized()
    : sessionMgr is null ? Results.NotFound()
    : Results.Ok(await sessionMgr.ListOrphanVmNamesAsync()));

app.MapPost("/sandboxes/reap", async (HttpContext ctx) =>
    !AuthorizedForConfig(ctx, tokens) ? Results.Unauthorized()
    : sessionMgr is null ? Results.NotFound()
    : Results.Ok(await sessionMgr.ReapOrphanSandboxesAsync()));

// Per-owner usage attribution for the operator ("who is consuming the host"). Owner-gated when config is
// restricted; the same snapshot is logged periodically by UsageReporter.
app.MapGet("/admin/usage", (HttpContext ctx) =>
    !AuthorizedForConfig(ctx, tokens) ? Results.Unauthorized()
    : sessionMgr is null ? Results.NotFound()
    : Results.Ok(sessionMgr.GetUsageReport()));

// ---- credentials: link GitHub (App-manifest flow) so sandboxes can push with scoped tokens ----
var ghConnect = app.Services.GetService<Agnes.Host.Hosting.GitHubConnectFlow>();

app.MapGet("/credentials/status", (HttpContext ctx) =>
    !Authorized(ctx, tokens) ? Results.Unauthorized()
    : ghConnect is null ? Results.NotFound()
    : Results.Ok(ghConnect.Status()));

// Start the connect: returns the loopback URL the desktop opens in the user's browser.
app.MapPost("/credentials/github/connect", (HttpContext ctx) =>
{
    if (!Authorized(ctx, tokens)) return Results.Unauthorized();
    if (ghConnect is null) return Results.NotFound();
    var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}";
    return Results.Ok(new { url = ghConnect.BeginConnect(baseUrl) });
});

// Browser legs — no bearer (the browser can't carry it); guarded by the unguessable state token.
app.MapGet("/credentials/github/start", (string? state) =>
{
    var html = ghConnect?.StartPage(state);
    return html is null ? Results.NotFound() : Results.Content(html, "text/html");
});

app.MapGet("/credentials/github/callback", async (string? code, string? state, string? installation_id) =>
    ghConnect is null ? Results.NotFound()
    : Results.Content(await ghConnect.HandleCallbackAsync(code, state, installation_id), "text/html"));

// Unlink one linked account: drops its App id and private key from the host, so nothing can mint a token
// for it again. Host-wide config, hence owner-gated when the operator restricts config. The minting source
// reads the store live, so removal takes effect at once without re-registering anything.
app.MapDelete("/credentials/github/{account}", (HttpContext ctx, string account) =>
{
    if (!AuthorizedForConfig(ctx, tokens)) return Results.Unauthorized();
    var store = app.Services.GetService<Agnes.Host.Hosting.GitHubAppStore>();
    if (store is null) return Results.NotFound();
    return store.Remove(account) ? Results.NoContent() : Results.NotFound();
});

// Fallback: store a token credential source (a fine-grained PAT) for a host.
var credentialSources = app.Services.GetService<Agnes.Host.Hosting.CredentialSourceRegistry>();
app.MapPost("/credentials/token", (HttpContext ctx, StoreCredentialRequest req) =>
{
    if (!Authorized(ctx, tokens)) return Results.Unauthorized();
    if (credentialSources is null || string.IsNullOrWhiteSpace(req.Host) || string.IsNullOrWhiteSpace(req.Token))
    {
        return Results.BadRequest();
    }

    credentialSources.Set(new Agnes.Host.Hosting.StoredTokenCredentialSource(req.Host, req.Token, req.Username ?? "x-access-token"));
    return Results.Ok(new { host = req.Host });
});

// ---- projects: the per-repo bundles a session inherits (sandbox + MCP + GitHub account + defaults) ----
var projects = app.Services.GetService<Agnes.Host.Projects.ProjectStore>();
var projectGit = new Agnes.Host.Git.GitService();

app.MapGet("/projects", (HttpContext ctx) =>
    !Authorized(ctx, tokens) ? Results.Unauthorized()
    : projects is null ? Results.NotFound()
    : Results.Ok(projects.List().Select(Agnes.Host.Projects.ProjectMapping.ToDto)));

app.MapGet("/projects/{id}", (HttpContext ctx, string id) =>
    !Authorized(ctx, tokens) ? Results.Unauthorized()
    : projects?.Get(id) is { } p ? Results.Ok(Agnes.Host.Projects.ProjectMapping.ToDto(p)) : Results.NotFound());

// What project a working directory resolves to (a non-creating preview for the session setup).
app.MapGet("/projects/resolve", async (HttpContext ctx, string? dir) =>
{
    if (!Authorized(ctx, tokens)) return Results.Unauthorized();
    if (projects is null) return Results.NotFound();
    var remote = await projectGit.GetRemoteUrlAsync(dir ?? string.Empty);
    var repoKey = Agnes.Host.Hosting.GitRemote.TryParse(remote, out var h, out var r) ? $"{h}/{r}" : string.Empty;
    return Results.Ok(Agnes.Host.Projects.ProjectMapping.ToDto(projects.Peek(repoKey)));
});

app.MapPut("/projects/{id}", (HttpContext ctx, string id, ProjectDto dto) =>
{
    if (!AuthorizedForConfig(ctx, tokens)) return Results.Unauthorized();
    if (projects is null) return Results.NotFound();
    var saved = projects.Save(Agnes.Host.Projects.ProjectMapping.ToProject(dto with { Id = id }));
    _ = images?.RebuildForProjectAsync(saved); // re-bake the project's sandbox image in the background
    return Results.Ok(Agnes.Host.Projects.ProjectMapping.ToDto(saved));
});

app.MapDelete("/projects/{id}", (HttpContext ctx, string id) =>
    !AuthorizedForConfig(ctx, tokens) ? Results.Unauthorized()
    : projects is null ? Results.NotFound()
    : projects.Remove(id) ? Results.NoContent() : Results.NotFound());

if (webFiles is not null)
{
    app.MapFallbackToFile("index.html", new StaticFileOptions
    {
        FileProvider = webFiles,
        OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-store",
    });
}
else
{
    app.MapGet("/", () => $"Agnes host — wire protocol v{WireProtocol.Version}. Hub at {WireProtocol.HubPath}.");
}

// Print the enabled bootstrap methods so a new device knows how to connect. (Tokens are per-device and
// never logged; the GitHub client id is a public OAuth id.)
if (tokens.PairingEnabled)
{
    app.Logger.LogInformation("Agnes pairing code: {Code}  — enter this on a new client to pair it.", tokens.PairingCode);
}

// The fingerprint of the certificate this host serves, printed the way sshd's host key can be inspected.
// A client meeting a self-signed host for the first time has no pin and therefore cannot complete a
// handshake at all — so without something to compare against, trust-on-first-use is just trust. Printing it
// here gives the operator the out-of-band channel: read it off the host's own console, check it against what
// the client shows, then accept. Not a secret — it is a hash of a certificate handed to anyone who connects.
{
    var hostCert = app.Services.GetService<IHostCertificateProvider>();
    if (PinnedFingerprint2(hostCert) is { Length: > 0 } hostFingerprint)
    {
        app.Logger.LogInformation(
            "Agnes host certificate SHA-256: {Fingerprint}  — a client pairing for the first time must be shown "
            + "this, and should refuse if what it sees differs.", hostFingerprint);
    }
}

// Local mirror of PinnedFingerprint so the startup banner does not depend on declaration order.
static string? PinnedFingerprint2(IHostCertificateProvider? provider)
    => provider is null ? null
        : provider.CaValidatedHostName is { Length: > 0 } ? null
        : provider.Fingerprint;

if (gitHubAuth.IsUsable)
{
    app.Logger.LogInformation("GitHub sign-in enabled (client id {ClientId}); allowed: users [{Users}] orgs [{Orgs}].",
        gitHubAuth.ClientId, string.Join(", ", gitHubAuth.AllowedUsers), string.Join(", ", gitHubAuth.AllowedOrgs));
}

var keypairUsable = app.Services.GetRequiredService<KeypairAuth>().IsUsable;
if (keypairUsable)
{
    app.Logger.LogInformation("Keypair sign-in enabled (authorized_keys: {File}).", keypairAuthOptions.AuthorizedKeysFile);
}

if (authRateLimit.Enabled)
{
    app.Logger.LogInformation("Auth rate limit: {PerIp}/min per IP, {Global}/min global (trust X-Forwarded-For: {Xff}).",
        authRateLimit.PerIpPerMinute, authRateLimit.GlobalPerMinute, authRateLimit.TrustForwardedFor);
}

if (!tokens.PairingEnabled && !gitHubAuth.IsUsable && !keypairUsable)
{
    app.Logger.LogWarning("No usable sign-in method is configured — set Agnes:Auth:GitHub / Keypair or re-enable pairing, or no client can connect.");
}

// Resolve the configured transport from the plugin registry (default: direct) and advertise, once the
// server is listening, the address(es) clients should use for it (AC13).
var transportName = builder.Configuration["Agnes:Transport:Provider"] ?? "direct";
var transports = app.Services.GetRequiredService<IPluginRegistry<ITransportProvider>>();
var transport = transports.Find(transportName)
    ?? throw new InvalidOperationException(
        $"No transport provider named '{transportName}' is registered (have: {string.Join(", ", transports.All.Select(t => t.Id))}).");
app.Lifetime.ApplicationStarted.Register(() =>
{
    var bound = app.Urls.Count > 0 ? app.Urls.ToArray() : ["(host default binding)"];
    try
    {
        // ExposeAsync actively brings a tunnel transport up (e.g. runs `tailscale serve`); Direct just
        // echoes the bound addresses. Block here so a misconfigured transport fails loudly at startup with an
        // actionable error rather than silently leaving the host unreachable/unintended (AC6).
        var endpoint = transport.ExposeAsync(new HostExposureContext(bound)).GetAwaiter().GetResult();
        // Publish the reachable endpoint so the pairing QR/deep-link advertises it, not a bound LAN address.
        var reachability = app.Services.GetRequiredService<HostReachability>();
        reachability.Endpoint = endpoint;
        reachability.BoundAddresses = bound;
        app.Logger.LogInformation("Transport '{Transport}' ({Hint}): clients reach this host at {Addresses}.",
            transport.DisplayName, endpoint.DisplayHint, string.Join(", ", endpoint.ClientAddresses));
    }
    catch (Exception ex)
    {
        app.Logger.LogCritical(ex, "Transport '{Transport}' could not be exposed — stopping. {Message}",
            transport.DisplayName, ex.Message);
        app.Lifetime.StopApplication();
    }
});
app.Lifetime.ApplicationStopping.Register(() => transport.StopAsync().GetAwaiter().GetResult());

app.Run();

/// <summary>Program entry point marker (also used for the assembly reference in tests).</summary>
public partial class Program;
