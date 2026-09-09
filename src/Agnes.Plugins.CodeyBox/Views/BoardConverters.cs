using System.Collections;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Agnes.Plugins.CodeyBox.Views;

/// <summary>
/// The questions the runway asks that a binding path cannot: "is this the chain the pane is showing",
/// "which hue does this Why line deserve", "is this day today".
/// </summary>
/// <remarks>
/// They live here rather than as extra members on <see cref="Board"/> and friends because every one of
/// them is a fact about how <em>this</em> view draws the board, not about the board. Another head could
/// render the same record with a different idea of what deserves a hue.
/// </remarks>
public static class BoardConverters
{
    /// <summary>The head's state, which is what the row's mark is drawn from.</summary>
    public static readonly IValueConverter HeadState =
        new FuncValueConverter<Chain?, StepState>(chain => chain is null
            ? StepState.Ready
            : StateOfHead(chain) ?? StepState.Ready);

    /// <summary>
    /// The Why line is pink only where something is actually broken and nobody is being asked to decide
    /// anything: the head failed, or the parent holding the chain failed. Everything else that is merely
    /// stopped states a fact and stays dim.
    /// </summary>
    public static readonly IValueConverter WhyIsError =
        new FuncValueConverter<Chain?, bool>(IsError);

    /// <summary>Amber is reserved for "blocked on you", which for a chain means it needs a decision.</summary>
    public static readonly IValueConverter WhyNeedsPerson =
        new FuncValueConverter<Chain?, bool>(NeedsPerson);

    /// <summary>Neither: a fact, not a call to action.</summary>
    public static readonly IValueConverter WhyIsPlain =
        new FuncValueConverter<Chain?, bool>(chain => !IsError(chain) && !NeedsPerson(chain));

    /// <summary>Where the composer was opened from, in words rather than as an enum name.</summary>
    public static readonly IValueConverter IntentWord =
        new FuncValueConverter<ComposerIntent, string>(intent => intent switch
        {
            ComposerIntent.FollowUp => "follow-up",
            ComposerIntent.Sibling => "sibling",
            ComposerIntent.Split => "split",
            ComposerIntent.Duplicate => "duplicate",
            ComposerIntent.Promote => "from a suggestion",
            _ => "new",
        });

