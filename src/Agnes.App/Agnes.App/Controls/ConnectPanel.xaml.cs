using System.ComponentModel;
using Agnes.Ui.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Agnes.App.Controls;

public sealed partial class ConnectPanel : UserControl
{
    private WorkspaceViewModel? _workspace;

    public ConnectPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _workspace = DataContext as WorkspaceViewModel ?? global::Agnes.App.App.Workspace;
        if (_workspace is not null)
        {
            _workspace.PropertyChanged += OnWorkspacePropertyChanged;
            ShowCloudflareStatus(_workspace.Status);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_workspace is not null)
        {
            _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
            _workspace = null;
        }
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceViewModel.Status) && sender is WorkspaceViewModel workspace)
        {
            ShowCloudflareStatus(workspace.Status);
        }
    }

    // Use an explicit click handler for this critical browser bootstrap action. It avoids relying on
    // a nested command binding during first render, and gives the operator immediate visible feedback.
    private async void OnCloudflareAccessClick(object sender, RoutedEventArgs e)
    {
        // Set the named element before any interop or network work. If this text changes, the browser
        // event was received; subsequent text is the actual host response.
        CloudflareAccessStatus.Text = "Starting Cloudflare Access sign-in…";
        CloudflareAccessButton.IsEnabled = false;

        var workspace = _workspace ?? DataContext as WorkspaceViewModel ?? global::Agnes.App.App.Workspace;
        if (workspace is null)
        {
            ShowCloudflareStatus("Agnes has not finished initializing. Reload this page and try again.");
            CloudflareAccessButton.IsEnabled = true;
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
            ShowCloudflareStatus("Unable to determine this browser's Agnes address. Reload this page and try again.");
            CloudflareAccessButton.IsEnabled = true;
            return;
        }
#endif

        try
        {
            ShowCloudflareStatus("Requesting an Agnes connection through Cloudflare Access…");
            await workspace.ConnectWithCloudflareAccessCommand.ExecuteAsync(null);
            ShowCloudflareStatus(workspace.Status);
        }
        catch (Exception ex)
        {
            // AsyncRelayCommand normally captures failures, but do not allow a browser-side failure
            // to be silent if that implementation changes.
            ShowCloudflareStatus("Error: " + ex.Message);
        }
        finally
        {
            CloudflareAccessButton.IsEnabled = true;
        }
    }

    private void ShowCloudflareStatus(string status)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            CloudflareAccessStatus.Text = status;
            return;
        }

        DispatcherQueue.TryEnqueue(() => CloudflareAccessStatus.Text = status);
    }
}
