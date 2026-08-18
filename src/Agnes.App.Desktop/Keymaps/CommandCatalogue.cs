using System.Windows.Input;
using Agnes.App.Desktop.ViewModels;
using Agnes.Ui.Core.ViewModels;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace Agnes.App.Desktop.Keymaps;

public sealed record CommandBinding(ICommand Command, object? Parameter = null);

public sealed record CommandDefinition(
    AgnesCommand Command,
    string Description,
    string Group,
    IReadOnlyList<KeymapContext> Contexts,
    Func<Control, CommandBinding?> Binding)
{
    public string Id => Command.Id();
    public string ContextDisplay => string.Join(", ", Contexts.Select(c => c.Id()));
    public CommandBinding? Bind(Control target) => Binding(target);
}

public static class CommandCatalogue
{
    private static readonly KeymapContext[] Window = [KeymapContext.Window];
    private static readonly KeymapContext[] Palette = [KeymapContext.PaletteFocus];
    private static readonly KeymapContext[] Session = [KeymapContext.Session];

    public static IReadOnlyList<CommandDefinition> All { get; } =
    [
        D(AgnesCommand.TabNew, "New tab", "Tabs and windows", Window),
        D(AgnesCommand.TabClose, "Close the current tab", "Tabs and windows", Window),
        D(AgnesCommand.TabNext, "Next tab", "Tabs and windows", Window),
        D(AgnesCommand.TabPrevious, "Previous tab", "Tabs and windows", Window),
        .. Enumerable.Range(1, 9).Select(i => D((AgnesCommand)((int)AgnesCommand.TabPosition1 + i - 1), $"Jump to tab {i}", "Tabs and windows", Window)),
        D(AgnesCommand.PaletteOpen, "Open or close the command palette", "Tabs and windows", Window),
        D(AgnesCommand.DashboardOpen, "Open the status dashboard", "Tabs and windows", Window),
        D(AgnesCommand.PaletteNext, "Move to the next command", "Command palette", Palette),
        D(AgnesCommand.PalettePrevious, "Move to the previous command", "Command palette", Palette),
        D(AgnesCommand.PaletteRun, "Run the selected command", "Command palette", Palette),
        D(AgnesCommand.PaletteClose, "Close the command palette", "Command palette", Palette),
        D(AgnesCommand.AllSessionsSearch, "Search every session", "Search and Settings", [KeymapContext.AllSessionsSearchFocus]),
        D(AgnesCommand.SettingsMcpSearch, "Search MCP registries", "Search and Settings", [KeymapContext.SettingsMcpSearchFocus]),
        D(AgnesCommand.SettingsPluginSearch, "Search plugins", "Search and Settings", [KeymapContext.SettingsPluginSearchFocus]),
        D(AgnesCommand.SettingsSkillSearch, "Search skills", "Search and Settings", [KeymapContext.SettingsSkillSearchFocus]),
        D(AgnesCommand.LaunchProfileRenameCommit, "Save a launch-profile name", "Search and Settings", [KeymapContext.SettingsLaunchProfileRenameFocus]),
        D(AgnesCommand.LaunchProfileRenameCancel, "Cancel launch-profile rename", "Search and Settings", [KeymapContext.SettingsLaunchProfileRenameFocus]),
        D(AgnesCommand.SessionRenameCommit, "Save the session name", "Session editing", [KeymapContext.SessionRenameFocus]),
        D(AgnesCommand.SessionRenameCancel, "Cancel session rename", "Session editing", [KeymapContext.SessionRenameFocus]),
        D(AgnesCommand.SessionTagAdd, "Add a session tag", "Session editing", [KeymapContext.SessionTagFocus]),
        D(AgnesCommand.SessionReferenceAdd, "Add a file or URL reference", "Session editing", [KeymapContext.SessionReferenceFocus]),
        D(AgnesCommand.SessionFindOpen, "Find in this session", "Transcript navigation", Session),
        D(AgnesCommand.SessionFindClose, "Close session find", "Transcript navigation", [KeymapContext.Session, KeymapContext.SessionFindFocus]),
        D(AgnesCommand.SessionFindNext, "Next find result", "Transcript navigation", [KeymapContext.Session, KeymapContext.SessionFindFocus]),
        D(AgnesCommand.SessionFindPrevious, "Previous find result", "Transcript navigation", [KeymapContext.Session, KeymapContext.SessionFindFocus]),
        D(AgnesCommand.SessionPromptNext, "Next prompt", "Transcript navigation", Session),
        D(AgnesCommand.SessionPromptPrevious, "Previous prompt", "Transcript navigation", Session),
        D(AgnesCommand.SessionChangeNext, "Next file change", "Transcript navigation", Session),
        D(AgnesCommand.SessionChangePrevious, "Previous file change", "Transcript navigation", Session),
        D(AgnesCommand.ComposerSend, "Send, or queue behind a running turn", "Writing a prompt", [KeymapContext.ComposerFocus]),
        D(AgnesCommand.ComposerSendNow, "Interrupt the running turn and send now", "Writing a prompt", [KeymapContext.ComposerFocus]),
        D(AgnesCommand.ComposerRecallPrevious, "Recall the previous prompt", "Writing a prompt", [KeymapContext.ComposerFocus]),
        D(AgnesCommand.ComposerRecallNext, "Recall the next prompt", "Writing a prompt", [KeymapContext.ComposerFocus]),
    ];

