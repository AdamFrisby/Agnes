using Agnes.Ui.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Agnes.App.Shells;

public sealed partial class MobileShell : UserControl
{
    public MobileShell() => InitializeComponent();

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (DataContext is WorkspaceViewModel workspace)
        {
            workspace.CloseSession();
        }
    }
}
