using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Agnes.App.Mobile.Views;

public partial class CodeyBoxView : UserControl
{
    public CodeyBoxView()
    {
        Agnes.App.Mobile.Services.StartupTrace.MarkOnce("tabview.built CodeyBoxView");
        AvaloniaXamlLoader.Load(this);
    }
}
