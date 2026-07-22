# Global inbox & unified approvals

| | |
|---|---|
| **Category** | Notifications |
| **Plugin surface** | Core host/protocol feature — no new plugin interface, but composes with `INotificationChannel` (`01-push-notifications.md`) and, for the approval system, any future `ISharingBackend`/automation plugins |
| **Priority** | P1 |
| **Rough effort** | M |
| **Depends on** | `../00-plugin-architecture.md`; pairs naturally with `01-push-notifications.md` |

## Background

Agnes is built around running many agent sessions at once, possibly across multiple hosts. The more sessions someone runs in parallel, the more likely it is that several of them are simultaneously waiting on the human for something — a tool wants permission to run, an agent needs a structured answer to continue, or some other action needs a human's sign-off before it happens. Today, finding out "what needs me right now" means opening each session in turn and checking. That doesn't scale past a handful of sessions, and it's exactly the situation Agnes is meant to make easy: running a lot of agents without babysitting any single one of them.

The right fix is a **cross-session view** — one place that answers "what needs my attention across every session I can see," instead of a fact you can only discover per-session. There's also a related but distinct need: some actions in Agnes are consequential enough (committing to a repo, writing outside a sandbox, an external tool invoking a session-control action over MCP) that whether they should require human sign-off depends on *who or what* is asking, not just *what* the action is. Today Agnes has no general way to express "this action is fine when a human clicks it directly in the app, but should pause for approval when an agent or an external MCP client triggers it." Those are two separable problems — a read-mostly aggregation view, and a write-side gating mechanism — and it's worth keeping them separable rather than shipping them as one inseparable feature, since the aggregation view alone is valuable and much cheaper to build.

## Current state in Agnes

Agnes has permission requests today: `IAgentSession` emits `PermissionRequestedEvent` and exposes `RespondToPermissionAsync(requestId, optionId, ...)` (`Agnes.Abstractions/Agent.cs`), and `SessionViewModel`/`TranscriptBuilder` render them inline in a session's transcript. This is entirely **per-session** — there is no hub method or view model that unions open requests across sessions, so a user with a dozen active sessions has no single screen listing everything currently blocked on them.

There is also no generic "gate this action behind approval" mechanism. Host operations like `GitCommit` (`Agnes.Protocol/IAgnesHub.cs`, implemented in `Agnes.Host/Hosting/AgnesHub.cs`) are plain hub methods: any caller with a valid session-scoped connection can invoke them directly, with no notion of "this should require a human's explicit sign-off when it's the *agent* asking versus when the *user* clicked a button in the client."

## Proposed design

**Tier 1: global inbox.** This is mostly an aggregation view over data Agnes already has, which is what makes it worth shipping on its own first. `Agnes.Protocol` gains a hub method that unions open `PermissionRequestedEvent`s (and, once an agent surfaces one, structured "answer this to continue" requests) across every session the connected client is authorized to see, sorted by recency. `Agnes.Ui.Core` gets a dedicated `InboxViewModel` that renders this as one scrollable list with a jump-to-session action per item — the same underlying event data sessions already emit, just presented without requiring the user to know which session to look in.

**Tier 2: generic approval-gated actions.** This is a genuinely new abstraction, worth treating as a separate, larger investment:

```csharp
namespace Agnes.Abstractions;

public sealed record ActionSpec(string Id, string DisplayName, IReadOnlyList<string> ArgumentNames);

public interface IApprovalGatedAction
{
    ActionSpec Spec { get; }
    Task<ActionPreview?> PreviewAsync(IReadOnlyDictionary<string, string> arguments, CancellationToken ct = default);
    Task<ActionResult> ExecuteAsync(IReadOnlyDictionary<string, string> arguments, CancellationToken ct = default);
}

public enum ApprovalSurface { SessionAgent, ExternalMcp, Client, Automation }
```

Any host operation that wants approval-gating (a git commit, a write outside a sandbox boundary, an MCP-forwarded tool call, a future automation-triggered action) implements `IApprovalGatedAction` instead of being a bare hub method. `Agnes.Host` keeps a per-surface gating table — effectively `Dictionary<(ActionId, ApprovalSurface), bool>` — deciding whether invocations from a given surface execute immediately or must be approved first. When gated, invoking the action creates a durable `ApprovalRequest` (id, arguments, a human-readable summary, an optional preview of the effect, and a status of `open`/`approved`/`rejected`/`executed`/`failed`) that shows up in the same inbox as tier 1, rather than a transient modal that disappears if the user navigates away. Durability matters here specifically because approval requests can sit for a while — a user might not be looking at the app when an agent asks to commit, and the request needs to survive until they are.

**Default gating policy** should follow the same trust boundary Agnes already draws elsewhere: a human directly operating the client is trusted by definition (`AgentSessionOptions.SkipPermissions` already encodes "the user explicitly opted into the agent skipping approval," defaulting to `false` — i.e. Agnes's existing default posture is ask-first), while anything invoked *by* an agent, an external MCP client, or an automation is not automatically trusted just because it's plumbed through the same hub. So the sane default is: `Client`-surface invocations of an `IApprovalGatedAction` are ungated (a person clicking "commit" in the UI doesn't need to approve their own click), while `SessionAgent`, `ExternalMcp`, and `Automation` surfaces are gated by default. This mirrors, rather than duplicates, the reasoning already baked into `SkipPermissions`.

## Acceptance criteria

- Given a user has three sessions with open permission requests and one with none, when they open the global inbox, then all open requests from the three sessions appear in one list, sorted by recency, and the fourth session contributes nothing.
- Given an inbox item, when the user activates it, then the client navigates to the originating session with that request in view (not just a generic session list).
- Given a client the user is not authorized to see a particular host/session on, when they load the inbox, then that host's/session's requests are excluded — the inbox never leaks cross-tenant/cross-host data the client couldn't otherwise see.
- Given `GitCommit` is registered as an `IApprovalGatedAction` with `SessionAgent` gated and `Client` ungated, when the agent (not the user) triggers a commit action, then execution pauses and a durable `ApprovalRequest` appears in the inbox instead of the commit happening immediately.
- Given the same gating configuration, when the user clicks "commit" directly in the client UI, then it executes immediately with no approval step, because the `Client` surface is ungated by default.
- Given an `ApprovalRequest` is open, when the app is closed and reopened (or a different device with inbox access opens it), then the request is still present and answerable — approval state is durable, not tied to a single client session's lifetime.
- Non-regression: existing per-session permission-request behavior (inline in the transcript, answered via `RespondToPermissionAsync`) continues to work unchanged after the inbox aggregates the same data — the inbox is additive, not a replacement transport.

## Open questions

- Tier 2 (`IApprovalGatedAction`) is a meaningfully larger investment than tier 1 — worth shipping the inbox aggregation alone first and treating the generic approval-gating system as a later decision once there's a second or third concrete caller beyond `GitCommit`/MCP-forwarded actions to design against; a generic abstraction built for one caller tends to guess its shape wrong.
- Exact default gating table (which actions are gated on which surfaces out of the box) needs product judgment beyond the `SkipPermissions`-derived default above — worth a short explicit list reviewed before shipping rather than deciding it ad hoc per action as they're added.
- Should an `ApprovalRequest`'s preview (`ActionPreview`) be able to express partial/best-effort previews (e.g. "this write touches N files" without listing all of them for a huge change), or must every gated action support a full preview before it can opt in? Leaning toward allowing `PreviewAsync` to return `null` and rendering "no preview available" rather than blocking adoption on always having one.
