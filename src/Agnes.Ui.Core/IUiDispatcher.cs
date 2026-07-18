namespace Agnes.Ui.Core;

/// <summary>Marshals an action onto the UI thread. Implemented per platform by the app.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Runs actions inline; useful for tests and non-UI contexts.</summary>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    public static readonly ImmediateDispatcher Instance = new();

    public void Post(Action action) => action();
}
