using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Agnes.App.Mobile.Views;

/// <summary>The sheet for a file the agent sent: preview, then share / save / open.</summary>
public partial class ReceivedFileSheetView : UserControl
{
    /// <summary>Creates the view.</summary>
    public ReceivedFileSheetView() => AvaloniaXamlLoader.Load(this);
}
