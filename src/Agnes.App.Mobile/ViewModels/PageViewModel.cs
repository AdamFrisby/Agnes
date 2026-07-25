using CommunityToolkit.Mvvm.ComponentModel;

namespace Agnes.App.Mobile.ViewModels;

/// <summary>
/// A full-screen destination pushed onto the shell's navigation stack.
///
/// The app is one Android activity with an in-app stack, so "back" is a single concept the shell owns:
/// it closes a sheet, then pops a page, then leaves the app. That is what makes the system back gesture
/// behave the way a phone user expects without any activity churn.
/// </summary>
public abstract partial class PageViewModel : ObservableObject
{
    /// <summary>Shown in the page's app bar.</summary>
    public abstract string Title { get; }

    /// <summary>Optional second line under the title (host, project, agent).</summary>
    public virtual string? Subtitle => null;


    /// <summary>Called when the page becomes the visible top of the stack.</summary>
    public virtual void OnAppearing() { }

    /// <summary>Called when the page is covered or popped.</summary>
    public virtual void OnDisappearing() { }

    /// <summary>Gives the page first refusal on the back gesture (e.g. to close an inline search field).
    /// Returning true means the page handled it and should not be popped.</summary>
    public virtual bool OnBackRequested() => false;
}
