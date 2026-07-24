using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Agnes.App.Desktop.Converters;

/// <summary>An <c>int</c> depth → a left-indent <see cref="Thickness"/> (<c>depth * Step</c>). Used to
/// indent nested rows (e.g. the agent roster) without hard-coding a margin per level.</summary>
public sealed class IndentConverter : IValueConverter
{
    public double Step { get; set; } = 16;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => new Thickness(value is int d ? d * Step : 0, 0, 0, 0);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
