using Agnes.Abstractions;
using Agnes.Acp;
using Agnes.Agents.ClaudeCode;
using Agnes.Agents.OpenCode;
using Agnes.Host.Events;
using Agnes.Host.Hosting;
using Agnes.Host.Sessions;
using Agnes.Protocol;

var builder = WebApplication.CreateBuilder(args);

// Surface the real server-side exception message to the client (this is a local, single-user host, so
// there's no third party to leak internals to) — otherwise hub failures arrive as an opaque
// "An unexpected error occurred" and can't be diagnosed from the UI.
builder.Services.AddSignalR(o => o.EnableDetailedErrors = true);

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
    pairingEnabled: builder.Configuration.GetValue("Agnes:Auth:Pairing:Enabled", true)));

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

// ---- projects: per-repo bundles (sandbox + MCP + GitHub account + defaults) a session inherits ----
var projectsFile = builder.Configuration["Agnes:ProjectsFile"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "projects.json");
builder.Services.AddSingleton(sp => new Agnes.Host.Projects.ProjectStore(
    projectsFile, sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Projects.ProjectStore>()));

// ---- managed-sandbox registry: persisted so stopped/closed VMs stay visible (resume/delete) across restarts ----
var sandboxesFile = builder.Configuration["Agnes:SandboxesFile"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "sandboxes.json");
builder.Services.AddSingleton(sp => new Agnes.Host.Sessions.SandboxRegistry(
    sandboxesFile, sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Sessions.SandboxRegistry>()));

// ---- event store: SQLite if a path is configured, else in-memory ----
var databasePath = builder.Configuration["Agnes:Database"];
if (string.IsNullOrWhiteSpace(databasePath))
{
    builder.Services.AddSingleton<IEventStore, InMemoryEventStore>();
}
else
{
    builder.Services.AddSingleton<IEventStore>(new SqliteEventStore(databasePath));
}

// ---- broadcast + session manager ----
builder.Services.AddSingleton<ISessionBroadcaster, SignalRBroadcaster>();
builder.Services.AddSingleton<SessionManager>();

// ---- scheduled / background tasks + inbox ----
builder.Services.AddSingleton<ScheduledTaskManager>();
builder.Services.AddHostedService<ScheduledRunner>();

