using CommunityToolkit.Mvvm.ComponentModel;

namespace Agnes.App.Mobile.ViewModels;

/// <summary>
/// A bottom sheet: the mobile stand-in for the desktop client's side panels and flyouts.
///
/// Everything secondary about a session — the changed files, the tool timeline, git, session info —
/// lives in one of these rather than in a permanently-visible panel. A phone has one column, so
/// detail is summoned and dismissed, not tiled.
/// </summary>
public abstract partial class SheetViewModel : ObservableObject
{
    public abstract string Title { get; }

    /// <summary>Optional line under the title, for context the sheet's rows would otherwise repeat.</summary>
    public virtual string? Subtitle => null;

    /// <summary>How tall the sheet opens, as a fraction of the screen. Short sheets (a menu, a picker)
    /// shouldn't cover the content they act on.</summary>
    public virtual double HeightFraction => 0.72;

    /// <summary>Raised when the sheet wants to close itself (an action inside it completed).</summary>
    public event Action? CloseRequested;

    protected void Close() => CloseRequested?.Invoke();
}
