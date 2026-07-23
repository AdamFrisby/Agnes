using System.Text.Json;
using System.Text.Json.Serialization;
using Agnes.Host.Hosting;
using Agnes.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Agnes.Host.Attention;

/// <summary>The create-request body (snake_case on the wire, per the public API schema). All fields optional
/// at the boundary so validation — not a bind failure — produces the 400s with useful messages.</summary>
public sealed record CreateAttentionRequestDto(
    string? Source,
    string? Question,
    IReadOnlyList<string>? Options,
    string? CallbackUrl,
    int? TimeoutSeconds);

/// <summary>The create response — just the id the caller polls / correlates the callback on.</summary>
public sealed record AttentionRequestCreatedDto(string RequestId);

/// <summary>The poll response: current status and the answer once available.</summary>
public sealed record AttentionRequestStatusDto(
    string RequestId,
    string Source,
    string Question,
    IReadOnlyList<string> Options,
    string Status,
    string? Answer,
    DateTimeOffset CreatedAt);

/// <summary>
/// The public REST surface for external attention requests: create and poll. Authenticated with an Agnes
/// device token (the same bearer style as <c>/devices</c>), and SCOPED to the owning caller — a request is
/// only readable via the token that created it. Kept as a small static mapper called from <c>Program.cs</c>,
/// matching the other endpoint groups. JSON is snake_case both ways to match the documented schema.
/// </summary>
public static class AttentionEndpoints
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static void MapAttentionEndpoints(this WebApplication app, DeviceRegistry tokens, AttentionRequestService service)
    {
        // Create a new attention request. Owner = the authenticated caller (device/token id).
        app.MapPost("/v1/attention-requests", async (HttpContext ctx) =>
        {
            var caller = ResolveCaller(ctx, tokens);
            if (caller is null)
            {
                return Results.Unauthorized();
            }

            CreateAttentionRequestDto? dto;
            try
            {
                dto = await ctx.Request.ReadFromJsonAsync<CreateAttentionRequestDto>(Json, ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return Problem("Request body is not valid JSON.");
            }

            if (dto is null)
            {
                return Problem("A JSON body is required.");
            }

            if (string.IsNullOrWhiteSpace(dto.Source))
            {
                return Problem("'source' is required.");
            }

            if (string.IsNullOrWhiteSpace(dto.Question))
            {
                return Problem("'question' is required.");
            }

            var options = (dto.Options ?? [])
                .Where(o => !string.IsNullOrWhiteSpace(o))
                .Select(o => o.Trim())
                .ToArray();
            if (options.Length == 0)
            {
                return Problem("'options' must contain at least one non-empty choice.");
            }

            if (dto.CallbackUrl is { Length: > 0 } url
                && !(Uri.TryCreate(url, UriKind.Absolute, out var parsed)
                     && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)))
            {
                return Problem("'callback_url' must be an absolute http(s) URL.");
            }

            if (dto.TimeoutSeconds is { } t && t <= 0)
            {
                return Problem("'timeout_seconds' must be positive when supplied.");
            }

            var created = service.Create(
                caller, dto.Source!.Trim(), dto.Question!.Trim(), options,
                string.IsNullOrWhiteSpace(dto.CallbackUrl) ? null : dto.CallbackUrl,
                dto.TimeoutSeconds);

            return Results.Json(new AttentionRequestCreatedDto(created.Id), Json, statusCode: StatusCodes.Status201Created);
        });

        // Poll a request the caller owns. A request created by a different caller (or an unknown id) is a 404
        // — never distinguish "not yours" from "doesn't exist", so existence doesn't leak across callers.
        app.MapGet("/v1/attention-requests/{id}", (HttpContext ctx, string id) =>
        {
            var caller = ResolveCaller(ctx, tokens);
            if (caller is null)
            {
                return Results.Unauthorized();
            }

            var request = service.GetForOwner(id, caller);
            return request is null
                ? Results.NotFound()
                : Results.Json(ToStatusDto(request), Json);
        });
    }

    private static string? ResolveCaller(HttpContext ctx, DeviceRegistry tokens)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..]
            : ctx.Request.Query[WireProtocol.TokenParameter].ToString();
        return tokens.ResolveCallerId(token);
    }

    private static AttentionRequestStatusDto ToStatusDto(AttentionRequest r)
        => new(r.Id, r.Source, r.Question, r.Options,
            r.Status.ToString().ToLowerInvariant(), r.Answer, r.CreatedAt);

    private static IResult Problem(string message)
        => Results.Json(new { error = message }, Json, statusCode: StatusCodes.Status400BadRequest);
}
