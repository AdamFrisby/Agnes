using Agnes.Abstractions;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>One entry in a session's audit trail of capabilities granted or denied.</summary>
public sealed class PermissionAuditEntry
{
    public PermissionAuditEntry(string title, PermissionOutcome outcome, string? optionId, DateTimeOffset when)
    {
        Title = title;
        Outcome = outcome;
        OptionId = optionId;
        When = when;
    }

    public string Title { get; }
    public PermissionOutcome Outcome { get; }
    public string? OptionId { get; }
    public DateTimeOffset When { get; }

    public bool Allowed => Outcome == PermissionOutcome.Allowed;
    public string OutcomeText => Outcome.ToString();
    public string TimeText => When.ToLocalTime().ToString("HH:mm:ss");
}
