namespace Agnes.Ui.Core.ViewModels;

/// <summary>The high-level state of a session — what it's doing / whether it needs you.</summary>
public enum SessionActivity
{
    /// <summary>Nothing in flight, no pending changes.</summary>
    Idle,

    /// <summary>A turn is running.</summary>
    Running,

    /// <summary>Blocked awaiting the user (a permission request).</summary>
    NeedsInput,

    /// <summary>A turn finished and there are changes to review.</summary>
    ReadyForReview,

    /// <summary>The last turn errored / was interrupted.</summary>
    Error,
}
