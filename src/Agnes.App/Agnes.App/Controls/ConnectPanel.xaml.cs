using Agnes.Ui.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Agnes.App.Controls;

public sealed partial class ConnectPanel : UserControl
{
    public ConnectPanel() => InitializeComponent();

    // Use an explicit click handler for this critical browser bootstrap action. It avoids relying on
    // a nested command binding during first render, and gives the operator immediate visible feedback.
    private void OnCloudflareAccessClick(object sender, RoutedEventArgs e)
    {
        var workspace = DataContext as WorkspaceViewModel ?? global::Agnes.App.App.Workspace;
        if (workspace is null)
        {
            return;
        }

#if __WASM__
        try
        {
            var origin = Uno.Foundation.WebAssemblyRuntime.InvokeJS("window.location.origin");
            if (Uri.TryCreate(origin, UriKind.Absolute, out var browserOrigin))
            {
                workspace.HostUrl = browserOrigin.GetLeftPart(UriPartial.Authority);
            }
        }
        catch
        {
            workspace.Status = "Unable to determine this browser's Agnes address. Enter the Host URL and try again.";
            return;
        }
#endif

        workspace.ConnectWithCloudflareAccessCommand.Execute(null);
    }
}
