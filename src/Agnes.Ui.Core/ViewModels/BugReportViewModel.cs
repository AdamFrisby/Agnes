using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Ui.Core.ViewModels;

/// <summary>
/// Drives the "Report a bug" flow (see <c>.ideas/ops/01-bug-reports-and-diagnostics.md</c>): the user fills
/// in a title/summary and optional current/expected behaviour, and submits to whatever
/// <see cref="IAgnesHost"/> the accessor returns. If the host surfaces likely-duplicate issues, they're shown
/// with an open-in-browser affordance so the user can comment instead of filing a new one. If the host is
/// unreachable or bug reporting is unavailable, it falls back to opening a prefilled public GitHub new-issue
/// URL — never carrying any private diagnostic payload. Host-agnostic, so it drives a real host and the
/// in-memory simulation identically.
/// </summary>
public sealed class BugReportViewModel : ObservableObject
{
    private readonly Func<IAgnesHost?> _host;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action<string> _openUrl;
    private readonly string _repo;

    public BugReportViewModel(Func<IAgnesHost?> host, IUiDispatcher dispatcher, Action<string> openUrl, string repo = "AdamFrisby/Agnes")
    {
        _host = host;
        _dispatcher = dispatcher;
        _openUrl = openUrl;
        _repo = repo;

        SubmitCommand = new AsyncRelayCommand(SubmitAsync);
        OpenDuplicateCommand = new RelayCommand<DuplicateIssue>(d => { if (d is not null) { _openUrl(d.Url); } });
    }

    /// <summary>Likely-duplicate open issues the host found for this title (empty until a submit surfaces some).</summary>
    public ObservableCollection<DuplicateIssue> Duplicates { get; } = [];

    private string _title = string.Empty;
    public string Title { get => _title; set => SetProperty(ref _title, value); }

    private string _summary = string.Empty;
    public string Summary { get => _summary; set => SetProperty(ref _summary, value); }

    private string _currentBehavior = string.Empty;
    public string CurrentBehavior { get => _currentBehavior; set => SetProperty(ref _currentBehavior, value); }

    private string _expectedBehavior = string.Empty;
    public string ExpectedBehavior { get => _expectedBehavior; set => SetProperty(ref _expectedBehavior, value); }

    private string _status = string.Empty;
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { if (SetProperty(ref _isBusy, value)) { OnPropertyChanged(nameof(HasDuplicates)); } } }

    public bool HasDuplicates => Duplicates.Count > 0;

    public ICommand SubmitCommand { get; }
    public ICommand OpenDuplicateCommand { get; }

    private async Task SubmitAsync()
    {
        if (string.IsNullOrWhiteSpace(Title))
        {
            Status = "Please enter a short title first.";
            return;
        }

        IsBusy = true;
        Status = "Submitting…";
        _dispatcher.Post(Duplicates.Clear);

        var report = new BugReport(Title, Summary, NullIfBlank(CurrentBehavior), NullIfBlank(ExpectedBehavior), DiagnosticPayload: null);
        var dto = new BugReportDto(report.Title, report.Summary, report.CurrentBehavior, report.ExpectedBehavior);

        try
        {
            var host = _host() ?? throw new InvalidOperationException("Not connected to a host.");
            var result = await host.SubmitBugReportAsync(dto).ConfigureAwait(false);
            _dispatcher.Post(() => Apply(result, report));
        }
        catch (Exception ex)
        {
            // Host unreachable/disabled → open the public prefilled issue in the browser (no private payload).
            var fallback = BugReportPrefill.NewIssueUrl(_repo, report);
            _openUrl(fallback);
            _dispatcher.Post(() => Status = "Couldn't reach the host (" + ex.Message + "); opened a prefilled issue in your browser.");
        }
        finally
        {
            _dispatcher.Post(() => IsBusy = false);
        }
    }

    private void Apply(BugReportResult result, BugReport report)
    {
        Duplicates.Clear();
        if (result.Duplicates is { Count: > 0 } dupes)
        {
            foreach (var d in dupes)
            {
                Duplicates.Add(d);
            }

            OnPropertyChanged(nameof(HasDuplicates));
            Status = $"Found {dupes.Count} possibly-related open issue(s) — comment on one instead, or submit again to file a new one.";
            return;
        }

        OnPropertyChanged(nameof(HasDuplicates));

        if (result.Success && result.Url is { Length: > 0 } created)
        {
            _openUrl(created);
            Status = "Thanks — your report was filed. Opening it in your browser.";
        }
        else if (result.Url is { Length: > 0 } fallback)
        {
            // No API token on the host: it handed back a prefilled new-issue URL for the browser.
            _openUrl(fallback);
            Status = "Opening a prefilled issue in your browser to finish filing.";
        }
        else
        {
            Status = "Couldn't submit: " + (result.Error ?? "unknown error") + ". Opening a prefilled issue in your browser instead.";
            _openUrl(BugReportPrefill.NewIssueUrl(_repo, report));
        }
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
