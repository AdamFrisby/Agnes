using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Agnes.App.Desktop.Converters;

/// <summary>
/// A string → bool for `Classes.x` bindings: true when the bound value is one of <see cref="Match"/>
/// (a comma-separated list; ordinal, case-insensitive). Used where the value is a name rather than an
/// enum we own — the ACP plan statuses ("completed" / "in_progress" / "pending") and the event-log
/// kinds, which are event type names.
/// </summary>
public sealed class StringEqualsConverter : IValueConverter
{
    private string[] _match = [];

    public string Match
    {
        get => string.Join(',', _match);
        set => _match = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && Array.Exists(_match, m => string.Equals(s, m, StringComparison.OrdinalIgnoreCase));

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}

/// <summary>
/// A number → bool for `Classes.x` bindings: true once the value reaches <see cref="Threshold"/>.
/// Drives the context meter's mint → amber → pink ramp, so where the thresholds sit is a
/// view-level decision and the colours themselves stay in the theme.
/// </summary>
public sealed class AtLeastConverter : IValueConverter
{
    public double Threshold { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null
           && double.TryParse(System.Convert.ToString(value, CultureInfo.InvariantCulture),
               NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
           && d >= Threshold;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
