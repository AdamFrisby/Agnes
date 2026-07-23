using System.Collections.Generic;
using System.Linq;
using Agnes.Abstractions;
using Agnes.Protocol;

namespace Agnes.Ui.Core.Onboarding;

/// <summary>A group of sign-in methods that share a real-world <see cref="AuthFlowKind"/> — the client shows
/// these under one heading ("Add this device" / "Restore access" / "Authorize a headless process") so the
/// user picks from the bucket that matches what they're actually trying to do.</summary>
/// <typeparam name="TOption">The per-method option shape the caller renders.</typeparam>
public sealed record AuthMethodBucket<TOption>(AuthFlowKind Kind, string Heading, IReadOnlyList<TOption> Methods);

/// <summary>
/// Buckets a host's advertised sign-in methods by <see cref="AuthFlowKind"/> (AC1). The host reports each
/// enabled method's kind in <see cref="AuthMethods.Flows"/>; when a host predates that field the kind is
/// derived from a per-method default, so bucketing still works against older hosts. Pure functions over the
/// wire data — no state — so the same logic is shared and unit-tested.
/// </summary>
public static class AuthMethodBuckets
{
    /// <summary>The method id the host advertises for each client-side <see cref="AuthMethodKind"/>, so a
    /// flow descriptor (keyed by id) can be matched to the client's method enum.</summary>
    public static string MethodId(AuthMethodKind kind) => kind switch
    {
        AuthMethodKind.Pairing => "pairing",
        AuthMethodKind.GitHub => "github",
        AuthMethodKind.Keypair => "keypair",
        AuthMethodKind.Oidc => "oidc",
        AuthMethodKind.Mtls => "mtls",
        _ => "",
    };

    /// <summary>The bucket a method falls in when the host doesn't report a kind (legacy host). Mirrors the
    /// host-side tags: keypair is the headless "connect a terminal" flow; everything else adds a device.</summary>
    public static AuthFlowKind DefaultFlow(AuthMethodKind kind) => kind switch
    {
        AuthMethodKind.Keypair => AuthFlowKind.ConnectTerminal,
        _ => AuthFlowKind.NewDevice,
    };

    /// <summary>Resolve a method's <see cref="AuthFlowKind"/>: the host's reported kind if present, else the
    /// per-method default.</summary>
    public static AuthFlowKind FlowFor(AuthMethods methods, AuthMethodKind kind)
    {
        var id = MethodId(kind);
        var descriptor = methods.Flows?.FirstOrDefault(f => f.MethodId == id);
        return descriptor?.Kind ?? DefaultFlow(kind);
    }

    /// <summary>The user-facing heading for a bucket.</summary>
    public static string Heading(AuthFlowKind kind) => kind switch
    {
        AuthFlowKind.NewDevice => "Add this device",
        AuthFlowKind.RestoreAccount => "Restore access",
        AuthFlowKind.ConnectTerminal => "Authorize a headless process",
        _ => "Sign in",
    };

    /// <summary>Group already-built options into buckets by their <see cref="AuthFlowKind"/>, preserving the
    /// input order within each bucket and ordering the buckets themselves in the enum's natural order
    /// (new device, restore, headless). Empty buckets are omitted.</summary>
    public static IReadOnlyList<AuthMethodBucket<TOption>> Group<TOption>(
        IEnumerable<TOption> options, System.Func<TOption, AuthFlowKind> flowOf)
    {
        return options
            .GroupBy(flowOf)
            .OrderBy(g => (int)g.Key)
            .Select(g => new AuthMethodBucket<TOption>(g.Key, Heading(g.Key), g.ToList()))
            .ToList();
    }
}
