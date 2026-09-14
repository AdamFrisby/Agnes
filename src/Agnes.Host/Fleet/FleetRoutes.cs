using System.Text.Json;
using Agnes.Host.Hosting;
using Agnes.Protocol;

namespace Agnes.Host.Fleet;

/// <summary>
/// The fleet surface: the orchestrator routes the Agnes apps have a control for, and no others.
/// </summary>
/// <remarks>
/// <para>This is deliberately not a reverse proxy. Every route here is mapped by hand, its path is
/// assembled from parts this table vetted (an id is a short token, never a path), a query string is
/// forwarded only where a route names the keys it takes, and a body is read into a typed record and
/// re-serialised — so a field the record does not have never reaches the orchestrator. A route the
/// orchestrator grows tomorrow does not exist here until someone adds it, on purpose, with a control
/// that uses it. That is the point: a new orchestrator feature with unknown security characteristics
/// cannot be reached through a paired phone by accident.</para>
/// <para>Reads take any paired device. Anything that changes the fleet — retry, replay, promote, cancel,
/// answer, dismiss, raise a ceiling — takes an Owner, the same line the host draws for its own
/// configuration. The device token never leaves the host; the orchestrator sees the host's key.</para>
/// <para>What is here is what the Android head shows: the overview, the wall, the queue, an item's
/// page and decision card, and the inbox's questions. The desktop plugin's wider surface (suggestions,
/// releases, test cases, prompt edits, supervision) is not proxied; the desktop runs beside the
/// orchestrator and reads it directly.</para>
/// </remarks>
public static class FleetRoutes
{
    /// <summary>An orchestrator id: letters, digits, dot, dash, underscore — never a slash, never empty.</summary>
    private const string Id = "{id:regex(^[A-Za-z0-9._-]{{1,80}}$)}";

    /// <summary>What "raise the ceiling" sends: the one field the decision card changes.</summary>
    public sealed record RaiseCeilingBody(int? AuditMaxIterations);

    /// <summary>What the answer button sends.</summary>
    public sealed record AnswerBody(string? QuestionId, string? Answer);

    /// <summary>What the dismiss button sends.</summary>
    public sealed record DismissBody(string? QuestionId, string? Reason);

