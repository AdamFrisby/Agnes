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
    public PluginScreenDocument(ICustomScreenProvider provider)
    {
        Id = provider.ScreenId;
        Title = provider.Title;
        Icon = provider.Icon;
        CanClose = true;
        ScreenViewModel = provider.CreateViewModel();
    }

    /// <summary>The plugin-owned view-model this screen renders.</summary>
    public object ScreenViewModel { get; }

    /// <summary>Optional icon glyph the plugin supplied.</summary>
    public string? Icon { get; }
}
