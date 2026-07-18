using Agnes.Abstractions;

namespace Agnes.Ui.Core;

/// <summary>
/// A client-side trust policy: remembers "always allow / always reject" decisions per host + tool
/// kind and auto-answers matching permission requests. Enforced by the client (the agent still
/// sees an ordinary response); every auto-decision is still logged in the session's audit trail.
/// </summary>
public interface IPermissionPolicy
{
    /// <summary>Returns true to auto-allow, false to auto-reject, or null to ask the user.</summary>
    bool? Decide(string hostUrl, ToolKind? toolKind);

    /// <summary>Records a standing decision for this host + tool kind.</summary>
    void Remember(string hostUrl, ToolKind? toolKind, bool allow);

    /// <summary>Forgets a standing decision (returns to asking).</summary>
    void Forget(string hostUrl, ToolKind? toolKind);
}

/// <summary>Default policy: never decides automatically.</summary>
public sealed class NullPermissionPolicy : IPermissionPolicy
{
    public static readonly NullPermissionPolicy Instance = new();

    public bool? Decide(string hostUrl, ToolKind? toolKind) => null;
    public void Remember(string hostUrl, ToolKind? toolKind, bool allow) { }
    public void Forget(string hostUrl, ToolKind? toolKind) { }
}
