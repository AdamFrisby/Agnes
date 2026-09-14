using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace Agnes.Host.Fleet;

/// <summary>Where the orchestrator is and how the host authenticates to it.</summary>
public sealed record FleetOptions(string BaseUrl, string ApiKey)
{
    /// <summary>
    /// Reads <c>Agnes:Fleet:BaseUrl</c> / <c>Agnes:Fleet:ApiKey</c>, or else the orchestrator's own config
    /// file (<c>Agnes:Fleet:ConfigPath</c>, default <c>~/.config/codeybox/config.json</c>), which is what the
    /// desktop plugin reads when it runs on the same machine. <c>Agnes:Fleet:Enabled=false</c> turns the
    /// whole surface off. Null when nothing is configured: the host then advertises no fleet and answers
    /// every fleet route 404.
    /// </summary>
    public static FleetOptions? From(IConfiguration configuration)
    {
        if (string.Equals(configuration["Agnes:Fleet:Enabled"], "false", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var url = configuration["Agnes:Fleet:BaseUrl"];
        var key = configuration["Agnes:Fleet:ApiKey"];
        if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(key))
        {
            return new FleetOptions(url.TrimEnd('/'), key);
        }

        var path = configuration["Agnes:Fleet:ConfigPath"]
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "codeybox", "config.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var file = JsonSerializer.Deserialize<OrchestratorConfigFile>(File.ReadAllText(path), FileJson);
            return file is { ApiBaseUrl.Length: > 0, ApiKey.Length: > 0 }
                ? new FleetOptions(file.ApiBaseUrl.TrimEnd('/'), file.ApiKey)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions FileJson = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    // The orchestrator's own file: a boundary format, read into a typed record and nothing else.
    private sealed record OrchestratorConfigFile(string? ApiBaseUrl, string? ApiKey);
}

/// <summary>
/// Forwards one already-vetted request to the orchestrator: the host's key goes on, the device's token
/// does not, and the answer comes back as the orchestrator gave it.
/// </summary>
/// <remarks>
/// This class knows nothing about which routes exist; that is <see cref="FleetRoutes"/>, and it is the
/// route table — not this forwarder — that bounds the surface. Nothing here can be reached with a path
/// the table did not build.
/// </remarks>
public sealed class FleetProxy
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient? _http;

    public FleetProxy(FleetOptions? options, HttpMessageHandler? handler = null)
    {
        Options = options;
        if (options is not null)
        {
            _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            _http.BaseAddress = new Uri(options.BaseUrl + "/");
            _http.Timeout = Timeout;
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
    }

    public FleetOptions? Options { get; }

    /// <summary>Whether the host has an orchestrator to forward to; what the capability advertises.</summary>
    public bool IsConfigured => _http is not null;

    /// <summary>
    /// Sends <paramref name="method"/> to <paramref name="upstreamPath"/> (already assembled by the route
    /// table from vetted parts) with <paramref name="body"/> (already re-serialised from a typed record, or
    /// null), and writes the orchestrator's status, content type and body to <paramref name="context"/>.
    /// An orchestrator that cannot be reached is a 502 with a sentence, not a stack trace.
    /// </summary>
    public async Task ForwardAsync(HttpContext context, HttpMethod method, string upstreamPath, object? body, CancellationToken cancellationToken)
    {
        if (_http is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await context.Response.WriteAsJsonAsync(new { error = "This host has no fleet configured." }, cancellationToken);
            return;
        }

        using var request = new HttpRequestMessage(method, upstreamPath);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: BodyJson);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsJsonAsync(new { error = "The fleet could not be reached from the host: " + ex.Message }, cancellationToken);
            return;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            var contentType = response.Content.Headers.ContentType?.ToString();
            if (contentType is not null)
            {
                context.Response.ContentType = contentType;
            }
            await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
        }
    }

    private static readonly JsonSerializerOptions BodyJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