// ---- agent adapters (plugins) ----
builder.Services.AddSingleton<IAgentAdapter>(sp =>
{
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var options = new ClaudeCodeOptions
    {
        Command = builder.Configuration["Agnes:ClaudeCode:Command"] ?? "npx",
        Arguments = builder.Configuration.GetSection("Agnes:ClaudeCode:Args").Get<string[]>()
                    ?? ["-y", "@zed-industries/claude-code-acp"],
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

// A typed, DI-resolvable registry over every IAgentAdapter above — the plugin-point pattern every
// new provider interface in this backlog follows (see .ideas/00-plugin-architecture.md). Built once
// all the AddSingleton<IAgentAdapter> registrations above have run; consumed by SessionManager
// (Find-by-id instead of a hand-rolled dictionary) and by capability negotiation (GetCapabilities).
// Registered once as the concrete type, then exposed under both the read-only and mutable interfaces
// so IPluginInstaller can merge a NuGet-installed plugin's adapters into the exact same instance
// SessionManager already resolves — no separate "plugin adapters" list to keep in sync.
builder.Services.AddSingleton(sp =>
    new PluginRegistry<IAgentAdapter>(sp.GetServices<IAgentAdapter>(), a => a.Descriptor.Id));
builder.Services.AddSingleton<IPluginRegistry<IAgentAdapter>>(sp => sp.GetRequiredService<PluginRegistry<IAgentAdapter>>());
builder.Services.AddSingleton<IMutablePluginRegistry<IAgentAdapter>>(sp => sp.GetRequiredService<PluginRegistry<IAgentAdapter>>());
builder.Services.AddSingleton<Agnes.Host.Plugins.IPluginPointMerger>(sp =>
    new Agnes.Host.Plugins.PluginPointMerger<IAgentAdapter>(sp.GetRequiredService<IMutablePluginRegistry<IAgentAdapter>>(), a => a.Descriptor.Id));

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
        },
        sp.GetRequiredService<ILoggerFactory>()));
    builder.Services.AddSingleton<Agnes.Sandbox.ISandboxProvider>(
        sp => sp.GetRequiredService<Agnes.Sandbox.Incus.IncusSandboxProvider>());

    builder.Services.AddSingleton<Agnes.Sandbox.Credentials.ClaudeCredentialProvider>();
    builder.Services.AddSingleton<Agnes.Sandbox.Credentials.IAgentCredentialProvider>(
        sp => sp.GetRequiredService<Agnes.Sandbox.Credentials.ClaudeCredentialProvider>());

    builder.Services.AddSingleton<Agnes.Sandbox.Credentials.ClaudeTokenRotationPusher>();

    // MCP forward proxy: lets a sandboxed agent reach RunAt=Host MCP servers running on this host,
    // over the sandbox bridge (token-gated, bound only to the bridge address).
    builder.Services.AddSingleton<Agnes.Host.Hosting.McpForwardRegistry>();
    var mcpBridge = builder.Configuration["Agnes:Sandbox:Incus:Bridge"] ?? "incusbr0";
    var mcpHostAddress = ResolveBridgeAddress(builder.Configuration["Agnes:Sandbox:Incus:HostAddress"], mcpBridge);
    if (mcpHostAddress is not null)
    {
        var mcpPort = int.TryParse(builder.Configuration["Agnes:Sandbox:Incus:McpPort"], out var configuredPort) ? configuredPort : 0;
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
        var gitPort = int.TryParse(builder.Configuration["Agnes:Sandbox:Incus:GitPort"], out var configuredGitPort) ? configuredGitPort : 0;
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
builder.Services.AddSingleton(sp =>
    new PluginRegistry<Agnes.Sandbox.ISandboxProvider>(sp.GetServices<Agnes.Sandbox.ISandboxProvider>(), p => p.Name));
builder.Services.AddSingleton<IPluginRegistry<Agnes.Sandbox.ISandboxProvider>>(sp => sp.GetRequiredService<PluginRegistry<Agnes.Sandbox.ISandboxProvider>>());
builder.Services.AddSingleton<IMutablePluginRegistry<Agnes.Sandbox.ISandboxProvider>>(sp => sp.GetRequiredService<PluginRegistry<Agnes.Sandbox.ISandboxProvider>>());
builder.Services.AddSingleton<Agnes.Host.Plugins.IPluginPointMerger>(sp =>
    new Agnes.Host.Plugins.PluginPointMerger<Agnes.Sandbox.ISandboxProvider>(sp.GetRequiredService<IMutablePluginRegistry<Agnes.Sandbox.ISandboxProvider>>(), p => p.Name));

// Credential sources + the Connect-GitHub flow are always available (a user can link GitHub before
// they ever open a sandbox); the broker above only consumes what's registered here.
builder.Services.AddSingleton<Agnes.Host.Hosting.CredentialSourceRegistry>();
builder.Services.AddSingleton(_ => new Agnes.Host.Hosting.GitHubAppStore());
builder.Services.AddSingleton(sp => new Agnes.Host.Hosting.GitHubConnectFlow(
    sp.GetRequiredService<Agnes.Host.Hosting.GitHubAppStore>(),
    sp.GetRequiredService<Agnes.Host.Hosting.CredentialSourceRegistry>(),
    new HttpClient(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Hosting.GitHubConnectFlow>()));

// ---- plugin installer: NuGet-packaged third-party plugins (see .ideas/00-plugin-architecture.md) ----
// The scoped service a plugin gets when it declares (and is granted) the "credentials" capability.
builder.Services.AddSingleton<ICredentialBroker>(sp =>
    new Agnes.Host.Plugins.HostCredentialBroker(sp.GetRequiredService<Agnes.Host.Hosting.CredentialSourceRegistry>()));
builder.Services.AddSingleton(sp => new Agnes.Host.Plugins.PluginCapabilityService(
    PluginCapabilityIds.Credentials,
    (pluginServices, hostServices) => pluginServices.AddSingleton(hostServices.GetRequiredService<ICredentialBroker>())));

var pluginSources = builder.Configuration.GetSection("Agnes:Plugins:Sources").Get<string[]>() ?? [];
builder.Services.AddSingleton<Agnes.Host.Plugins.INuGetPluginFeed>(
    _ => new Agnes.Host.Plugins.NuGetProtocolFeed(pluginSources));

var allowUnsignedPlugins = builder.Configuration.GetValue("Agnes:Plugins:AllowUnsignedPackages", false);
builder.Services.AddSingleton<Agnes.Host.Plugins.IPluginPackageVerifier>(sp =>
    new Agnes.Host.Plugins.NuGetSignatureVerifier(allowUnsignedPlugins,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Plugins.NuGetSignatureVerifier>()));

var pluginsRoot = builder.Configuration["Agnes:Plugins:Directory"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "plugins");
var pluginStateFile = builder.Configuration["Agnes:Plugins:StateFile"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "plugins.json");
builder.Services.AddSingleton(sp => new Agnes.Host.Plugins.PluginStateStore(
    pluginStateFile, sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Plugins.PluginStateStore>()));

builder.Services.AddSingleton<IPluginInstaller>(sp => new Agnes.Host.Plugins.PluginInstaller(
    sp.GetRequiredService<Agnes.Host.Plugins.INuGetPluginFeed>(),
    sp.GetRequiredService<Agnes.Host.Plugins.IPluginPackageVerifier>(),
    sp.GetRequiredService<Agnes.Host.Plugins.PluginStateStore>(),
    pluginsRoot,
    sp,
    sp.GetServices<Agnes.Host.Plugins.IPluginPointMerger>(),
    sp.GetServices<Agnes.Host.Plugins.PluginCapabilityService>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<Agnes.Host.Plugins.PluginInstaller>()));

// The wire-facing adapter the SignalR hub delegates to (DTO mapping + consent-exception → typed outcome).
builder.Services.AddSingleton(sp => new Agnes.Host.Plugins.PluginManagementService(sp.GetRequiredService<IPluginInstaller>()));


var app = builder.Build();

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

// Eagerly resolve the plugin installer so previously installed, enabled plugins reload (and merge
// back into their registries) on this same startup, not lazily on the first plugin-management call.
_ = app.Services.GetRequiredService<IPluginInstaller>();

var tokens = app.Services.GetRequiredService<DeviceRegistry>();

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

    await next();
});

