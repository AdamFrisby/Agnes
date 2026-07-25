using Agnes.Ui.Core;
using Avalonia.Threading;

namespace Agnes.App.Mobile.Services;

/// <summary>Marshals view-model callbacks onto Avalonia's UI thread.</summary>
public sealed class MobileDispatcher : IUiDispatcher
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
