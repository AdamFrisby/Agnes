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
