using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.Plugins.CodeyBox;

/// <summary>
/// Creating work: one item, or a whole dependency chain out of a pasted plan.
/// </summary>
/// <remarks>
/// <para>The form this replaces was eleven boxes, every one of them empty and several of them
/// memory tests — the project id typed exactly, dependencies as comma-separated UUIDs, priority as a
/// bare number against a scale nothing on screen explained. It asked the operator for things the
/// interface already knew.</para>
///
/// <para>So the composer <b>infers</b> and then <b>shows what it inferred</b>. Project, agent, base
/// branch, audit budget and auditor profile come from where it was opened — the selected item, the
/// filtered project — and each is rendered as an overridable chip rather than a blank field, because a
/// blank field reads as a gap where an inherited value reads as a decision. The title derives from the
/// first line of the prompt until someone types over it. Queue position is chosen in words (Next,
/// After…, Normal, Background) and the number it maps to stays visible and editable beside it, because
/// the operator on the live instance was steering pickup by hand-typing ~70 distinct priority values and
/// the words alone would take that control away.</para>
///
/// <para><b>Chains.</b> Work here arrives in authored batches — nine items in one second, seven-step
/// series — and CodeyBox can express that in one pass: <c>POST /workitems</c> accepts <c>dependsOn</c>
/// entries naming items that already exist, including by <c>externalId</c>. So a pasted plan is split
/// into drafts, each is given a generated externalId under one per-plan prefix, and they are created
/// <b>parents first, in topological order</b>, each child naming its parents' externalIds. There is no
/// transaction: creation stops at the first failure and reports exactly which steps exist and which do
/// not, because the alternative — a silent partial chain — is the worst outcome available.</para>
/// </remarks>
public sealed partial class ComposerViewModel : ObservableObject
{
    private readonly CodeyBoxClient _client;
    private readonly Func<Action, Task> _toUi;
    private readonly Func<IReadOnlyList<Project>> _projects;
    private readonly Func<IReadOnlyList<WorkItemRow>> _items;
    private readonly Func<IReadOnlyList<Chain>> _nextChains;
    private readonly Func<Task> _refresh;
    private readonly Action<string> _select;

    /// <summary>Guards the debounced re-parse: a keystroke that lands while an older parse is in flight
    /// must win, and the older one must not be allowed to overwrite it on the way back.</summary>
    private int _parseGeneration;

    private CancellationTokenSource? _parseCts;

    /// <summary>How long the plan waits after the last keystroke before it is re-split. Long enough that
    /// typing a sentence costs one parse, short enough that the step list feels like it is following.</summary>
    internal static readonly TimeSpan ParseDebounce = TimeSpan.FromMilliseconds(250);

    public ComposerViewModel(
        CodeyBoxClient client,
        Func<Action, Task> toUi,
        Func<IReadOnlyList<Project>> projects,
        Func<IReadOnlyList<WorkItemRow>> items,
        Func<IReadOnlyList<Chain>> nextChains,
        Func<Task> refresh,
        Action<string> select)
    {
        _client = client;
        _toUi = toUi;
        _projects = projects;
        _items = items;
        _nextChains = nextChains;
        _refresh = refresh;
        _select = select;

        CreateCommand = new AsyncRelayCommand(CreateAsync, () => CanCreate);
        CancelCommand = new RelayCommand(Close);
    }

    // ---- open / close ----

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private ComposerIntent _intent;

    /// <summary>What the composer is doing, in the operator's words, for the panel's own heading.</summary>
    public string Heading => Intent switch
    {
        ComposerIntent.FollowUp => "Follow-up work",
        ComposerIntent.Sibling => "Another step in this chain",
        ComposerIntent.Split => "Split this into steps",
        ComposerIntent.Duplicate => "The same work, elsewhere",
        ComposerIntent.Promote => "Promote this suggestion",
        _ => "New work",
    };

    partial void OnIntentChanged(ComposerIntent value) => OnPropertyChanged(nameof(Heading));

