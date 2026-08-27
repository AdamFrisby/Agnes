using Agnes.Ui.Core.Plugins;
using Dock.Model.Mvvm.Controls;

namespace Agnes.App.Desktop.ViewModels;

/// <summary>
/// A dock document hosting a client plugin's custom screen — the same mechanism the built-in Settings tab
/// uses to replace the conversation view (see <c>.ideas/00d-event-spine-and-ui-extensibility.md</c>, AC7).
/// The plugin owns <see cref="ScreenViewModel"/>; the view layer resolves it to a view (a plugin ships its
/// own view/data-template; a VM with no template falls back to the default presenter).
/// </summary>
public sealed class PluginScreenDocument : Document
{
    /// <param name="resolveView">
    /// Asks the plugin set for a view for this screen's view-model (see
    /// <see cref="Agnes.Ui.Core.Plugins.IViewFactory"/>). Optional so a caller with no plugin set — a test,
    /// or a head that renders plugin view-models its own way — still gets a document.
    /// </param>
    public PluginScreenDocument(ICustomScreenProvider provider, Func<object, object?>? resolveView = null)
    {
        Id = provider.ScreenId;
        Title = provider.Title;
        Icon = provider.Icon;
        CanClose = true;
        ScreenViewModel = provider.CreateViewModel();
        ScreenView = resolveView?.Invoke(ScreenViewModel);
    }

    /// <summary>The plugin-owned view-model this screen renders.</summary>
    public object ScreenViewModel { get; }

    /// <summary>The view the plugin supplied for it, or null when it supplied none.</summary>
    public object? ScreenView { get; }

    /// <summary>
    /// What the tab actually presents. The plugin's own view when it registered one — an Avalonia control,
    /// which a <c>ContentControl</c> hosts directly — and otherwise the bare view-model, left for the head's
    /// own templates to match and, failing that, the default presenter. One property rather than two so the
    /// view has no branch in it.
    /// </summary>
    public object ScreenContent => ScreenView ?? ScreenViewModel;

    /// <summary>Optional icon glyph the plugin supplied.</summary>
    public string? Icon { get; }
}
