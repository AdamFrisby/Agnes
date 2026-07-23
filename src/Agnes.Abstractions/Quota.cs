namespace Agnes.Abstractions;

/// <summary>
/// A point-in-time reading of a connected-service account's usage against its plan. Purely informational —
/// it never gates whether a profile can be used, only lets a client warn "this account is nearly out" before
/// a long autonomous run fails mid-task with a provider-side quota error. Carried whole across the wire (it
/// holds no secret — only plan/meter numbers), like the other simple domain records.
/// </summary>
/// <param name="PlanLabel">Human-friendly plan/tier name to caption the badge (e.g. "Pro", "Team", "Free").</param>
/// <param name="Meters">The individual usage meters for this account (tokens, requests, credits, …). May be empty.</param>
/// <param name="FetchedAt">
/// When the underlying data was actually retrieved from the provider — NOT necessarily "now", since the host
/// serves repeated requests from a cache. A client shows this as an explicit "as of" staleness indicator so a
/// slightly-stale number is still honestly labelled rather than passed off as live.
/// </param>
public sealed record QuotaSnapshot(string PlanLabel, IReadOnlyList<QuotaMeter> Meters, DateTimeOffset FetchedAt);

/// <summary>
/// One usage dimension within a <see cref="QuotaSnapshot"/>. Every numeric field is nullable because a
/// provider may expose a used count without a hard limit (or vice versa); a client renders a meter bar only
/// when both are present and falls back to the raw number otherwise.
/// </summary>
/// <param name="Name">What this meter measures (e.g. "Monthly messages", "Input tokens").</param>
/// <param name="Used">How much has been consumed, or null when the provider doesn't report it.</param>
/// <param name="Limit">The plan's ceiling for this meter, or null when there's no fixed limit / it's unknown.</param>
/// <param name="Unit">Optional unit caption (e.g. "tokens", "requests", "USD"); null when the name is self-describing.</param>
public sealed record QuotaMeter(string Name, double? Used, double? Limit, string? Unit);

/// <summary>
/// OPTIONAL capability an <see cref="IConnectedServiceProvider"/> implements only when its provider exposes a
/// usable usage/quota API — most don't, and rather than forcing every provider to stub out a method it can't
/// honour, this follows Agnes's optional-capability pattern (like <c>IPausableSandbox</c>/<c>IStoppableSandbox</c>
/// in <c>Agnes.Sandbox</c>): the host checks for it with an <c>is</c> test and treats its absence as "quota not
/// supported" rather than an error. Reporting quota is purely additive — a provider without it still resolves
/// credentials and runs sessions exactly as before.
/// </summary>
/// <remarks>
/// The contract is a pure function of a profile id: given a profile, return its current usage or null when
/// unknown/unavailable. Implementations must NOT throw for an ordinary "no data" case (a provider hiccup,
/// nothing configured) — they return null so the host can surface a clean "unavailable" state instead of a
/// crash. The host wraps this in a caching layer (see the host's <c>QuotaService</c>) so a provider's usage
/// endpoint is hit at most once per staleness window, never on every UI paint.
/// </remarks>
public interface IQuotaReportingProvider
{
    /// <summary>
    /// Reads the current quota snapshot for <paramref name="profileId"/>, or null when this provider can't
    /// report usage for it (not connected, no usage API, a transient fetch failure). Should not throw for an
    /// ordinary no-data case — return null so the caller can render "unavailable" cleanly.
    /// </summary>
    Task<QuotaSnapshot?> GetQuotaAsync(string profileId, CancellationToken ct = default);
}
