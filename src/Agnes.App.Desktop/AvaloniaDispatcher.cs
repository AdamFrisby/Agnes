using Agnes.Ui.Core;
using Avalonia.Threading;

namespace Agnes.App.Desktop;

/// <summary>Marshals view-model callbacks onto Avalonia's UI thread.</summary>
public sealed class AvaloniaDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
