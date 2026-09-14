using System.Collections.ObjectModel;
using Agnes.App.Mobile.Services;
using Agnes.Plugins.CodeyBox;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.App.Mobile.ViewModels;

/// <summary>
/// More › CodeyBox: where this phone finds the orchestrator.
/// </summary>
/// <remarks>
/// The desktop plugin needs no screen like this — it reads the same <c>~/.config/codeybox/config.json</c>
/// the CLI writes, on the machine that runs the thing. A phone has neither the file nor the machine, so
/// the address and the key are typed once here. The Test button exists because the two ways this goes
/// wrong (an address that is not on this network, a key the orchestrator refuses) produce the same blank
/// screen, and telling them apart from a work-item list that did not appear is not reasonable.
/// </remarks>
public sealed partial class CodeyBoxSetupPageViewModel : PageViewModel
{
    private readonly IAppShell _shell;
    private readonly CodeyBoxViewModel _codeybox;

    public CodeyBoxSetupPageViewModel(IAppShell shell, CodeyBoxViewModel codeybox)
    {
        _shell = shell;
        _codeybox = codeybox;
        Address = codeybox.Config.BaseUrl;
        ApiKey = codeybox.Config.ApiKey;

        TestCommand = new AsyncRelayCommand(TestAsync, () => CanSave);
        SaveCommand = new RelayCommand(Save, () => CanSave);
        ForgetCommand = new RelayCommand(Forget);
    }

    public override string Title => "CodeyBox";

    public override string? Subtitle => "The orchestrator this phone watches";

    /// <summary>What the address field suggests. CodeyBox has no discovery and no QR: it is a port on a
    /// machine on your LAN, and the phone has to be told which.</summary>
    public string ExampleAddress => CodeyBoxConfig.ExampleBaseUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _address = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _apiKey = string.Empty;

    public bool CanSave => !string.IsNullOrWhiteSpace(Address) && !string.IsNullOrWhiteSpace(ApiKey);

    public bool IsConfigured => _codeybox.IsConfigured;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _statusIsGood;

    public bool HasStatus => Status.Length > 0;

    [ObservableProperty]
    private bool _isTesting;

    public IAsyncRelayCommand TestCommand { get; }
    public IRelayCommand SaveCommand { get; }
    public IRelayCommand ForgetCommand { get; }

    partial void OnAddressChanged(string value) => RaiseCanSave();

    partial void OnApiKeyChanged(string value) => RaiseCanSave();

    private void RaiseCanSave()
    {
        TestCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Asks the address for <c>/queue/status</c> and says what came back.
    /// </summary>
    /// <remarks>
    /// Read-only, and the cheapest thing the orchestrator serves: it proves the address resolves, the port
    /// is open, the key is accepted and the queue exists, in one round trip and without touching anything.
    /// The typed values are used rather than the saved ones, so Test answers a question about what is on
    /// screen rather than about what was last saved.
    /// </remarks>
    private async Task TestAsync()
    {
        IsTesting = true;
        Status = string.Empty;
        try
        {
            var config = new CodeyBoxConfig(Address, ApiKey);
            await using var probe = new CodeyBoxClient(config.ToOptions());
            var queue = await probe.GetQueueStatusAsync().ConfigureAwait(true);
            StatusIsGood = queue is not null;
            Status = queue switch
            {
                null => "That address answered, but not with a queue. Is it CodeyBox?",
                { IsPaused: true } paused =>
                    $"Found CodeyBox. The queue is paused{(paused.PausedReason is { Length: > 0 } why ? " — " + why : ".")}",
                { } running => $"Found CodeyBox. The queue is {running.State.ToLowerInvariant()}.",
            };
        }
        catch (Exception ex)
        {
            StatusIsGood = false;
            Status = CodeyBoxViewModel.Explain(ex);
        }
        finally
        {
            IsTesting = false;
        }
    }

    private void Save()
    {
        _codeybox.Save(new CodeyBoxConfig(Address.Trim(), ApiKey.Trim()));
        OnPropertyChanged(nameof(IsConfigured));
        _shell.Haptics.Tick();
        _shell.Toast("CodeyBox saved", ToastKind.Success);
        _shell.Pop();
    }

    /// <summary>Removes it entirely: the tab, the inbox rows and the stored key. Not a "disable" — a key
    /// this phone keeps but no longer uses is a credential nobody is watching.</summary>
    private void Forget()
    {
        Address = string.Empty;
        ApiKey = string.Empty;
        Status = string.Empty;
        _codeybox.Save(new CodeyBoxConfig());
        OnPropertyChanged(nameof(IsConfigured));
        _shell.Toast("CodeyBox forgotten", ToastKind.Warning);
        _shell.Pop();
    }
}

/// <summary>
/// One work item, full screen: the decision first when there is one, then the facts.
/// </summary>
/// <remarks>
/// <para><b>The decision leads.</b> Everything below it — project, agent, priority, cost, branch — is
/// context for an answer, and on a phone context that comes first is context that pushes the answer off
/// the screen. <see cref="Decision.For"/> decides whether there is one at all, which is the same function
/// the desktop card uses, handed this head's own commands.</para>
///
/// <para><b>The commands are the desktop's, not new ones.</b> Retry is <c>POST /workitems/{id}/retry</c>;
/// raise-the-ceiling is that retry followed by <c>PATCH /workitems/{id}</c> with a new audit budget,
/// because the patch is refused on a terminal item and AuditFailed is terminal; replay is
/// <c>POST /workitems/{id}/replay</c>; cancel is <c>DELETE /workitems/{id}</c>; answering and dismissing
/// a question are <c>POST /workitems/{id}/answer</c> and <c>POST /workitems/{id}/dismiss-question</c>.
/// A choice this API cannot carry out is not on the card.</para>
/// </remarks>
public sealed partial class CodeyBoxItemPageViewModel : PageViewModel
{
    private readonly IAppShell _shell;
    private readonly CodeyBoxViewModel _codeybox;
    private readonly CodeyBoxClient _client;
    private readonly CancellationTokenSource _cts = new();

