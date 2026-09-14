using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Agnes.App.Mobile.Views;

public partial class MoreView : UserControl
{
    public MoreView()
    {
        Agnes.App.Mobile.Services.StartupTrace.MarkOnce("tabview.built MoreView");
        AvaloniaXamlLoader.Load(this);
    }
}
