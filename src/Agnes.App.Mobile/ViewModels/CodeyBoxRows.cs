using System.Collections.ObjectModel;
using Agnes.Plugins.CodeyBox;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agnes.App.Mobile.ViewModels;

/// <summary>
/// The rows the phone's CodeyBox screens draw.
/// </summary>
/// <remarks>
/// Thin wrappers over the plugin's pure models rather than copies of them: everything here is a projection
/// of an <see cref="ItemTrace"/>, a <see cref="Chain"/> or a <see cref="WorkItemRow"/> that the plugin
/// already built. What they add is the handful of things a <em>phone</em> row needs and a desktop pane
/// does not — a word for a state that the desktop shows as a drawn glyph with room beside it, and a
/// one-line identity that fits 411 dp.
///
/// <para>The hues are not restated here. A row draws its motion with the plugin's own
/// <c>MotionDot</c>, so "sky is moving, amber is blocked, pink is wedged" has exactly one definition
/// across both heads; the booleans below only pick which class a text chip wears, from the same rule.</para>
/// </remarks>
public sealed record CodeyBoxTraceRow(ItemTrace Trace)
{
    public WorkItemRow Item => Trace.Item;

    public string Title => Trace.Item.Title;

    public Motion Motion => Trace.Motion;

    /// <summary>The row's own line: the resume time, the dependency, the quiet duration, the gate.</summary>
    public string Why => Trace.Why;

    public bool HasWhy => Why.Length > 0;

    /// <summary>Where it lives and what is running it, in the mono identity line the cards share.</summary>
    public string Detail => string.Join(
        "  ·  ",
        new[] { Trace.Item.Agent, Trace.Item.ProjectId, Trace.Item.ShortId }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

    public bool HasTrace => Trace.HasTrace;

    public string TraceCaption => Trace.TraceCaption;

    /// <summary>
    /// The state as a chip, in the order <see cref="ItemTrace.Rank"/> puts them: the most urgent true
    /// thing about the row wins, because a chip has room for one word and "blocked" on an item that is
    /// also wedged is the less useful half of the truth.
    /// </summary>
    public string StateWord => Trace switch
    {
        { Motion: Motion.Wedged } => "wedged",
        { Shape: Convergence.Oscillating } => "oscillating",
        { NearCeiling: true } => "near ceiling",
        { Shape: Convergence.Stuck } => "stuck",
        { NeedsPerson: true } => "needs you",
        { Motion: Motion.Parked } => "parked",
        { Motion: Motion.Blocked } => "blocked",
        _ => "moving",
    };

    // Same rule as MotionDot.MotionRole, applied to a text chip: sky moving, amber blocked (on a person
    // or on a dependency), pink wedged, quiet for parked.
    public bool IsWorking => Trace.Motion == Motion.Moving;
    public bool IsAttention => Trace.Motion == Motion.Blocked;
    public bool IsError => Trace.Motion == Motion.Wedged;
}

/// <summary>One chain on the runway, as a phone row.</summary>
public sealed record CodeyBoxChainRow(Chain Chain)
{
    public WorkItemRow Item => Chain.Head;

    public string Title => Chain.Title;

    public IReadOnlyList<Step> Steps => Chain.Steps;

    /// <summary>The strip is only worth drawing for a chain; a singleton's one pip says nothing its
    /// row does not already say.</summary>
    public bool HasStrip => Chain.Steps.Count > 1;

    public string Progress => Chain.Progress;

    public bool HasProgress => Progress.Length > 0;

    /// <summary>What is left of the chain's <c>Why</c> once its section has said the shared part.</summary>
    public string ShownWhy => Chain.ShownWhy;

    public bool HasShownWhy => Chain.HasShownWhy;

