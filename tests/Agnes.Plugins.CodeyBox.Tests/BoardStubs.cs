using System.Collections.ObjectModel;
using System.Windows.Input;
using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// A command that records what it was handed. Every bound command in the board's views is one of these
/// in a test, which is how a binding that quietly resolves to nothing gets caught.
/// </summary>
public sealed class Fired : ICommand
{
    private EventHandler? _unused;

    public List<object?> Parameters { get; } = [];

    public bool Ran => Parameters.Count > 0;

    public event EventHandler? CanExecuteChanged
    {
        add => _unused += value;
        remove => _unused -= value;
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => Parameters.Add(parameter);
}

/// <summary>
/// Everything the runway, the relations band and the composer bind to, on a plain object.
/// </summary>
/// <remarks>
/// A stub rather than the real <see cref="CodeyBoxQueueViewModel"/> on purpose. The real one starts a
/// polling loop and a websocket the moment a view attaches to it, so a render test against it is a test
/// of the network as much as of the markup; and it cannot be put into the states that matter here — a
/// board mid-move, a chain held by a failed parent — without driving a live fleet into them. Bindings in
/// this project are not compiled, so the names below are the contract: if one is renamed here without
/// being renamed in the markup, the render tests see a dead control.
/// </remarks>
public sealed class BoardStub
{
    /// <summary>What the queue view gates its whole tab on. The board only ever renders inside it.</summary>
    public bool IsConfigured => true;

    /// <summary>Enough of the section switch for the queue column to be the visible one.</summary>
    public SectionsStub Sections { get; } = new();

    public Board? Board { get; init; } = BoardSamples.Fleet();

    public bool HasBoard => Board is not null;

    public IReadOnlyList<Chain> HistoryMatches { get; init; } = [];

    public string Search { get; set; } = string.Empty;

    public string? ProjectFilter { get; set; }

    public string? AgentFilter { get; set; }

    public ObservableCollection<Project> Projects { get; } =
    [
        new Project("codeybox-self", "CodeyBox", null, "main", "claude", 25, null),
        new Project("agnes", "Agnes", null, "main", "codex", 25, null),
    ];

    public ObservableCollection<string> Agents { get; } = ["claude", "codex", "pi"];

    public WorkItemRow? Selected { get; set; }

    public Relations? Relations { get; init; }

    public Chain? PendingMove { get; init; }

    public bool HasPendingMove => PendingMove is not null;

    public bool IsAddingDependency { get; init; }

    public PickerStub Picker { get; init; } = new();

    public Fired SelectChainCommand { get; } = new();

    public Fired SelectStepCommand { get; } = new();

    public Fired SelectCommand { get; } = new();

    public Fired RunNextCommand { get; } = new();

    public Fired MoveUpCommand { get; } = new();

    public Fired MoveDownCommand { get; } = new();

    public Fired BeginRunAfterCommand { get; } = new();

    public Fired RunAfterCommand { get; } = new();

    public Fired CancelPendingMoveCommand { get; } = new();

    public Fired RemoveDependencyCommand { get; } = new();

    public Fired RetryParentCommand { get; } = new();

    public Fired UncancelParentCommand { get; } = new();

    public Fired OpenAddDependencyCommand { get; } = new();

    public Fired ApplyAddDependencyCommand { get; } = new();

    public Fired CancelAddDependencyCommand { get; } = new();

    public Fired OpenComposerCommand { get; } = new();

    public ComposerStub Composer { get; init; } = new();
}

/// <summary>
/// The slice of the sections view model the queue column's visibility hangs on.
/// </summary>
/// <remarks>
/// Only the flags that decide whether the queue column is on screen. The rest of the tab's sections bind
/// names this does not carry, so they render as their own empty selves beside it — which is fine here and
/// is itself worth seeing: the runway must survive being one section among ten.
/// </remarks>
public sealed class SectionsStub
{
    public bool IsQueue => true;

    public bool IsDashboard => false;

    public bool HasOverview => false;

    public string SectionTitle => "Work queue";
}

/// <summary>The dependency picker, as the band and the composer both bind it.</summary>
public sealed class PickerStub
{
    public string Search { get; set; } = string.Empty;

    public IReadOnlyList<DependencyCandidate> Candidates { get; init; } = BoardSamples.Candidates();

    public int TickedCount { get; init; } = 1;

    public Fired TickCommand { get; } = new();
}

/// <summary>The composer view model's bound surface.</summary>
public sealed class ComposerStub
{
    public bool IsOpen { get; init; }

    public ComposerIntent Intent { get; init; } = ComposerIntent.Blank;

    public string Title { get; set; } = "Wire the credential broker into the sandbox";

    public bool TitleIsDerived { get; init; } = true;

    public string Text { get; set; } =
        "Wire the credential broker into the sandbox so a sandboxed agent can push without a long-lived token.";

    public string? ProjectId { get; set; } = "codeybox-self";

    public ObservableCollection<Project> Projects { get; } =
    [
        new Project("codeybox-self", "CodeyBox", null, "main", "claude", 25, null),
    ];

    public string? Agent { get; set; } = "claude";

    public bool AgentInherited { get; init; } = true;

    public ObservableCollection<string> Agents { get; } = ["claude", "codex"];

    public string? BaseBranch { get; set; } = "main";

    public bool BaseBranchInherited { get; init; } = true;

    public int AuditMaxIterations { get; set; } = 25;

    public bool AuditMaxIterationsInherited { get; init; } = true;

    public string? AuditorProfile { get; set; }

    public ObservableCollection<string> AuditorProfiles { get; } = ["default", "security"];

    public bool HasAuditorProfiles => AuditorProfiles.Count > 0;

    public bool IsRefactor { get; set; }

    public string? ExternalId { get; set; }

    public Position Position { get; set; } = Position.Normal;

    public IReadOnlyList<Position> Positions { get; } = [.. Enum.GetValues<Position>()];

    public Chain? AfterChain { get; set; }

    public IReadOnlyList<Chain> NextChains { get; init; } = BoardSamples.Fleet().Next;

    public int Priority { get; set; }

    public bool PriorityIsManual { get; init; }

    public Plan? Plan { get; init; }

    public bool IsChain => Plan?.IsChain ?? false;

    public IReadOnlyList<PlanStepStub> Steps { get; init; } = [];

    public PickerStub Picker { get; init; } = new();

    public string Status { get; init; } = "Ready. Nothing has been sent yet.";

    public bool CanCreate { get; init; } = true;

    public Fired CreateCommand { get; } = new();

    public Fired CancelCommand { get; } = new();
}

/// <summary>One step of a pasted plan, before any of it exists.</summary>
public sealed class PlanStepStub
{
    public int Index { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Parents { get; init; } = string.Empty;

    public Fired ToggleParentCommand { get; } = new();
}
