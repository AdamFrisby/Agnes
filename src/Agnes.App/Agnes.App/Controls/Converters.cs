using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace Agnes.App.Controls;

/// <summary>Non-null → Visible, null → Collapsed (invert with parameter "invert").</summary>
public sealed class ObjectToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var visible = value is not null;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>true → the app accent brush, false → a neutral card brush (for message bubbles).</summary>
public sealed class BoolToBubbleBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var isUser = value is true;
        var color = isUser
            ? Microsoft.UI.ColorHelper.FromArgb(0x33, 0x4F, 0x8A, 0xF7)
            : Microsoft.UI.ColorHelper.FromArgb(0x22, 0x88, 0x88, 0x88);
        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
