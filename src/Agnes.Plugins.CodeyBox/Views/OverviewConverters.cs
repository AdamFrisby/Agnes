using System.Globalization;
using Avalonia.Data.Converters;

namespace Agnes.Plugins.CodeyBox.Views;

/// <summary>
/// The two "is there anything to draw here" questions the overview asks about a whole list.
/// </summary>
/// <remarks>
/// Both empty states are sentences the operator reads once — "sparklines fill in as the tab is left
/// open", "no quota history on this host" — and both depend on every element of a list rather than on
/// the list being empty, which a binding path cannot express. They live here rather than as extra
/// members on <see cref="Overview"/> because they are facts about how this view degrades, not about the
/// fleet: another head could show the same overview without either sentence.
/// </remarks>
public static class OverviewConverters
{
    /// <summary>True when not one vital has a control band yet, so the sparklines have nothing to say.</summary>
    public static readonly IValueConverter NoVitalHasBand =
        new FuncValueConverter<IEnumerable<Vital>?, bool>(vitals => vitals is null || !vitals.Any(v => v.HasBand));

    /// <summary>True when no agent's quota window has enough samples to draw a burn-down.</summary>
    public static readonly IValueConverter NoQuotaHistory =
        new FuncValueConverter<IEnumerable<QuotaBurn>?, bool>(quota => quota is null || !quota.Any(q => q.HasSamples));

    /// <summary>The complement of <see cref="NoQuotaHistory"/>: there is at least one burn-down worth drawing.</summary>
    public static readonly IValueConverter HasQuotaHistory =
        new FuncValueConverter<IEnumerable<QuotaBurn>?, bool>(quota => quota is not null && quota.Any(q => q.HasSamples));

    /// <summary>The latest iteration as the row states it: "iteration 14 of 25", or just the count.</summary>
    public static readonly IValueConverter Iterations =
        new FuncValueConverter<ItemTrace?, string>(trace => trace is null || !trace.HasTrace
            ? string.Empty
            : trace.Ceiling > 0
                ? string.Create(CultureInfo.CurrentCulture, $"iteration {trace.LastIteration} of {trace.Ceiling}")
                : string.Create(CultureInfo.CurrentCulture, $"iteration {trace.LastIteration}"));
}
