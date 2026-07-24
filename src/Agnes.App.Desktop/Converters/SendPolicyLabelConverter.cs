using System;
using System.Globalization;
using Agnes.Abstractions;
using Avalonia.Data.Converters;

namespace Agnes.App.Desktop.Converters;

/// <summary>Turns the <see cref="SendPolicy"/> enum into a human label for the "When busy" dropdown, so it
/// reads "Queue it" instead of "QueueInAgent".</summary>
public sealed class SendPolicyLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SendPolicy p
            ? p switch
            {
                SendPolicy.QueueInAgent => "Queue it",
                SendPolicy.InterruptAndSend => "Interrupt & send now",
                SendPolicy.PendingUntilReady => "Hold (send manually)",
                _ => p.ToString(),
            }
            : value?.ToString() ?? string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
