using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Agnes.App.Desktop.Converters;

/// <summary>A bool → opacity: <see cref="TrueOpacity"/> when true, <see cref="FalseOpacity"/> when false.
/// Used to dim finished agents in the roster (shown via "show all") without a second template.</summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public double TrueOpacity { get; set; } = 1.0;
    public double FalseOpacity { get; set; } = 0.5;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? TrueOpacity : FalseOpacity;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
