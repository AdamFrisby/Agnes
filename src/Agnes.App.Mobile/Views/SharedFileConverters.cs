using System.Globalization;
using Agnes.App.Mobile.ViewModels;
using Agnes.Ui.Core.Transcript;
using Avalonia.Data.Converters;

namespace Agnes.App.Mobile.Views;

/// <summary>
/// Which of the three type glyphs a received file wears: a picture, a document, or a plain file.
///
/// A converter rather than three <see cref="SharedFileItem"/> properties because the item is the shared
/// contract type — every head reads it, and "which Lucide glyph does the Android transcript draw" is not
/// something that belongs in <c>Agnes.Ui.Core</c>. It answers one parameter at a time so the view can hang
/// three icons off it and let exactly one be visible, which is all XAML can express without a converter
/// anyway (bindings negate one boolean, not conjoin two).
/// </summary>
public sealed class SharedFileKindConverter : IValueConverter
{
    /// <summary>The single instance views bind to.</summary>
    public static readonly SharedFileKindConverter Instance = new();

    /// <summary>True when the file is of the kind named by <paramref name="parameter"/>: <c>image</c>,
    /// <c>document</c> (text or PDF) or <c>other</c>.</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var file = value switch
        {
            SharedFileItem item => item,
            SharedFileRow row => row.File,
            _ => null,
        };

        if (file is null)
        {
            return false;
        }

        var document = file.IsText || file.IsPdf;
        return (parameter as string) switch
        {
            "image" => file.IsImage,
            "document" => document,
            _ => !file.IsImage && !document,
        };
    }

    /// <summary>Not supported — the mapping is one-way by nature.</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
