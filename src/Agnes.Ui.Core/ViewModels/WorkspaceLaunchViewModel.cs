using System.Collections.ObjectModel;
using Agnes.Abstractions;
using Agnes.Client;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// The "which checkout" step of the new-session flow (multi-machine workspace model, <c>connectivity/05</c>).
/// When a session is being started against a <see cref="Workspace"/> that has more than one active
/// <see cref="Checkout"/> across the connected hosts, this offers the checkouts (each labelled with its host
/// and branch) so the user picks which machine's copy to run on, rather than defaulting silently to an
/// arbitrary one. A single-checkout workspace preselects it and reports <see cref="RequiresCheckoutChoice"/>
/// false, so simple setups get no extra step.
/// </summary>
public sealed class WorkspaceLaunchViewModel : ObservableObject
{
    private CheckoutChoice? _selectedCheckout;

    public WorkspaceLaunchViewModel(WorkspaceCheckouts workspace)
    {
        Workspace = workspace.Workspace;
        foreach (var checkout in workspace.Checkouts)
        {
            Choices.Add(new CheckoutChoice(checkout));
        }

        // Single checkout: preselect it so launching is one step. Multiple: leave unset until the user chooses.
        _selectedCheckout = Choices.Count == 1 ? Choices[0] : null;
    }

    /// <summary>The logical project this launch targets.</summary>
    public Workspace Workspace { get; }

    /// <summary>The available checkouts to launch against (one per host that has this workspace checked out).</summary>
    public ObservableCollection<CheckoutChoice> Choices { get; } = [];

    /// <summary>Whether the user must pick a checkout (more than one exists). False for a single-checkout
    /// workspace — the existing single-host behaviour, with no extra step.</summary>
    public bool RequiresCheckoutChoice => Choices.Count > 1;

    /// <summary>The chosen checkout (preselected when there's exactly one).</summary>
    public CheckoutChoice? SelectedCheckout
    {
        get => _selectedCheckout;
        set
        {
            if (SetProperty(ref _selectedCheckout, value))
            {
                OnPropertyChanged(nameof(CanLaunch));
            }
        }
    }

    /// <summary>Whether a launch can proceed — a checkout has been chosen (always true for a single-checkout
    /// workspace, since it's preselected).</summary>
    public bool CanLaunch => _selectedCheckout is not null;
}

/// <summary>One checkout offered in the new-session picker, as a bindable row tagged with its host and branch.</summary>
public sealed class CheckoutChoice
{
    public CheckoutChoice(Checkout checkout) => Checkout = checkout;

    public Checkout Checkout { get; }

    public string CheckoutId => Checkout.Id;
    public string HostId => Checkout.HostId;
    public string Path => Checkout.Path;
    public string? Branch => Checkout.Branch;

    /// <summary>A one-line label for the row: the host and the branch it's on.</summary>
    public string Label => $"{HostId} · {Branch ?? "(no branch)"}";
}
