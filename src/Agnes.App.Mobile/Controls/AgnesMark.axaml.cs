using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Agnes.App.Mobile.Controls;

/// <summary>The Agnes squid mark. Scales to whatever box it's given.</summary>
public partial class AgnesMark : UserControl
{
    public AgnesMark() => AvaloniaXamlLoader.Load(this);
}