app.MapHub<AgnesHub>(WireProtocol.HubPath);

// ---- device auth + management ----
// Advertise which bootstrap methods this host offers, so a client shows only the enabled ones. All
// public info — the GitHub client id is an OAuth *public* client id, not a secret.
app.MapGet("/auth/methods", (GitHubAuthOptions gh, KeypairAuth keypair) =>
    Results.Ok(new AuthMethods(
        Pairing: tokens.PairingEnabled,
        GitHub: gh.IsUsable,
        GitHubClientId: gh.IsUsable ? gh.ClientId : null,
        Keypair: keypair.IsUsable)));

// Pair a new device with the current code; returns a durable per-device token (shown once).
app.MapPost("/pair", (PairRequest request) =>
{
    if (!tokens.PairingEnabled)
    {
        return Results.Json(new { error = "Pairing-code sign-in is disabled on this host." }, statusCode: StatusCodes.Status400BadRequest);
    }

    var result = tokens.TryPair(request.Code, request.DeviceName);
    return result is null
        ? Results.Json(new { error = "Invalid or expired pairing code." }, statusCode: StatusCodes.Status401Unauthorized)
        : Results.Ok(new PairResponse(result.DeviceId, result.DeviceName, result.Token));
});

// Exchange a GitHub user access token (obtained by the client via the device flow) for an Agnes device
// token, if the identity is on the host's allowlist. The GitHub token is verified then discarded.
app.MapPost("/auth/github/exchange", async (GitHubExchangeRequest request, GitHubIdentity github, CancellationToken ct) =>
{
    if (!github.Options.IsUsable)
    {
        return Results.Json(new { error = "GitHub sign-in is not enabled on this host." }, statusCode: StatusCodes.Status400BadRequest);
    }

    var login = await github.VerifyAsync(request.Token, ct);
    if (login is null)
    {
        return Results.Json(new { error = "This GitHub account isn't allowed to connect to this host." }, statusCode: StatusCodes.Status403Forbidden);
    }

    var result = tokens.IssueDeviceToken(request.DeviceName, subject: "github:" + login, kind: "github");
    return Results.Ok(new PairResponse(result.DeviceId, result.DeviceName, result.Token));
});

