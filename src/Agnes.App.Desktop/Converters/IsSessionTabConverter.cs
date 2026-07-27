using System;
using System.Globalization;
using Agnes.App.Desktop.ViewModels;
using Avalonia.Data.Converters;

namespace Agnes.App.Desktop.Converters;

/// <summary>
/// True when a tab is a session rather than one of the utility documents (Settings, Dashboard, Search, a
/// plugin screen). The tab-strip context menu is shared by all of them because Dock styles one item type,
/// so the session-only half of that menu asks this rather than binding to commands that simply don't
/// exist on the others — which would leave a Settings tab offering "Fork", greyed out and unexplained.
/// </summary>
public sealed class IsSessionTabConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SessionDocument;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