    public static CommandDefinition Definition(AgnesCommand command) => All.Single(d => d.Command == command);

    private static CommandBinding? Bind(AgnesCommand command, Control target)
    {
        var main = Find<MainWindowViewModel>(target);
        var sessionDocument = Find<SessionDocument>(target);
        var session = Find<SessionViewModel>(target) ?? sessionDocument?.Session;
        var memory = Find<MemorySearchViewModel>(target);
        var plugins = Find<PluginManagementViewModel>(target);
        var prompts = Find<PromptLibraryViewModel>(target);
        var profiles = Find<LaunchProfilesViewModel>(target);

        return command switch
        {
            AgnesCommand.TabNew when main is not null => new(main.NewTabCommand),
            AgnesCommand.TabClose when main is not null => new(main.CloseActiveTabCommand),
            AgnesCommand.TabNext when main is not null => new(main.NextTabCommand),
            AgnesCommand.TabPrevious when main is not null => new(main.PrevTabCommand),
            >= AgnesCommand.TabPosition1 and <= AgnesCommand.TabPosition9 when main is not null
                => new(main.ActivateTabByIndexCommand, ((int)command - (int)AgnesCommand.TabPosition1 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            AgnesCommand.PaletteOpen when main is not null => new(main.TogglePaletteCommand),
            AgnesCommand.PaletteNext when main is not null => new(main.MovePaletteSelectionCommand, "down"),
            AgnesCommand.PalettePrevious when main is not null => new(main.MovePaletteSelectionCommand, "up"),
            AgnesCommand.PaletteRun when main is not null => new(main.RunTopPaletteItemCommand),
            AgnesCommand.PaletteClose when main is not null => new(main.ClosePaletteCommand),
            AgnesCommand.DashboardOpen when main is not null => new(main.OpenDashboardCommand),
            AgnesCommand.AllSessionsSearch when memory is not null => new(memory.SearchCommand),
            AgnesCommand.SettingsMcpSearch when main is not null => new(main.SearchMcpCatalogCommand),
            AgnesCommand.SettingsPluginSearch when plugins is not null => new(plugins.SearchCommand),
            AgnesCommand.SettingsSkillSearch when prompts is not null => new(prompts.BrowseRegistryCommand),
            AgnesCommand.LaunchProfileRenameCommit when profiles is not null => new(profiles.CommitRenameCommand, target.DataContext),
            AgnesCommand.LaunchProfileRenameCancel when profiles is not null => new(profiles.CancelRenameCommand, target.DataContext),
            AgnesCommand.SessionRenameCommit when sessionDocument is not null => new(sessionDocument.CommitRenameCommand),
            AgnesCommand.SessionRenameCancel when sessionDocument is not null => new(sessionDocument.CancelRenameCommand),
            AgnesCommand.SessionTagAdd when sessionDocument is not null => new(sessionDocument.AddTagCommand),
            AgnesCommand.SessionFindOpen when session is not null => new(session.OpenSearchCommand),
            AgnesCommand.SessionFindClose when session is not null => new(session.CloseSearchCommand),
            AgnesCommand.SessionFindNext when session is not null => new(session.NextMatchCommand),
            AgnesCommand.SessionFindPrevious when session is not null => new(session.PrevMatchCommand),
            AgnesCommand.SessionPromptNext when session is not null => new(session.NextPromptCommand),
            AgnesCommand.SessionPromptPrevious when session is not null => new(session.PrevPromptCommand),
            AgnesCommand.SessionChangeNext when session is not null => new(session.NextChangeCommand),
            AgnesCommand.SessionChangePrevious when session is not null => new(session.PrevChangeCommand),
            AgnesCommand.SessionReferenceAdd when session is not null => new(session.AddReferenceCommand),
            AgnesCommand.ComposerSend when session is not null => new(session.SendCommand),
            AgnesCommand.ComposerSendNow when session is not null => new(session.SendNowCommand),
            AgnesCommand.ComposerRecallPrevious when session is not null => new(session.RecallPreviousCommand),
            AgnesCommand.ComposerRecallNext when session is not null => new(session.RecallNextCommand),
            _ => null,
        };
    }

    private static CommandDefinition D(AgnesCommand command, string description, string group, IReadOnlyList<KeymapContext> contexts)
        => new(command, description, group, contexts, target => Bind(command, target));

    private static T? Find<T>(Control control) where T : class
    {
        if (control.DataContext is T direct) return direct;
        var ancestors = control.GetVisualAncestors().OfType<Control>()
            .Concat(control.GetLogicalAncestors().OfType<Control>())
            .Distinct();
        foreach (var ancestor in ancestors)
        {
            if (ancestor.DataContext is T value) return value;
            if (ancestor.DataContext is SettingsDocument settings && settings.Owner is T owner) return owner;
            if (ancestor.DataContext is SearchDocument search && search.Search is T memory) return memory;
        }

        return null;
    }
}