    /// <summary>
    /// Fills the composer in from where it was opened and shows it.
    /// </summary>
    /// <remarks>
    /// <see cref="Composer.Infer"/> is the pure half and is being implemented alongside this. Until it
    /// exists the composer opens on the context's own facts rather than refusing to open at all — a
    /// half-seeded form is usable, an exception at the click is not. The integrator removes the catch.
    /// </remarks>
    public void Open(ComposerContext context)
    {
        var projects = _projects();
        var items = _items();

        Draft? inferred = null;
        try
        {
            inferred = Composer.Infer(context, projects, items);
        }
        catch (NotImplementedException)
        {
            // BoardModel/Composer is landing concurrently. See the remark above.
        }

        Intent = context.Intent;
        Prefix = $"plan-{DateTimeOffset.Now:yyyyMMdd-HHmm}";
        Status = string.Empty;

        Reconcile.Apply(
            Projects,
            [.. projects.Select(p => new ProjectChoice(
                p.Id, p.DisplayName, p.DefaultAgent, p.AuditMaxIterations, p.DefaultBaseBranch))],
            p => p.Id);

        Reconcile.Apply(
            Agents,
            [.. items.Select(i => i.Agent).Where(a => a is { Length: > 0 }).Distinct().Order()!],
            a => a);

        ProjectId = inferred?.ProjectId is { Length: > 0 } id
            ? id
            : context.ProjectFilter
                ?? context.From?.ProjectId
                ?? context.Suggestion?.ProjectId
                ?? projects.FirstOrDefault()?.Id;

        // Set before Text, because deriving the title is what an untouched Title box does and setting
        // Text is what triggers the derivation.
        TitleIsDerived = true;
        Agent = inferred?.Agent;
        BaseBranch = inferred?.BaseBranch;
        AuditMaxIterations = inferred?.AuditMaxIterations;
        AuditorProfile = inferred?.AuditorProfile;
        IsRefactor = inferred?.IsRefactor ?? false;
        ExternalId = inferred?.ExternalId ?? string.Empty;

        Text = inferred?.Prompt is { Length: > 0 } prompt
            ? prompt
            : Seed(context);

        if (inferred?.Title is { Length: > 0 } title)
        {
            Title = title;
            TitleIsDerived = false;
        }

        PriorityIsManual = false;
        AfterChain = null;
        Position = Position.Normal;

        // Nothing is excluded: the work being composed does not exist yet, so it cannot be its own parent
        // and has no descendants — and the item a follow-up came FROM is precisely the one it should be
        // able to wait on. That item is still the anchor the list is banded around.
        Picker.Reset(subject: null, items, inferred?.DependsOn ?? PreTicked(context), near: context.From);

        RefreshNextChains();
        RecomputePriority();
        IsOpen = true;
        NotifyInheritance();
        CreateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>What the prompt starts as when the model has not inferred one: the suggestion's own
    /// rationale, or the prompt being followed up on, so the box is never blank when something was
    /// already said.</summary>
    private static string Seed(ComposerContext context) => context.Intent switch
    {
        ComposerIntent.Promote when context.Suggestion is { } s =>
            string.Join(Environment.NewLine + Environment.NewLine,
                new[] { s.Title, s.Rationale }.Where(p => !string.IsNullOrWhiteSpace(p))),
        ComposerIntent.Duplicate or ComposerIntent.Split when context.From?.Prompt is { Length: > 0 } p => p,
        _ => string.Empty,
    };

    /// <summary>A follow-up waits on what it follows. Nothing else pre-ticks an edge: a sibling joins a
    /// chain at the same level, and a duplicate is deliberately independent.</summary>
    private static IReadOnlyList<string> PreTicked(ComposerContext context)
        => context is { Intent: ComposerIntent.FollowUp, From: { } from } ? [from.Id] : [];

    private void Close()
    {
        IsOpen = false;
        Status = string.Empty;
        Text = string.Empty;
        Title = string.Empty;
        TitleIsDerived = true;
        Plan = null;
        Steps.Clear();
        AfterChain = null;
        PendingPlan = null;
        _parseCts?.Cancel();
    }

    public IRelayCommand CancelCommand { get; }

    // ---- the fields ----

    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>Whether <see cref="Title"/> is still following the first line of <see cref="Text"/>.
    /// Typing in the title box is what turns it off, and nothing turns it back on — a derived title that
    /// re-derived over an edit would be a box that undoes what you type.</summary>
    [ObservableProperty]
    private bool _titleIsDerived = true;

    /// <summary>Set while the derivation writes <see cref="Title"/>, so its own write is not mistaken for
    /// the operator's.</summary>
    private bool _deriving;

    partial void OnTitleChanged(string value)
    {
        if (!_deriving)
        {
            TitleIsDerived = false;
        }

        CreateCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanCreate));
    }

    /// <summary>The prompt, or the pasted plan. One box for both, because the difference is decided by
    /// what was pasted rather than by a mode the operator has to pick first.</summary>
    [ObservableProperty]
    private string _text = string.Empty;

    partial void OnTextChanged(string value)
    {
        if (TitleIsDerived)
        {
            _deriving = true;
            Title = FirstLine(value);
            _deriving = false;
        }

        CreateCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanCreate));
        ScheduleParse();
    }

    /// <summary>The first non-blank line, stripped of the markdown a pasted plan carries.</summary>
    internal static string FirstLine(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('#', '-', '*', '>', ' ').Trim();
            if (line.Length > 0)
            {
                return line.Length <= 120 ? line : line[..120];
            }
        }

        return string.Empty;
    }

    [ObservableProperty]
    private string? _projectId;

    partial void OnProjectIdChanged(string? value)
    {
        Reconcile.Apply(AuditorProfiles, ProjectOf(value)?.AuditTypes ?? [], t => t);
        if (AuditorProfile is { } chosen && !AuditorProfiles.Contains(chosen))
        {
            AuditorProfile = null;
        }

        OnPropertyChanged(nameof(HasAuditorProfiles));
        NotifyInheritance();
        RecomputePriority();

        // Re-split against the new project: draft externalIds are namespaced per project, so the same
        // text under a different project is a different set of ids.
        ScheduleParse();
        CreateCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanCreate));
    }

    public ObservableCollection<ProjectChoice> Projects { get; } = [];

    public ObservableCollection<string> Agents { get; } = [];

    public ObservableCollection<string> AuditorProfiles { get; } = [];

    public bool HasAuditorProfiles => AuditorProfiles.Count > 0;

    private Project? ProjectOf(string? id)
        => id is { Length: > 0 } ? _projects().FirstOrDefault(p => p.Id == id) : null;

    /// <summary>Agent override. Null inherits the project's default, which <see cref="AgentInherited"/>
    /// states so an empty box reads as a decision rather than a gap.</summary>
    [ObservableProperty]
    private string? _agent;

    partial void OnAgentChanged(string? value) => NotifyInheritance();

    public bool AgentIsInherited => string.IsNullOrWhiteSpace(Agent);

    public string AgentInherited => Inherited(ProjectOf(ProjectId)?.DefaultAgent);

    [ObservableProperty]
    private string? _baseBranch;

    partial void OnBaseBranchChanged(string? value) => NotifyInheritance();

    public bool BaseBranchIsInherited => string.IsNullOrWhiteSpace(BaseBranch);

    public string BaseBranchInherited => Inherited(ProjectOf(ProjectId)?.DefaultBaseBranch);

    /// <summary>Null inherits the project's audit budget. Nullable rather than zero: zero would mean "no
    /// audit iterations at all", which is a real and very different instruction.</summary>
    [ObservableProperty]
    private int? _auditMaxIterations;

    partial void OnAuditMaxIterationsChanged(int? value) => NotifyInheritance();

    public bool AuditMaxIterationsIsInherited => AuditMaxIterations is null;

    public string AuditMaxIterationsInherited
        => ProjectOf(ProjectId) is { AuditMaxIterations: > 0 } p
            ? $"{p.AuditMaxIterations} (project default)"
            : string.Empty;

    private static string Inherited(string? value)
        => value is { Length: > 0 } ? $"{value} (project default)" : string.Empty;

    private void NotifyInheritance()
    {
        foreach (var name in new[]
        {
            nameof(AgentIsInherited), nameof(AgentInherited),
            nameof(BaseBranchIsInherited), nameof(BaseBranchInherited),
            nameof(AuditMaxIterationsIsInherited), nameof(AuditMaxIterationsInherited),
        })
        {
            OnPropertyChanged(name);
        }
    }

    [ObservableProperty]
    private string? _auditorProfile;

    [ObservableProperty]
    private bool _isRefactor;

    [ObservableProperty]
    private string _externalId = string.Empty;

    // ---- where it lands in the queue ----

    /// <summary>Position in words. The number it maps to is <see cref="Priority"/>, always visible.</summary>
    [ObservableProperty]
    private Position _position = Position.Normal;

    partial void OnPositionChanged(Position value)
    {
        OnPropertyChanged(nameof(IsAfter));
        RecomputePriority();
    }

    /// <summary>The four words position is chosen in. An instance property because that is what a
    /// picker binds to.</summary>
    public IReadOnlyList<Position> Positions { get; } = Enum.GetValues<Position>();

    public bool IsAfter => Position == Position.After;

    /// <summary>The chain the new work should follow, for <see cref="Position.After"/>.</summary>
    [ObservableProperty]
    private Chain? _afterChain;

    partial void OnAfterChainChanged(Chain? value) => RecomputePriority();

    /// <summary>The dispatch order as it stands, so "after…" is chosen from what is actually queued.</summary>
    public ObservableCollection<Chain> NextChains { get; } = [];

    private void RefreshNextChains() => Reconcile.Apply(NextChains, [.. _nextChains()], c => c.Id);

    /// <summary>Kept current while the composer is open, so a chain that starts running does not stay on
    /// offer as something to queue behind.</summary>
    internal void NoteBoardChanged()
    {
        if (!IsOpen)
        {
            return;
        }

        RefreshNextChains();
        if (!PriorityIsManual)
        {
            RecomputePriority();
        }
    }

    /// <summary>The number the orchestrator will actually sort by. Editable: the words cover the four
    /// cases worth naming, and the operator here steers with far more than four values.</summary>
    [ObservableProperty]
    private int _priority;

    partial void OnPriorityChanged(int value)
    {
        if (!_recomputing)
        {
            PriorityIsManual = true;
        }
    }

    /// <summary>Whether the operator has typed a priority, in which case the position words stop
    /// overwriting it. Nothing clears this but reopening: a number someone typed being silently replaced
    /// is the failure mode this exists to prevent.</summary>
    [ObservableProperty]
    private bool _priorityIsManual;

    private bool _recomputing;

    private void RecomputePriority()
    {
        if (PriorityIsManual)
        {
            return;
        }

        var ceiling = ProjectOf(ProjectId)?.PriorityCeiling ?? Project.GlobalMaxPriority;
        int value;
        try
        {
            value = Composer.PriorityFor(Position, AfterChain?.Id, [.. NextChains], ceiling);
        }
        catch (NotImplementedException)
        {
            // Composer.PriorityFor lands alongside this. Normal is 0 either way, so the common case is
            // right and the others simply do not move until it arrives.
            value = 0;
        }

        _recomputing = true;
        Priority = value;
        _recomputing = false;
    }

    // ---- dependencies ----

    /// <summary>What the new work waits on. The same picker the pane uses, so the two are learned once.</summary>
    public DependencyPicker Picker { get; } = new();

    // ---- the plan ----

    /// <summary>The external-id prefix for this plan, generated once per <see cref="Open"/> so sibling
    /// references resolve at create time without a round trip per step.</summary>
    public string Prefix { get; private set; } = string.Empty;

    /// <summary>The plan as parsed, with the operator's edges applied.</summary>
    [ObservableProperty]
    private Plan? _plan;

    /// <summary>The plan exactly as <see cref="Composer.Parse"/> returned it, before the operator
    /// reticked anything. Held so a re-parse can be re-applied without losing edits that still fit.</summary>
    private Plan? PendingPlan { get; set; }

    partial void OnPlanChanged(Plan? value)
    {
        OnPropertyChanged(nameof(IsChain));
        OnPropertyChanged(nameof(PlanSummary));
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(CanCreate));
        CreateCommand.NotifyCanExecuteChanged();
    }

    public bool IsChain => Plan?.IsChain ?? false;

    public string PlanSummary => Plan?.Summary ?? string.Empty;

    public bool HasProblems => Plan is { Problems.Count: > 0 };

    /// <summary>The steps, editable. Each row can retick which earlier steps it waits on.</summary>
    public ObservableCollection<DraftStep> Steps { get; } = [];

    private void ScheduleParse()
    {
        var generation = Interlocked.Increment(ref _parseGeneration);
        _parseCts?.Cancel();
        _parseCts?.Dispose();
        var cts = new CancellationTokenSource();
        _parseCts = cts;

        var text = Text;
        var projectId = ProjectId ?? string.Empty;
        var prefix = Prefix;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ParseDebounce, cts.Token).ConfigureAwait(false);

                // Off the UI thread on purpose: splitting a long pasted plan is real work, and it runs on
                // every keystroke that survives the debounce.
                Plan? parsed;
                try
                {
                    parsed = Composer.Parse(text, projectId, prefix);
                }
                catch (NotImplementedException)
                {
                    parsed = null;
                }

                if (generation != Volatile.Read(ref _parseGeneration))
                {
                    return;
                }

                await _toUi(() => AdoptPlan(parsed)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // superseded by a later keystroke
            }
            catch (Exception ex)
            {
                Diagnostic.Report("parse plan", ex);
            }
        }, CancellationToken.None);
    }

    private void AdoptPlan(Plan? parsed)
    {
        PendingPlan = parsed;
        if (parsed is null)
        {
            Plan = null;
            Steps.Clear();
            return;
        }

        Plan = parsed;
        Reconcile.Apply(
            Steps,
            [.. parsed.Drafts.Select((d, i) => Step(parsed, d, i))],
            s => s.Index);
    }

    private DraftStep Step(Plan plan, Draft draft, int index)
    {
        var parents = ParentIndices(plan, draft);

        // Only EARLIER steps are offered as parents. A plan is read top to bottom, and an edge pointing
        // backwards up the page is the only kind that cannot close a loop.
        var options = Enumerable.Range(0, index)
            .Select(p => new DraftEdge(index, p, parents.Contains(p)))
            .ToList();

        return new DraftStep(index, draft.Title, parents, options, ToggleParentCommand);
    }

    /// <summary>Which earlier drafts a draft waits on, as indices — the plan's own edges expressed the way
    /// the step list can render and retick them.</summary>
    private static IReadOnlyList<int> ParentIndices(Plan plan, Draft draft)
        => [.. draft.DependsOn
            .Select(reference => plan.Drafts.ToList().FindIndex(d => d.ExternalId == reference))
            .Where(i => i >= 0)];

    /// <summary>Ticks one edge between two drafts. Carried on every step row so a parent checkbox can
    /// name the edge it means, rather than the view model holding a "currently editing" mode.</summary>
    public IRelayCommand<DraftEdge> ToggleParentCommand =>
        _toggleParent ??= new RelayCommand<DraftEdge>(ToggleParent);

    private IRelayCommand<DraftEdge>? _toggleParent;

    /// <summary>
    /// Ticks or unticks one edge between two drafts, then revalidates.
    /// </summary>
    /// <remarks>
    /// Edits a local copy of the plan rather than re-parsing: the text has not changed, so re-splitting it
    /// would throw the operator's edges away. Cycles are caught here, over the draft indices, because none
    /// of these items exist yet and so <see cref="BoardModel.WouldCycle"/> — which walks the live queue —
    /// has nothing to walk.
    /// </remarks>
    internal void ToggleParent(DraftEdge? edge)
    {
        if (edge is not { } e || Plan is not { } plan
            || e.StepIndex < 0 || e.StepIndex >= plan.Drafts.Count
            || e.ParentIndex < 0 || e.ParentIndex >= plan.Drafts.Count
            || e.ParentIndex == e.StepIndex)
        {
            return;
        }

        var drafts = plan.Drafts.ToList();
        var parentRef = drafts[e.ParentIndex].ExternalId;
        if (parentRef is null)
        {
            return;
        }

        var edges = drafts[e.StepIndex].DependsOn.ToList();
        if (!edges.Remove(parentRef))
        {
            edges.Add(parentRef);
        }

        drafts[e.StepIndex] = drafts[e.StepIndex] with { DependsOn = edges };
        AdoptPlan(plan with { Drafts = drafts, Problems = Validate(drafts) });
    }

    /// <summary>
    /// Everything about the edited drafts that would be rejected: an empty step, a reference to a step
    /// that is not there, or a cycle.
    /// </summary>
    internal static IReadOnlyList<string> Validate(IReadOnlyList<Draft> drafts)
    {
        var problems = new List<string>();
        var byExternalId = drafts
            .Where(d => d.ExternalId is { Length: > 0 })
            .ToDictionary(d => d.ExternalId!, d => d, StringComparer.Ordinal);

        for (var i = 0; i < drafts.Count; i++)
        {
            if (!drafts[i].IsValid)
            {
                problems.Add($"Step {i + 1} needs a title and a prompt.");
            }
        }

        // Depth-first with a colour mark: white unvisited, grey on the stack, black finished. An edge
        // back into grey is a cycle, which is the only shape the orchestrator will not accept.
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var cycle = false;

        bool Walk(Draft draft)
        {
            var id = draft.ExternalId!;
            if (state.TryGetValue(id, out var mark))
            {
                return mark == 1;
            }

            state[id] = 1;
            foreach (var parent in draft.DependsOn)
            {
                if (byExternalId.TryGetValue(parent, out var next) && Walk(next))
                {
                    return true;
                }
            }

            state[id] = 2;
            return false;
        }

        foreach (var draft in drafts.Where(d => d.ExternalId is { Length: > 0 }))
        {
            cycle |= Walk(draft);
        }

        if (cycle)
        {
            problems.Add("These steps depend on each other in a loop.");
        }

        return problems;
    }

    // ---- creating ----

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isCreating;

    public bool CanCreate => !IsCreating
        && ProjectId is { Length: > 0 }
        && !string.IsNullOrWhiteSpace(Text)
        && (IsChain || !string.IsNullOrWhiteSpace(Title))
        && Plan is not { Problems.Count: > 0 };

    partial void OnIsCreatingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCreate));
        CreateCommand.NotifyCanExecuteChanged();
    }

    public IAsyncRelayCommand CreateCommand { get; }

    private async Task CreateAsync()
    {
        if (ProjectId is not { Length: > 0 } projectId)
        {
            await _toUi(() => Status = "Choose a project first.").ConfigureAwait(false);
            return;
        }

        await _toUi(() => { IsCreating = true; Status = string.Empty; }).ConfigureAwait(false);
        try
        {
            var created = IsChain && Plan is { } plan
                ? await CreateChainAsync(plan).ConfigureAwait(false)
                : await CreateSingleAsync(projectId).ConfigureAwait(false);

            if (created is { Count: > 0 })
            {
                await _refresh().ConfigureAwait(false);
                await _toUi(() =>
                {
                    Close();
                    _select(created[0]);
                }).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Diagnostic.Report("compose", ex);
            await _toUi(() => Status = $"Couldn't create — {ex.Message}").ConfigureAwait(false);
        }
        finally
        {
            await _toUi(() => IsCreating = false).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<string>> CreateSingleAsync(string projectId)
    {
        var id = await _client.CreateWorkItemAsync(
            new NewWorkItem(
                projectId,
                Title.Trim(),
                Text.Trim(),
                Agent: Blank(Agent),
                BaseBranch: Blank(BaseBranch),
                Priority: Priority,
                DependsOn: Picker.Ticked.Count == 0 ? null : Picker.Ticked,
                AuditMaxIterations: AuditMaxIterations,
                AuditorProfile: Blank(AuditorProfile),
                ExternalId: Blank(ExternalId),
                IsRefactor: IsRefactor ? true : null)).ConfigureAwait(false);

        return id is null ? [] : [id];
    }

    /// <summary>
    /// Creates a chain, parents first.
    /// </summary>
    /// <remarks>
    /// Sequential and ordered, not parallel: <c>POST /workitems</c> accepts a <c>dependsOn</c> naming an
    /// externalId only once that item <b>already exists</b>, so a child sent before its parent is
    /// rejected. There is no batch endpoint and no transaction, so a failure halfway leaves a real,
    /// partial chain — which is reported precisely rather than rolled back, because the steps that did
    /// land are running and pretending otherwise would be worse than saying so.
    /// </remarks>
    private async Task<IReadOnlyList<string>> CreateChainAsync(Plan plan)
    {
        var order = TopologicalOrder(plan.Drafts);
        var created = new List<string>();
        var byExternalId = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var draft in order)
        {
            // Every draft carries its externalId so its children can name it, and the picker's existing
            // items ride along as UUIDs on every step — one shared prerequisite for the whole chain.
            var dependsOn = draft.DependsOn
                .Select(reference => byExternalId.TryGetValue(reference, out var id) ? id : reference)
                .Concat(Picker.Ticked)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            string? newId;
            try
            {
                newId = await _client.CreateWorkItemAsync(
                    new NewWorkItem(
                        draft.ProjectId,
                        draft.Title,
                        draft.Prompt,
                        Agent: draft.Agent ?? Blank(Agent),
                        BaseBranch: draft.BaseBranch ?? Blank(BaseBranch),
                        Priority: draft.Priority ?? Priority,
                        DependsOn: dependsOn.Count == 0 ? null : dependsOn,
                        AuditMaxIterations: draft.AuditMaxIterations ?? AuditMaxIterations,
                        AuditorProfile: draft.AuditorProfile ?? Blank(AuditorProfile),
                        ExternalId: draft.ExternalId,
                        IsRefactor: draft.IsRefactor || IsRefactor ? true : null)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Diagnostic.Report($"create chain step {draft.Title}", ex);
                await _toUi(() => Status = Partial(created.Count, plan.Drafts.Count, draft.Title, ex.Message))
                    .ConfigureAwait(false);
                await _refresh().ConfigureAwait(false);
                return [];
            }

            if (newId is null)
            {
                await _toUi(() => Status = Partial(created.Count, plan.Drafts.Count, draft.Title, "no id came back"))
                    .ConfigureAwait(false);
                await _refresh().ConfigureAwait(false);
                return [];
            }

            created.Add(newId);
            if (draft.ExternalId is { Length: > 0 } external)
            {
                byExternalId[external] = newId;
            }
        }

        await _toUi(() => Status = $"Created {created.Count} steps.").ConfigureAwait(false);
        return created;
    }

    /// <summary>Says exactly how far a half-created chain got, and where it stopped. Names the step rather
    /// than the count alone, because the operator's next move is to look at that one.</summary>
    internal static string Partial(int created, int total, string failedTitle, string reason)
        => created == 0
            ? $"Nothing was created — “{failedTitle}” failed: {reason}"
            : $"Created {created} of {total} steps. “{failedTitle}” failed: {reason}. " +
              $"The {created} that landed are queued; the remaining {total - created} were not sent.";

    /// <summary>
    /// Drafts in dependency order, parents first. Stable: ties keep the order they were written in, so a
    /// linear plan is created in the order it was pasted.
    /// </summary>
    internal static IReadOnlyList<Draft> TopologicalOrder(IReadOnlyList<Draft> drafts)
    {
        var byExternalId = drafts
            .Where(d => d.ExternalId is { Length: > 0 })
            .GroupBy(d => d.ExternalId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var ordered = new List<Draft>(drafts.Count);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);

        void Place(Draft draft)
        {
            var key = draft.ExternalId ?? draft.Title;
            if (!placed.Add(key) || !visiting.Add(key))
            {
                return;
            }

            foreach (var reference in draft.DependsOn)
            {
                if (byExternalId.TryGetValue(reference, out var parent) && !ReferenceEquals(parent, draft))
                {
                    // A cycle is already reported as a Problem and blocks creation; guarding here as well
                    // keeps this from recursing forever if one ever reaches it.
                    if (!visiting.Contains(parent.ExternalId ?? parent.Title))
                    {
                        Place(parent);
                    }
                }
            }

            visiting.Remove(key);
            ordered.Add(draft);
        }

        foreach (var draft in drafts)
        {
            Place(draft);
        }

        return ordered;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// One step of a plan as the composer's step list shows it: what it is called, and which earlier steps it
/// waits on, by index.
/// </summary>
/// <remarks>
/// Indices rather than ids because none of these items exist yet, and "step 3 waits on step 1" is what the
/// operator is actually reading — the generated externalIds are plumbing that they should never have to
/// see. <see cref="ToggleParentCommand"/> takes the pair so a row's parent checkbox can carry which edge
/// it means without the view model holding a "currently editing" mode.
/// </remarks>
public sealed record DraftStep(
    int Index,
    string Title,
    IReadOnlyList<int> Parents,
    IReadOnlyList<DraftEdge> Options,
    IRelayCommand<DraftEdge> ToggleParentCommand)
{
    /// <summary>"Step 3", as the row is labelled.</summary>
    public string Label => $"Step {Index + 1}";

    public bool HasParents => Parents.Count > 0;

    /// <summary>"waits on step 1, step 2" — the edge set in the same words the label uses.</summary>
    public string ParentsLabel => Parents.Count == 0
        ? "independent"
        : "waits on " + string.Join(", ", Parents.Select(p => $"step {p + 1}"));
}

/// <summary>One tickable edge in the composer's step list: "step 3 waits on step 1", and whether it
/// currently does.</summary>
public sealed record DraftEdge(int StepIndex, int ParentIndex, bool IsOn)
{
    public string Label => $"step {ParentIndex + 1}";
}
