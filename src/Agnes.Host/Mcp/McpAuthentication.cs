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