    public CodeyBoxItemPageViewModel(
        IAppShell shell, CodeyBoxViewModel codeybox, CodeyBoxClient client, WorkItemRow item)
    {
        _shell = shell;
        _codeybox = codeybox;
        _client = client;
        _item = item;

        RetryCommand = new AsyncRelayCommand<WorkItemRow>(row => ActAsync(row, "Retried", r => _client.RetryAsync(r.Id, _cts.Token)));
        ReplayCommand = new AsyncRelayCommand<WorkItemRow>(row => ActAsync(row, "Started over on a fresh branch", r => _client.ReplayAsync(r.Id, _cts.Token)));
        PromoteCommand = new AsyncRelayCommand<WorkItemRow>(row => ActAsync(row, "Promoted", r => _client.PromoteAsync(r.Id, _cts.Token)));
        RaiseCeilingCommand = new AsyncRelayCommand<WorkItemRow>(RaiseCeilingAsync);
        CancelItemCommand = new AsyncRelayCommand<WorkItemRow>(CancelAsync);
        KeepItemCommand = new RelayCommand(() => IsConfirmingCancel = false);
        AnswerCommand = new RelayCommand<WorkItemQuestion>(Answer);
        DismissCommand = new AsyncRelayCommand<WorkItemQuestion>(DismissAsync);
        ShowOutputCommand = new AsyncRelayCommand(
            () => SheetAsync("Agent output", () => _client.GetStdoutTailAsync(Item.Id, _cts.Token)));
        ShowDiffCommand = new AsyncRelayCommand(
            () => SheetAsync("Diff", () => _client.GetDiffTextAsync(Item.Id, _cts.Token)));
        ShowTimelineCommand = new AsyncRelayCommand(
            () => SheetAsync("Timeline", TimelineTextAsync));
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
    }

    public override string Title => Item.Title;

