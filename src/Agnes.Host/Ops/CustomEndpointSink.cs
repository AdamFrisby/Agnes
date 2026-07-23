using System.Net.Http.Json;
using System.Text.Json;
using Agnes.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agnes.Host.Ops;

/// <summary>
/// Posts a bug report as JSON to a self-hoster's configured endpoint (an internal tracker, a webhook, …).
/// Enforces the payload byte cap LOCALLY, before any upload, so an oversized diagnostic payload can't be
/// sent and then relied on the server to reject; also bounds the send with a timeout so a large body can't
/// hang the client. The owner-only host-log attachment is deferred, so the payload is always null today —
/// the cap is enforced regardless, for when it lands.
/// </summary>
public sealed class CustomEndpointSink : IBugReportSink
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly long _maxPayloadBytes;
    private readonly TimeSpan _timeout;
    private readonly ILogger<CustomEndpointSink> _logger;

    public CustomEndpointSink(HttpClient http, string endpoint, long maxPayloadBytes, TimeSpan timeout, ILogger<CustomEndpointSink> logger)
    {
        _http = http;
        _endpoint = endpoint;
        _maxPayloadBytes = maxPayloadBytes;
        _timeout = timeout;
        _logger = logger;
    }

    public string Id => "custom-endpoint";

    public async Task<BugReportResult> SubmitAsync(BugReport report, CancellationToken ct = default)
    {
        // Reject an oversized diagnostic payload here, before we touch the network — never send it.
        if (report.DiagnosticPayload is { LongLength: var length } && length > _maxPayloadBytes)
        {
            return new BugReportResult(false, null,
                $"Diagnostic payload is {length} bytes, over the {_maxPayloadBytes}-byte cap; not uploaded.");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);

            using var response = await _http.PostAsync(
                _endpoint, JsonContent.Create(report, options: Json), timeoutCts.Token).ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? new BugReportResult(true, _endpoint, null)
                : new BugReportResult(false, null, $"Endpoint returned {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            _logger.LogWarning(ex, "Custom bug-report endpoint submission failed.");
            return new BugReportResult(false, null, ex.Message);
        }
    }
}
