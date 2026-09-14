using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Agnes.App.Mobile.Views;

public partial class InboxView : UserControl
{
    public InboxView()
    {
        Agnes.App.Mobile.Services.StartupTrace.MarkOnce("tabview.built InboxView");
        AvaloniaXamlLoader.Load(this);
    }
}
