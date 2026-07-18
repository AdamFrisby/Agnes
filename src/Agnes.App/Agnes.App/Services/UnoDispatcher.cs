using Agnes.Ui.Core;
using Microsoft.UI.Dispatching;

namespace Agnes.App.Services;

/// <summary>Marshals view-model callbacks onto the UI thread via the window's dispatcher queue.</summary>
public sealed class UnoDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;

    public UnoDispatcher(DispatcherQueue queue) => _queue = queue;

    public void Post(Action action)
    {
        if (_queue.HasThreadAccess)
        {
            action();
        }
        else
        {
            _queue.TryEnqueue(() => action());
        }
    }
}
