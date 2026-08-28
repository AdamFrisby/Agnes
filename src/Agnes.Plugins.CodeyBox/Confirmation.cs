using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Plugins.CodeyBox;

/// <summary>
/// A pending irreversible action, held until the operator confirms it.
/// </summary>
/// <remarks>
/// Cancel, abandon, delete and dispose all used to fire on one click, styled identically to retry and
/// promote and sitting in the same row — abandoning a work item was one position from uncancelling one.
/// Nothing about the control said the two differed, and nothing could undo the wrong choice.
///
/// <para>A bar rather than a modal dialog: the plugin renders inside a host tab and should not seize the
/// whole window, and the bar can name the exact item, which a generic "are you sure?" cannot. Arming is
/// explicit and cancelling is the wider target, so the accidental path is the safe one.</para>
/// </remarks>
public sealed partial class Confirmation : ObservableObject
{
    private Func<Task>? _action;

    public Confirmation() => ConfirmCommand = new AsyncRelayCommand(RunAsync);

    /// <summary>What is about to happen, in the operator's words and naming the subject — "Abandon
    /// 43c8ec28?" rather than "Are you sure?".</summary>
    [ObservableProperty]
    private string _prompt = string.Empty;

    /// <summary>The word on the confirming button, so it restates the act rather than saying "OK".</summary>
    [ObservableProperty]
    private string _verb = "Confirm";

    [ObservableProperty]
    private bool _isPending;

    public IAsyncRelayCommand ConfirmCommand { get; }

    public IRelayCommand DismissCommand => _dismiss ??= new RelayCommand(Clear);

    private IRelayCommand? _dismiss;

    /// <summary>Arms a confirmation. Replaces any pending one, so two requests cannot queue up and have
    /// the second silently answered by a click aimed at the first.</summary>
    public void Ask(string verb, string subject, Func<Task> action)
    {
        Verb = verb;
        Prompt = $"{verb} {subject}? This cannot be undone.";
        _action = action;
        IsPending = true;
    }

    private async Task RunAsync()
    {
        var action = _action;
        Clear();
        if (action is not null)
        {
            await action().ConfigureAwait(false);
        }
    }

    private void Clear()
    {
        _action = null;
        IsPending = false;
        Prompt = string.Empty;
    }
}
