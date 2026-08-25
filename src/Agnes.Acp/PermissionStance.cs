namespace Agnes.Acp;

/// <summary>
/// What an ACP launch needs to know to choose its permission flags: whether the user opted out of being
/// asked, and whether the agent is confined.
/// </summary>
/// <param name="SkipPermissions">
/// The user's explicit opt-in to autonomous operation (<see cref="Agnes.Abstractions.AgentSessionOptions.SkipPermissions"/>).
/// </param>
/// <param name="Sandboxed">
/// Whether the agent runs inside a sandbox rather than on the host
/// (<see cref="Agnes.Abstractions.AgentSessionOptions.Sandbox"/>).
/// </param>
/// <remarks>
/// A record rather than a second positional <c>bool</c>, because the two facts are read together and
/// <c>Build(true, false)</c> at a call site says nothing about which is which.
///
/// <para>The distinction earns its place because a CLI's own guardrails are worth different amounts in the
/// two cases. Copilot verifies filesystem paths independently of its tool-permission prompt, and on an
/// unsandboxed host that check is the real boundary — it should survive the user waiving the per-tool
/// prompt. Inside a VM it guards nothing the VM does not already guard, while still emitting prompts into
/// a session the user has explicitly said not to interrupt them about.</para>
/// </remarks>
public readonly record struct PermissionStance(bool SkipPermissions, bool Sandboxed);