    public override string? Subtitle => Item.State;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Subtitle), nameof(Facts), nameof(CanPromote))]
    private WorkItemRow _item;

    /// <summary>The item's open questions. Kept beside the decision rather than behind a section: an
    /// agent blocked on a person is the one thing here that should interrupt someone.</summary>
    public ObservableCollection<WorkItemQuestion> Questions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDecision))]
    private Decision? _decision;

    public bool HasDecision => Decision is not null;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _status = string.Empty;

    public bool HasStatus => Status.Length > 0;

    /// <summary>
    /// Armed, not fired.
    /// </summary>
    /// <remarks>
    /// Cancelling an item is destructive and a phone has no hover and no undo, so the first tap names what
    /// it will stop and the second does it — the same two-step the device prune uses in More › Devices.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmCancelText))]
    private bool _isConfirmingCancel;

    public string ConfirmCancelText => $"Cancel “{Item.Title}” for good?";

    /// <summary>The labelled facts under the decision, in the order they answer "what is this".</summary>
    public IReadOnlyList<CodeyBoxFact> Facts =>
    [
        .. new CodeyBoxFact?[]
        {
            new("PROJECT", Item.ProjectId),
            new("AGENT", Item.Agent),
            new("STATE", Item.State),
            new("PRIORITY", Item.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("COST", Item.Cost),
            new("BRANCH", Item.WorkBranch),
            new("ID", Item.ShortId),
        }.Where(f => f is { Value.Length: > 0 }).Select(f => f!),
    ];

    /// <summary>Promote only exists for something the orchestrator would actually pick: the endpoint is
    /// refused outside Queued, and a button whose only outcome is a refusal teaches nothing.</summary>
    public bool CanPromote => Item.State == "Queued";

    public IAsyncRelayCommand<WorkItemRow> RetryCommand { get; }
    public IAsyncRelayCommand<WorkItemRow> ReplayCommand { get; }
    public IAsyncRelayCommand<WorkItemRow> PromoteCommand { get; }
    public IAsyncRelayCommand<WorkItemRow> RaiseCeilingCommand { get; }
    public IAsyncRelayCommand<WorkItemRow> CancelItemCommand { get; }
    public IRelayCommand KeepItemCommand { get; }
    public IRelayCommand<WorkItemQuestion> AnswerCommand { get; }
    public IAsyncRelayCommand<WorkItemQuestion> DismissCommand { get; }
    public IAsyncRelayCommand ShowOutputCommand { get; }
    public IAsyncRelayCommand ShowDiffCommand { get; }
    public IAsyncRelayCommand ShowTimelineCommand { get; }
    public IAsyncRelayCommand RefreshCommand { get; }

    public override void OnAppearing() => _ = LoadAsync();

    public override void OnDisappearing() => _cts.Cancel();

    /// <summary>Reads the questions and rebuilds the card. Cheap — one request, and only for an item whose
    /// state says there might be something to find.</summary>
    public async Task LoadAsync()
    {
        IReadOnlyList<WorkItemQuestion> questions = [];
        try
        {
            if (Item.State == "NeedsOperatorInput" || Item.IsFailed)
            {
                questions = await _client.GetQuestionsAsync(Item.Id, _cts.Token).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Status = CodeyBoxViewModel.Explain(ex);
        }

        Questions.Clear();
        foreach (var question in questions)
        {
            Questions.Add(question);
        }

        RebuildDecision();
    }

    private void RebuildDecision()
        => Decision = Decision.For(
            Item,
            [.. Questions],
            new DecisionActions(
                Answer: AnswerCommand,
                Dismiss: DismissCommand,
                Retry: RetryCommand,
                RaiseCeiling: RaiseCeilingCommand,
                Replay: ReplayCommand,
                Cancel: CancelItemCommand,
                ShowOutput: ShowOutputCommand,
                ShowTimeline: ShowTimelineCommand,
                ShowDiff: ShowDiffCommand),
            _codeybox.BaseBranchOf(Item),
            _codeybox.CeilingOf(Item));

    // ---- the actions ------------------------------------------------------------------------------

    private async Task ActAsync(WorkItemRow? row, string said, Func<WorkItemRow, Task> action)
    {
        if (row is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await action(row).ConfigureAwait(true);
            _shell.Haptics.Tick();
            _shell.Toast($"{said} — {row.Title}", ToastKind.Success);
            await AfterAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostic.Report($"codeybox {said} {row.ShortId}", ex);
            Status = CodeyBoxViewModel.Explain(ex);
            _shell.Toast(Status, ToastKind.Danger);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Retry, then raise the budget.
    /// </summary>
    /// <remarks>
    /// In that order, and it matters: <c>PATCH /workitems/{id}</c> refuses an audit-budget change on a
    /// terminal item, and AuditFailed — the only state that offers this — is terminal. The retry is what
    /// makes the item patchable.
    /// </remarks>
    private async Task RaiseCeilingAsync(WorkItemRow? row)
    {
        if (row is null)
        {
            return;
        }

        var ceiling = _codeybox.CeilingOf(row);
        if (ceiling <= 0)
        {
            Status = "This item has no audit budget to raise.";
            return;
        }

        await ActAsync(
            row,
            $"Retried with {ceiling + Decision.CeilingStep} audit iterations",
            async r =>
            {
                await _client.RetryAsync(r.Id, _cts.Token).ConfigureAwait(false);
                await _client.PatchWorkItemAsync(
                    r.Id, new AuditBudgetPatch(ceiling + Decision.CeilingStep), _cts.Token).ConfigureAwait(false);
            }).ConfigureAwait(true);
    }

    private async Task CancelAsync(WorkItemRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (!IsConfirmingCancel)
        {
            IsConfirmingCancel = true;
            _shell.Haptics.Tick();
            return;
        }

        IsConfirmingCancel = false;
        await ActAsync(row, "Cancelled", r => _client.CancelAsync(r.Id, _cts.Token)).ConfigureAwait(true);
    }

    /// <summary>Opens a reply box for one question. A sheet, not an inline field: the answer is prose, the
    /// keyboard takes half the screen, and the question has to stay readable while it is being answered.</summary>
    private void Answer(WorkItemQuestion? question)
    {
        if (question is null)
        {
            return;
        }

        _shell.ShowSheet(new CodeyBoxAnswerSheetViewModel(_shell, _client, question, AfterAsync));
    }

    /// <summary>
    /// Dismissing a question needs a reason — the orchestrator rejects an empty one — and the reason is
    /// the same every time it is dismissed from here, so it says so rather than asking for a sentence
    /// nobody will read.
    /// </summary>
    private Task DismissAsync(WorkItemQuestion? question)
        => question is null
            ? Task.CompletedTask
            : ActAsync(
                Item,
                "Question dismissed",
                _ => _client.DismissQuestionAsync(
                    question.WorkItemId, question.QuestionId, "Dismissed from the Agnes phone client", _cts.Token));

    /// <summary>After anything that changed the fleet: re-read this item and let the tab re-gather, so the
    /// card and the queue behind it cannot disagree about what just happened.</summary>
    private async Task AfterAsync()
    {
        IsConfirmingCancel = false;
        await _codeybox.RefreshAsync(_cts.Token).ConfigureAwait(true);
        if (_codeybox.Find(Item.Id) is { } fresh)
        {
            Item = fresh;
        }

        await LoadAsync().ConfigureAwait(true);
        _ = _codeybox.RefreshNeedsYouAsync(CancellationToken.None);
    }

    // ---- the lookups ------------------------------------------------------------------------------

    private async Task SheetAsync(string title, Func<Task<string>> fetch)
    {
        try
        {
            var body = await fetch().ConfigureAwait(true);
            _shell.ShowSheet(new DetailSheetViewModel(
                _shell,
                title,
                string.IsNullOrWhiteSpace(body) ? "Nothing recorded." : body,
                command: Item.Title));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _shell.Toast(CodeyBoxViewModel.Explain(ex), ToastKind.Danger);
        }
    }

    private async Task<string> TimelineTextAsync()
    {
        var entries = await _client.GetTimelineAsync(Item.Id, _cts.Token).ConfigureAwait(false);
        return string.Join(
            '\n',
            entries.Select(e => FormattableString.Invariant($"{e.At}  {e.Label,-16}  {e.Summary}")));
    }
}

/// <summary>One labelled fact on the item page, in the pane's fact-label convention.</summary>
public sealed record CodeyBoxFact(string Label, string? Value)
{
    public string Text => Value ?? string.Empty;
}

/// <summary>
/// The reply box for one of an agent's questions.
/// </summary>
/// <remarks>
/// A sheet rather than an inline field, because the keyboard takes half a phone's screen and the question
/// is the thing you need to keep reading while you answer it. Send is disabled on an empty answer: the
/// orchestrator would take it, and an empty answer un-parks the item with nothing learned.
/// </remarks>
public sealed partial class CodeyBoxAnswerSheetViewModel : SheetViewModel
{
    private readonly IAppShell _shell;
    private readonly CodeyBoxClient _client;
    private readonly Func<Task> _after;

    public CodeyBoxAnswerSheetViewModel(
        IAppShell shell, CodeyBoxClient client, WorkItemQuestion question, Func<Task> after)
    {
        _shell = shell;
        _client = client;
        _after = after;
        Question = question;
        SendCommand = new AsyncRelayCommand(SendAsync, () => CanSend);
    }

    public override string Title => "Answer the agent";

    public override double HeightFraction => 0.86;

    public WorkItemQuestion Question { get; }

    public string QuestionText => Question.QuestionText;

    public string Asked => "asked " + Question.Age;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    private string _answer = string.Empty;

    public bool CanSend => !string.IsNullOrWhiteSpace(Answer);

    [ObservableProperty]
    private bool _isSending;

    public IAsyncRelayCommand SendCommand { get; }

    partial void OnAnswerChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    private async Task SendAsync()
    {
        IsSending = true;
        try
        {
            await _client.AnswerQuestionAsync(Question.WorkItemId, Question.QuestionId, Answer.Trim())
                .ConfigureAwait(true);
            _shell.Haptics.Tick();
            _shell.Toast("Answer sent — it reaches the agent on its next run", ToastKind.Success);
            Close();
            await _after().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _shell.Toast(CodeyBoxViewModel.Explain(ex), ToastKind.Danger);
        }
        finally
        {
            IsSending = false;
        }
    }
}
