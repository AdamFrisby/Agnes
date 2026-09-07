using Agnes.Host.Hosting;
using Agnes.Protocol;
using Microsoft.AspNetCore.Http;

namespace Agnes.Host.Mcp;

/// <summary>Raised by a tool when the current request carries no valid Agnes device token. The MCP layer
/// surfaces it as a tool error; the caller sees an explicit "unauthenticated" rejection rather than a silent
/// no-op.</summary>
public sealed class McpUnauthenticatedException : Exception
{
    public McpUnauthenticatedException(string message) : base(message) { }
}

/// <summary>Validates a bearer token and resolves it to a stable caller identity — the SAME authority path a
/// paired client uses (<see cref="DeviceRegistry.ResolveCallerId"/>). Abstracted so the tool layer is
/// unit-testable without a real registry.</summary>
public interface IMcpDeviceAuthenticator
{
    /// <summary>The caller id for a valid token, or null when the token is missing/unknown.</summary>
    string? ResolveCaller(string? token);
}

/// <summary>Production authenticator backed by the host's <see cref="DeviceRegistry"/>.</summary>
public sealed class DeviceRegistryMcpAuthenticator : IMcpDeviceAuthenticator
{
    private readonly DeviceRegistry _devices;

    public DeviceRegistryMcpAuthenticator(DeviceRegistry devices) => _devices = devices;

    public string? ResolveCaller(string? token) => _devices.ResolveCallerId(token);
}

/// <summary>
/// The outer wall in front of <c>/mcp-agnes</c>: which bearers are recognized at all.
/// </summary>
/// <remarks>
/// Two kinds pass, and the difference between them is the security model. A <b>device</b> token is a paired
/// human and may drive every session on the host. A <b>session</b> token is one agent: it is not a device
/// token, carries none of that authority, and <see cref="AgnesMcpTools"/> refuses it for anything but its own
/// session's goals and file-sharing.
///
/// Accepting only device tokens here rejects every agent before it reaches the tools — which is precisely
/// what made the <c>agnes</c> server unreachable from the sessions its config was being written into. Pulled
/// out of <c>Program</c> so that decision is testable rather than reachable only by starting a host.
/// </remarks>
public static class McpEndpointGate
{
    public static bool IsAccepted(string? token, DeviceRegistry devices, SessionMcpTokens sessions)
        => devices.IsValid(token) || sessions.SessionFor(token) is not null;
}

/// <summary>Supplies the bearer token accompanying the current MCP request. Abstracted so tools can be
/// exercised offline (a fixed token) without an HTTP context.</summary>
public interface IMcpCallerTokenSource
{
    string? CurrentToken { get; }
}

/// <summary>Reads the token from the active HTTP request — an <c>Authorization: Bearer &lt;token&gt;</c> header
/// (how the OpenAI Realtime MCP connector authenticates) or, as a fallback, the <c>access_token</c> query
/// parameter (matching the SignalR hub's convention).</summary>
public sealed class HttpContextMcpTokenSource : IMcpCallerTokenSource
{
    private readonly IHttpContextAccessor _accessor;

    public HttpContextMcpTokenSource(IHttpContextAccessor accessor) => _accessor = accessor;

    public string? CurrentToken => ExtractToken(_accessor.HttpContext);

    /// <summary>Pulls the bearer token off a request: Authorization header first, then the access_token query.</summary>
    public static string? ExtractToken(HttpContext? context)
    {
        if (context is null)
        {
            return null;
        }

        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return header["Bearer ".Length..].Trim();
        }

        var query = context.Request.Query[WireProtocol.TokenParameter].ToString();
        return string.IsNullOrEmpty(query) ? null : query;
    }
}
