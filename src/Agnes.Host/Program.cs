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

// ---- host identity ----
var displayName = builder.Configuration["Agnes:DisplayName"] ?? Environment.MachineName;
builder.Services.AddSingleton(new HostIdentity(
    HostId: Guid.NewGuid().ToString("n"),
    DisplayName: displayName,
    Version: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.1.0"));

// ---- auth (dev device token) ----
builder.Services.AddSingleton(new DeviceTokenStore(builder.Configuration["Agnes:PairingToken"]));

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

var app = builder.Build();

var tokens = app.Services.GetRequiredService<DeviceTokenStore>();

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
app.MapGet("/", () => $"Agnes host — wire protocol v{WireProtocol.Version}. Hub at {WireProtocol.HubPath}.");

// Surface the dev pairing token so a client can connect.
app.Logger.LogInformation("Agnes pairing token: {Token}", tokens.PairingToken);

app.Run();

/// <summary>Program entry point marker (also used for the assembly reference in tests).</summary>
public partial class Program;
