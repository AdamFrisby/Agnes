using System.Collections.Generic;
using System.Windows.Input;
using Agnes.Ui.Core.Onboarding;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// Drives the onboarding showcase: a next/prev/dismiss walk over a <em>data-driven</em> list of
/// <see cref="FeatureCard"/>s. The card list is injected, so adding or removing a highlighted feature needs no
/// change here — this same view model can serve a "what's new" surface just by handing it a different list.
/// <para>
/// Shown-once is keyed by an app version: <see cref="ShouldAutoShow"/> is true only until <see cref="Dismiss"/>
/// records that version in the store. After that it never auto-shows again for that version, but
/// <see cref="Show"/> keeps it reachable manually (e.g. from a help/about menu).
/// </para>
/// </summary>
public sealed class ShowcaseViewModel : ObservableObject
{
    private readonly IReadOnlyList<FeatureCard> _cards;
    private readonly IOnboardingStore _store;
    private readonly string _appVersion;

    public ShowcaseViewModel(IReadOnlyList<FeatureCard> cards, IOnboardingStore store, string appVersion)
    {
        _cards = cards;
        _store = store;
        _appVersion = appVersion;

        NextCommand = new RelayCommand(Next, () => HasNext);
        PrevCommand = new RelayCommand(Prev, () => HasPrev);
        DismissCommand = new RelayCommand(Dismiss);
    }

    /// <summary>All cards in order — the renderer iterates this without knowing any individual card.</summary>
    public IReadOnlyList<FeatureCard> Cards => _cards;

    /// <summary>Number of steps in the sequence.</summary>
    public int StepCount => _cards.Count;

    private bool _isOpen;
    /// <summary>Whether the showcase overlay is currently visible.</summary>
    public bool IsOpen { get => _isOpen; private set => SetProperty(ref _isOpen, value); }

    private int _index;
    /// <summary>Zero-based index of the current card.</summary>
    public int Index
    {
        get => _index;
        private set
        {
            if (SetProperty(ref _index, value))
            {
                OnPropertyChanged(nameof(Current));
                OnPropertyChanged(nameof(HasNext));
                OnPropertyChanged(nameof(HasPrev));
                OnPropertyChanged(nameof(IsLast));
                OnPropertyChanged(nameof(StepLabel));
                (NextCommand as RelayCommand)?.NotifyCanExecuteChanged();
                (PrevCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>The card at <see cref="Index"/>, or null if the list is empty.</summary>
    public FeatureCard? Current => _cards.Count == 0 ? null : _cards[_index];

    public bool HasNext => _index < _cards.Count - 1;
    public bool HasPrev => _index > 0;
    public bool IsLast => _index >= _cards.Count - 1;

    /// <summary>A "n of N" progress label for the header.</summary>
    public string StepLabel => _cards.Count == 0 ? string.Empty : $"{_index + 1} of {_cards.Count}";

    public ICommand NextCommand { get; }
    public ICommand PrevCommand { get; }
    public ICommand DismissCommand { get; }

    /// <summary>Whether the showcase should auto-appear on this launch — true until this app version's showcase
    /// has been dismissed once. Independent of <see cref="Show"/>, which always works for a manual re-open.</summary>
    public bool ShouldAutoShow => _store.Load().ShowcaseShownVersion != _appVersion;

    /// <summary>Open the showcase from the first card. Used both for the first-run auto-show and a manual re-open.</summary>
    public void Show()
    {
        Index = 0;
        IsOpen = true;
    }

    private void Next()
    {
        if (HasNext)
        {
            Index++;
        }
    }

    private void Prev()
    {
        if (HasPrev)
        {
            Index--;
        }
    }

    /// <summary>Close the showcase and record this app version as shown, so it won't auto-appear again.</summary>
    public void Dismiss()
    {
        IsOpen = false;
        _store.Save(_store.Load() with { ShowcaseShownVersion = _appVersion });
    }
}
