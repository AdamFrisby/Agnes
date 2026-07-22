namespace Agnes.Abstractions;

/// <summary>The kind of action a permission option represents.</summary>
public enum PermissionOptionKind
{
    AllowOnce,
    AllowAlways,
    RejectOnce,
    RejectAlways,
}

/// <summary>One choice offered to the user in response to a permission request.</summary>
public sealed record PermissionOption(string OptionId, string Name, PermissionOptionKind Kind);

/// <summary>The resolved outcome of a permission request.</summary>
public enum PermissionOutcome
{
    Allowed,
    Denied,
    Cancelled,
}

/// <summary>One structured question the agent asks the user (part of a <see cref="QuestionAskedEvent"/>).
/// A single- or multi-select set of options, each with a short label + description, plus optional notes.</summary>
public sealed record AgentQuestion(
    string Id,
    string Header,
    string Prompt,
    IReadOnlyList<QuestionChoice> Options,
    bool MultiSelect = false,
    bool AllowFreeText = true);

/// <summary>One option offered for an <see cref="AgentQuestion"/>.</summary>
public sealed record QuestionChoice(string Label, string Description);

/// <summary>The user's answer to one <see cref="AgentQuestion"/>: the chosen option label(s) and any notes.</summary>
public sealed record QuestionAnswer(string QuestionId, IReadOnlyList<string> SelectedLabels, string? Notes = null);
