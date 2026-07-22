using System;
using System.Threading.Tasks;
using Agnes.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.App.Desktop.ViewModels;

/// <summary>
/// Backs the "Fork session" dialog: an editable target folder (host-proposed, numeral-incremented) and,
/// when the source is sandboxed on a cloner-capable host, a "copy the sandbox (CoW)" toggle. The confirm
/// button runs the fork; errors and a busy state keep the dialog open until it succeeds or is cancelled.
/// </summary>
public sealed partial class ForkPrompt : ObservableObject
{
    [ObservableProperty]
    private string _targetDirectory = string.Empty;

    [ObservableProperty]
    private bool _copySandbox;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    [ObservableProperty]
    private bool _busy;

    public ForkPrompt(string sourceTitle, ForkPlan plan, Func<ForkPrompt, Task> onConfirm, Action onCancel)
    {
        SourceTitle = sourceTitle;
        SourceDirectory = plan.SourceDirectory;
        CanCopySandbox = plan.CanCopySandbox;
        _targetDirectory = plan.ProposedDirectory;
        _copySandbox = plan.CanCopySandbox; // default on when available
        ConfirmCommand = new AsyncRelayCommand(() => onConfirm(this));
        CancelCommand = new RelayCommand(onCancel);
    }

    public string SourceTitle { get; }
    public string SourceDirectory { get; }
    public bool CanCopySandbox { get; }
    public IAsyncRelayCommand ConfirmCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);
}
