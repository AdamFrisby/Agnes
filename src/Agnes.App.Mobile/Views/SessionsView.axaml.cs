using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Agnes.App.Mobile.Views;

public partial class SessionsView : UserControl
{
    public SessionsView()
    {
        Agnes.App.Mobile.Services.StartupTrace.MarkOnce("tabview.built SessionsView");
        AvaloniaXamlLoader.Load(this);
    }
}