// Keypair auth: a single-use challenge nonce the client signs with its private key.
app.MapGet("/auth/keypair/challenge", (KeypairAuth keypair) =>
    keypair.IsUsable
        ? Results.Ok(new KeypairChallenge(keypair.IssueChallenge()))
        : Results.Json(new { error = "Keypair sign-in is not enabled on this host." }, statusCode: StatusCodes.Status400BadRequest));

// Verify a signed challenge against the authorized keys and issue a device token.
app.MapPost("/auth/keypair", (KeypairAuthRequest request, KeypairAuth keypair) =>
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
    return Results.Ok(new PairResponse(result.DeviceId, result.DeviceName, result.Token));
});

// The host's IPv4 address on the sandbox bridge — where the MCP forward listener binds and what the
// guest dials. Prefer the configured value; otherwise read it off the bridge interface. Null → the
// forward proxy stays off (host MCP servers just won't reach sandboxes).
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

// List / revoke paired devices (requires a valid token).
static bool Authorized(HttpContext ctx, DeviceRegistry reg)
{
    var header = ctx.Request.Headers.Authorization.ToString();
    var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        ? header["Bearer ".Length..]
        : ctx.Request.Query[WireProtocol.TokenParameter].ToString();
    return reg.IsValid(token);
}

app.MapGet("/devices", (HttpContext ctx) =>
    Authorized(ctx, tokens) ? Results.Ok(tokens.ListDevices()) : Results.Unauthorized());

app.MapDelete("/devices/{id}", (HttpContext ctx, string id) =>
    !Authorized(ctx, tokens) ? Results.Unauthorized()
    : tokens.Revoke(id) ? Results.NoContent() : Results.NotFound());

// ---- MCP server management (requires a valid token; mirrors /devices) ----
var mcp = app.Services.GetRequiredService<McpRegistry>();

app.MapGet("/mcp", (HttpContext ctx) =>
    Authorized(ctx, tokens) ? Results.Ok(mcp.List()) : Results.Unauthorized());

app.MapPost("/mcp", (HttpContext ctx, McpServerRequest request) =>
    Authorized(ctx, tokens) ? Results.Ok(mcp.Add(request)) : Results.Unauthorized());

app.MapPut("/mcp/{id}", (HttpContext ctx, string id, McpServerRequest request) =>
    !Authorized(ctx, tokens) ? Results.Unauthorized()
    : mcp.Update(id, request) is { } updated ? Results.Ok(updated) : Results.NotFound());

app.MapDelete("/mcp/{id}", (HttpContext ctx, string id) =>
    !Authorized(ctx, tokens) ? Results.Unauthorized()
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
    if (!Authorized(ctx, tokens)) return Results.Unauthorized();
    if (images is null) return Results.NotFound();
    _ = images.SaveAndRebuildAsync(SandboxImageMapping.ToManifest(dto)); // rebuild runs in the background
    return Results.Ok(SandboxImageMapping.Status(images.Status));
});

app.MapPost("/sandbox/image/rebuild", (HttpContext ctx) =>
{
    if (!Authorized(ctx, tokens)) return Results.Unauthorized();
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
    if (!Authorized(ctx, tokens)) return Results.Unauthorized();
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
    !Authorized(ctx, tokens) ? Results.Unauthorized()
    : sessionMgr is null ? Results.NotFound()
    : Results.Ok(await sessionMgr.ReapOrphanSandboxesAsync()));

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
    if (!Authorized(ctx, tokens)) return Results.Unauthorized();
    if (projects is null) return Results.NotFound();
    var saved = projects.Save(Agnes.Host.Projects.ProjectMapping.ToProject(dto with { Id = id }));
    _ = images?.RebuildForProjectAsync(saved); // re-bake the project's sandbox image in the background
    return Results.Ok(Agnes.Host.Projects.ProjectMapping.ToDto(saved));
});

app.MapDelete("/projects/{id}", (HttpContext ctx, string id) =>
    !Authorized(ctx, tokens) ? Results.Unauthorized()
    : projects is null ? Results.NotFound()
    : projects.Remove(id) ? Results.NoContent() : Results.NotFound());

if (webFiles is not null)
{
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = webFiles });
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

app.Run();

/// <summary>Program entry point marker (also used for the assembly reference in tests).</summary>
public partial class Program;