    public string Detail => string.Join(
        "  ·  ",
        new[] { Chain.Agent, Chain.ProjectId, Chain.Cost }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

    /// <summary>The head step's state, for the mark at the left of the row.</summary>
    public StepState Mark => Chain.Steps.FirstOrDefault(s => s.Item.Id == Chain.Head.Id)?.State
                             ?? Chain.Steps[^1].State;
}

/// <summary>
/// One section of the queue: Now, Next, a waiting group, or a landed day.
/// </summary>
/// <remarks>
/// <para>The fold is the reason this is a view model rather than a record over the plugin's own
/// <see cref="WaitGroup"/> / <see cref="LandedDay"/>. The desktop pane folds only <c>Next</c>, because a
/// 1200 px column can carry a waiting group whole; a phone cannot carry any of them, and sixteen rows
/// that say the same thing push the next section off the screen exactly as the desktop's sixteen did.
/// So every section here folds by the same rule, at <see cref="Board.NextPreview"/> — the plugin's own
/// number, not a second one.</para>
///
/// <para>A tail of one is never folded: replacing one row with a button that reveals one row is a worse
/// row.</para>
/// </remarks>
public sealed partial class CodeyBoxQueueSection : ObservableObject
{
    private readonly IReadOnlyList<CodeyBoxChainRow> _all;

    public CodeyBoxQueueSection(string header, string sharedWhy, IReadOnlyList<Chain> chains)
    {
        Header = header;
        SharedWhy = sharedWhy;
        _all = [.. chains.Select(c => new CodeyBoxChainRow(c))];
        ShowMoreCommand = new RelayCommand(() => { IsExpanded = true; Apply(); });
        Apply();
    }

    public string Header { get; }

    /// <summary>The line every row in the section was saying, lifted off them by
    /// <c>BoardModel.Lift</c> and drawn once here.</summary>
    public string SharedWhy { get; }

    public bool HasSharedWhy => SharedWhy.Length > 0;

    public ObservableCollection<CodeyBoxChainRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMore))]
    [NotifyPropertyChangedFor(nameof(MoreLabel))]
    private int _hidden;

    public bool HasMore => Hidden > 0;

    public string MoreLabel => Hidden > 0
        ? FormattableString.Invariant($"Show {Hidden} more")
        : string.Empty;

    public bool IsExpanded { get; private set; }

    public IRelayCommand ShowMoreCommand { get; }

    /// <summary>An empty section is not drawn at all — a heading with nothing under it is a question
    /// the screen asks and then declines to answer.</summary>
    public bool HasRows => _all.Count > 0;

    private void Apply()
    {
        var folded = !IsExpanded && _all.Count > Board.NextPreview + 1;
        var shown = folded ? _all.Take(Board.NextPreview).ToList() : _all;

        Rows.Clear();
        foreach (var row in shown)
        {
            Rows.Add(row);
        }

        Hidden = _all.Count - shown.Count;
    }
}

/// <summary>
/// One CodeyBox item that is waiting on a person, as the Inbox shows it.
/// </summary>
/// <remarks>
/// Two shapes, because there are two things a person is being asked for and they take different answers:
/// a <em>question</em> the agent wrote down, which can be answered or dismissed from the list without
/// opening anything; and a <em>failure</em>, which cannot — deciding what to do about one needs the
/// evidence, so that row's action is to open the item's decision card.
/// </remarks>
public sealed record CodeyBoxNeedsRow(WorkItemRow Item, WorkItemQuestion? Question)
{
    /// <summary>"Question" or "Failed" — the same one-word kind the Agnes blocker rows carry.</summary>
    public string Kind => Question is null ? "Failed" : "Question";

    public bool IsQuestion => Question is not null;

    public string Title => Question?.QuestionText ?? Item.Title;

    /// <summary>For a question, which item asked it; for a failure, why it stopped.</summary>
    public string Detail => Question is null
        ? Item.LastError is { Length: > 0 } error ? error : Item.ErrorTitle
        : Item.Title;

    public string Provenance => string.Join(
        "  ·  ",
        new[] { Item.Agent, Item.ProjectId, Item.ShortId }
            .Where(part => !string.IsNullOrWhiteSpace(part)));

    public string Age => Question is null ? Item.Age : Question.Age;

    /// <summary>What the row's link does, which is not the same thing in the two cases: a question has
    /// already been answered or dismissed from here, so opening it is just looking; a failure has not
    /// been decided at all, and that is what the page is for.</summary>
    public string OpenVerb => Question is null ? "Decide what to do" : "Open the item";
}
