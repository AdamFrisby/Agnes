namespace Agnes.Abstractions;

/// <summary>
/// Where an approval-gated invocation came from — the trust boundary Agnes gates on (notifications/02 tier 2).
/// The same action can execute immediately from one surface yet require human sign-off from another: a person
/// clicking a button in the client is trusted by definition, whereas the same action asked for <em>by</em> an
/// agent, an external MCP client, or an automation is not automatically trusted just because it is plumbed
/// through the same hub.
/// </summary>
public enum ApprovalSurface
{
    /// <summary>The in-session agent itself asked for the action (e.g. a tool call).</summary>
    SessionAgent,

    /// <summary>An external MCP client forwarded the action through the host.</summary>
    ExternalMcp,

    /// <summary>A human directly operating an Agnes client triggered the action.</summary>
    Client,

    /// <summary>A scheduled/automation trigger initiated the action.</summary>
    Automation,
}

/// <summary>
/// A consequential host operation that can be gated behind human approval instead of being a bare hub method
/// (notifications/02 tier 2). Implementations are immutable, argument-carrying records: the constructor
/// captures everything <see cref="ExecuteAsync"/> needs, so a gated invocation can be parked as a durable
/// request and run later — after a human approves it — with no further arguments. <see cref="Summary"/> and
/// <see cref="Preview"/> are what the inbox renders while the request waits; a null <see cref="Preview"/>
/// simply renders "no preview available" rather than blocking adoption.
/// </summary>
public interface IApprovalGatedAction
{
    /// <summary>Stable identifier for the kind of action (e.g. <c>"git.commit"</c>) — the key, together with a
    /// <see cref="ApprovalSurface"/>, into the host's gating table.</summary>
    string ActionId { get; }

    /// <summary>Human-readable one-line description of this specific invocation (its arguments folded in).</summary>
    string Summary { get; }

    /// <summary>Optional preview of the effect (e.g. the diff or file count); null ⇒ no preview available.</summary>
    string? Preview { get; }

    /// <summary>Performs the action. Only ever called once, and only after the invocation is either ungated or
    /// explicitly approved by a human.</summary>
    Task ExecuteAsync(CancellationToken ct = default);
}