    public static void MapFleet(this WebApplication app)
    {
        var fleet = app.MapGroup("/fleet");

        // ---- reads: the overview, the wall, the queue ----
        Read(fleet, "workitems", "workitems");
        Read(fleet, "queue/status", "queue/status");
        Read(fleet, "workers/status", "workers/status");
        Read(fleet, "concurrency", "concurrency");
        Read(fleet, "quota", "quota");
        Read(fleet, "quota/history", "quota/history", query: ["agent", "from", "limit"]);
        Read(fleet, "fleet/transition-health", "fleet/transition-health");
        Read(fleet, "projects", "projects");

        // ---- reads: one item's page, decision card and evidence ----
        Read(fleet, $"workitems/{Id}/questions", "workitems/{0}/questions");
        Read(fleet, $"workitems/{Id}/timeline", "workitems/{0}/timeline");
        Read(fleet, $"workitems/{Id}/agent-history", "workitems/{0}/agent-history");
        Read(fleet, $"workitems/{Id}/audit-progress", "workitems/{0}/audit-progress");
        Read(fleet, $"workitems/{Id}/diff", "workitems/{0}/diff");
        Read(fleet, $"workitems/{Id}/stdout-tail", "workitems/{0}/stdout-tail");

        // ---- writes: the decision card's buttons, Owner only ----
        Write(fleet, HttpMethod.Post, $"workitems/{Id}/retry", "workitems/{0}/retry");
        Write(fleet, HttpMethod.Post, $"workitems/{Id}/replay", "workitems/{0}/replay");
        Write(fleet, HttpMethod.Post, $"workitems/{Id}/promote", "workitems/{0}/promote");
        Write(fleet, HttpMethod.Delete, $"workitems/{Id}", "workitems/{0}");
        Write<RaiseCeilingBody>(fleet, HttpMethod.Patch, $"workitems/{Id}", "workitems/{0}",
            body => body.AuditMaxIterations is > 0 and <= 1000 ? new { auditMaxIterations = body.AuditMaxIterations } : null);
        Write<AnswerBody>(fleet, HttpMethod.Post, $"workitems/{Id}/answer", "workitems/{0}/answer",
            body => body is { QuestionId.Length: > 0, Answer: not null } ? new { questionId = body.QuestionId, answer = body.Answer } : null);
        Write<DismissBody>(fleet, HttpMethod.Post, $"workitems/{Id}/dismiss-question", "workitems/{0}/dismiss-question",
            body => body is { QuestionId.Length: > 0, Reason.Length: > 0 } ? new { questionId = body.QuestionId, reason = body.Reason } : null);

        // Everything else under /fleet is not a route, whatever the orchestrator would say to it.
        fleet.MapFallback(ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return ctx.Response.WriteAsJsonAsync(new { error = "Not part of the fleet surface this host offers." });
        });
    }

    private static void Read(RouteGroupBuilder fleet, string pattern, string upstream, string[]? query = null)
        => fleet.MapGet(pattern, (HttpContext ctx, FleetProxy proxy, DeviceRegistry devices, CancellationToken ct) =>
        {
            if (!devices.IsValid(Token(ctx)))
            {
                return Results.Unauthorized().ExecuteAsync(ctx);
            }

            var path = Upstream(ctx, upstream);
            if (query is { Length: > 0 })
            {
                var kept = query
                    .Where(k => ctx.Request.Query.ContainsKey(k))
                    .Select(k => $"{k}={Uri.EscapeDataString(ctx.Request.Query[k].ToString())}")
                    .ToList();
                if (kept.Count > 0)
                {
                    path += "?" + string.Join('&', kept);
                }
            }

            return proxy.ForwardAsync(ctx, HttpMethod.Get, path, body: null, ct);
        });

    private static void Write(RouteGroupBuilder fleet, HttpMethod method, string pattern, string upstream)
        => fleet.MapMethods(pattern, [method.Method], (HttpContext ctx, FleetProxy proxy, DeviceRegistry devices, CancellationToken ct) =>
            Owner(ctx, devices) ?? proxy.ForwardAsync(ctx, method, Upstream(ctx, upstream), body: null, ct));

    /// <summary>A write with a body: read into <typeparamref name="TBody"/>, handed to <paramref name="shape"/>
    /// to become exactly what the orchestrator is sent — or null, which is a 400 and nothing forwarded.</summary>
    private static void Write<TBody>(RouteGroupBuilder fleet, HttpMethod method, string pattern, string upstream, Func<TBody, object?> shape)
        => fleet.MapMethods(pattern, [method.Method], async (HttpContext ctx, FleetProxy proxy, DeviceRegistry devices, CancellationToken ct) =>
        {
            if (Owner(ctx, devices) is { } refused)
            {
                await refused;
                return;
            }

            TBody? body;
            try
            {
                body = await ctx.Request.ReadFromJsonAsync<TBody>(BodyJson, ct);
            }
            catch (JsonException)
            {
                body = default;
            }

            var forwarded = body is null ? null : shape(body);
            if (forwarded is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new { error = "The body is not the shape this control sends." }, ct);
                return;
            }

            await proxy.ForwardAsync(ctx, method, Upstream(ctx, upstream), forwarded, ct);
        });

    /// <summary>Null when the caller is an Owner; otherwise the refusal to write.</summary>
    private static Task? Owner(HttpContext ctx, DeviceRegistry devices)
    {
        var token = Token(ctx);
        if (!devices.IsValid(token))
        {
            return Results.Unauthorized().ExecuteAsync(ctx);
        }

        return devices.IsOwner(devices.ResolveCallerId(token))
            ? null
            : Results.Json(new { error = "Changing the fleet takes an Owner device." }, statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(ctx);
    }

    private static string Upstream(HttpContext ctx, string upstream)
        => ctx.Request.RouteValues.TryGetValue("id", out var id) && id is string s
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, upstream, Uri.EscapeDataString(s))
            : upstream;

    private static string Token(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..]
            : ctx.Request.Query[WireProtocol.TokenParameter].ToString();
    }

    private static readonly JsonSerializerOptions BodyJson = new(JsonSerializerDefaults.Web);
}