    /// <summary>Today's landed work is open; the rest of the week is folded away.</summary>
    public static readonly IValueConverter IsToday =
        new FuncValueConverter<LandedDay?, bool>(day => day is not null
            && day.Title.Equals("Today", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a list has anything in it, for a section that should not appear empty.</summary>
    public static readonly IValueConverter Any =
        new FuncValueConverter<IEnumerable?, bool>(HasAny);

    /// <summary>Its complement, for the sentence that stands in for an empty list.</summary>
    public static readonly IValueConverter None =
        new FuncValueConverter<IEnumerable?, bool>(items => !HasAny(items));

    /// <summary>A count that means "there is something to do", for an Add button that must not fire empty.</summary>
    public static readonly IValueConverter Positive =
        new FuncValueConverter<int, bool>(count => count > 0);

    /// <summary>Whether a chip's editor is open at all, given which chip is.</summary>
    public static readonly IValueConverter NotEmpty =
        new FuncValueConverter<string?, bool>(field => !string.IsNullOrEmpty(field));

    /// <summary>
    /// What the create button says. A chain states its size, because "Create" on a paste that silently
    /// became seven items is the one press in this screen that cannot be taken back.
    /// </summary>
    public static readonly IValueConverter CreateLabel =
        new FuncValueConverter<Plan?, string>(plan => plan is null || !plan.IsChain
            ? "Create"
            : string.Create(CultureInfo.CurrentCulture, $"Create {plan.Drafts.Count} steps"));

    /// <summary>The id of whatever the pane is showing, for a strip's selected ring.</summary>
    public static readonly IValueConverter IdOf =
        new FuncValueConverter<WorkItemRow?, string?>(item => item?.Id);

    /// <summary>A relation's state, as the chip says it: "failed", "needs a person".</summary>
    public static readonly IValueConverter StateWord =
        new FuncValueConverter<StepState, string>(Controls.StepMarks.Word);

    /// <summary>"Blocked by Wire the broker (failed)" — the one line, composed.</summary>
    public static readonly IValueConverter BlockedBy =
        new FuncValueConverter<Relation?, string>(relation => relation is null
            ? string.Empty
            : string.Create(CultureInfo.CurrentCulture,
                            $"Blocked by {relation.Item.Title} ({Controls.StepMarks.Word(relation.State)})"));

    /// <summary>A failed parent is the one an operator can retry.</summary>
    public static readonly IValueConverter CanRetry =
        new FuncValueConverter<Relation?, bool>(relation => relation?.State == StepState.Failed);

    /// <summary>A cancelled parent is the one an operator can uncancel.</summary>
    public static readonly IValueConverter CanUncancel =
        new FuncValueConverter<Relation?, bool>(relation => relation?.State == StepState.Cancelled);

    /// <summary>
    /// The step numbers a plan step can be told to wait on: 1 to N−1, where N is the plan's length.
    /// </summary>
    /// <remarks>
    /// The range is a property of the plan rather than of any one step, which is why it is taken from the
    /// step list rather than from the step. Sending a step its own number is not a problem the view has
    /// to solve — the composer's toggle rejects an edge that would make a cycle, and it is the only place
    /// that can, since it is the only place that knows what the other edges are.
    /// </remarks>
    public static readonly IValueConverter ParentIndices =
        new FuncValueConverter<IEnumerable?, IReadOnlyList<int>>(steps =>
        {
            var count = steps switch
            {
                null => 0,
                ICollection collection => collection.Count,
                _ => steps.Cast<object?>().Count(),
            };

            return count < 2 ? [] : [.. Enumerable.Range(1, count - 1)];
        });

    /// <summary>
    /// True when the bound value equals the converter parameter, both ways — the binding that lets an
    /// enum drive a row of chips without a command per chip.
    /// </summary>
    public static readonly IValueConverter EnumEquals = new EnumEqualsConverter();

    /// <summary>
    /// Is this the row the pane is showing? Values: the row (a <see cref="Chain"/>, <see cref="Step"/> or
    /// <see cref="WorkItemRow"/>), then the current selection.
    /// </summary>
    public static readonly IMultiValueConverter IsSelected = new SelectionConverter();

    /// <summary>
    /// Should this row offer itself as a destination? Values: the row's chain, then the armed move.
    /// </summary>
    /// <remarks>
    /// True for every queued row except the one being moved — offering "put it after this" on the chain
    /// you just picked up is an instruction to put it after itself, which is not a move and reads as a
    /// bug rather than as a no-op.
    /// </remarks>
    public static readonly IMultiValueConverter PendingElsewhere = new PendingElsewhereConverter();

    /// <summary>"Choose where Rewrite the credential broker should run", or nothing when no move is armed.</summary>
    public static readonly IValueConverter ChooseWhere =
        new FuncValueConverter<Chain?, string>(chain => chain is null
            ? string.Empty
            : string.Create(CultureInfo.CurrentCulture, $"Choose where {chain.Title} should run"));

    /// <summary>
    /// Read from the head's STEP state rather than from the raw work-item state, and after
    /// <see cref="NeedsPerson"/> rather than before it.
    /// </summary>
    /// <remarks>
    /// Both details matter, and both were got wrong first. An item can be Failed on the orchestrator and
    /// still be a step waiting on a person — a failure awaiting a decision is the commonest shape of
    /// that — and the row's own mark, drawn from the step state, calls it amber. A Why line reading pink
    /// beside an amber mark on the same row says the two are about different things, which they are not.
    /// </remarks>
    private static bool IsError(Chain? chain)
        => chain is not null
           && !NeedsPerson(chain)
           && (chain.Blocker?.State is StepState.Failed || StateOfHead(chain) == StepState.Failed);

    private static bool NeedsPerson(Chain? chain)
        => chain is not null
           && (chain.Reason == WaitReason.Person || StateOfHead(chain) == StepState.NeedsPerson);

    /// <summary>The state of the step the row stands for, or none when the head is not among them.</summary>
    private static StepState? StateOfHead(Chain chain)
        => chain.Steps.FirstOrDefault(s => s.Item.Id == chain.Head.Id)?.State;

    private static bool HasAny(IEnumerable? items)
    {
        if (items is null)
        {
            return false;
        }

        if (items is ICollection collection)
        {
            return collection.Count > 0;
        }

        var walk = items.GetEnumerator();
        try
        {
            return walk.MoveNext();
        }
        finally
        {
            (walk as IDisposable)?.Dispose();
        }
    }

    /// <summary>The id a row is addressed by, whichever shape the row happens to be.</summary>
    private static string? IdIn(object? value) => value switch
    {
        Chain chain => chain.Head.Id,
        Step step => step.Item.Id,
        Relation relation => relation.Item.Id,
        DependencyCandidate candidate => candidate.Item.Id,
        WorkItemRow row => row.Id,
        _ => null,
    };

    private sealed class SelectionConverter : IMultiValueConverter
    {
        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count < 2)
            {
                return false;
            }

            var row = IdIn(values[0]);
            var selected = IdIn(values[1]);
            return row is { Length: > 0 } && string.Equals(row, selected, StringComparison.Ordinal);
        }
    }

    private sealed class PendingElsewhereConverter : IMultiValueConverter
    {
        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
            => values.Count >= 2
               && values[0] is Chain row
               && row.IsNext
               && values[1] is Chain moving
               && !string.Equals(row.Id, moving.Id, StringComparison.Ordinal);
    }

    private sealed class EnumEqualsConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is not null && value.Equals(parameter);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            // Unticking is not a choice — one of the chips is always the answer — so only the tick
            // writes back, and clicking the current one leaves the value where it is.
            => value is true ? parameter : BindingOperations.DoNothing;
    }
}
