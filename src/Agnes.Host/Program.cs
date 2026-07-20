using Agnes.Abstractions;
using Agnes.Acp;
using Agnes.Agents.ClaudeCode;
using Agnes.Agents.OpenCode;
using Agnes.Host.Events;
using Agnes.Host.Hosting;
using Agnes.Host.Sessions;
using Agnes.Protocol;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();

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
var devicesFile = builder.Configuration["Agnes:DevicesFile"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "devices.json");
builder.Services.AddSingleton(sp => new DeviceRegistry(
    builder.Configuration["Agnes:PairingToken"], devicesFile,
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<DeviceRegistry>()));

// ---- MCP server registry (configured from the UI, persisted to ~/.agnes/mcp.json) ----
var mcpFile = builder.Configuration["Agnes:McpFile"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agnes", "mcp.json");
builder.Services.AddSingleton(sp => new McpRegistry(
    mcpFile, sp.GetRequiredService<ILoggerFactory>().CreateLogger<McpRegistry>()));

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
    builder.Configuration.GetSection("Agnes:Codex:Args").Get<string[]>()));

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
}

var app = builder.Build();

// Eagerly instantiate the rotation pusher (its FileSystemWatcher starts in the ctor) so live
// sandboxes get refreshed credentials when the host claude CLI rotates its OAuth token.
_ = app.Services.GetService<Agnes.Sandbox.Credentials.ClaudeTokenRotationPusher>();
// Start the MCP forward listener (if sandboxing + a bridge address are configured).
_ = app.Services.GetService<Agnes.Host.Hosting.McpForwardListener>();

// Restore the session catalogue so sessions (and their history) survive a host restart.
await app.Services.GetRequiredService<SessionManager>().RestoreAsync();

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

// ---- device pairing + management ----
// Pair a new device with the current code; returns a durable per-device token (shown once).
app.MapPost("/pair", (PairRequest request) =>
{
    var result = tokens.TryPair(request.Code, request.DeviceName);
    return result is null
        ? Results.Json(new { error = "Invalid or expired pairing code." }, statusCode: StatusCodes.Status401Unauthorized)
        : Results.Ok(new PairResponse(result.DeviceId, result.DeviceName, result.Token));
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

if (webFiles is not null)
{
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = webFiles });
}
else
{
    app.MapGet("/", () => $"Agnes host — wire protocol v{WireProtocol.Version}. Hub at {WireProtocol.HubPath}.");
}

// Print the pairing code so a new device can pair. (Tokens are per-device and never logged.)
app.Logger.LogInformation("Agnes pairing code: {Code}  — enter this on a new client to pair it.", tokens.PairingCode);

app.Run();

/// <summary>Program entry point marker (also used for the assembly reference in tests).</summary>
public partial class Program;
