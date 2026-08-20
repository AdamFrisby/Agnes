namespace Agnes.App.Desktop.Keymaps;

public static class KeymapNames
{
    private static readonly IReadOnlyDictionary<AgnesCommand, string> CommandIds = new Dictionary<AgnesCommand, string>
    {
        [AgnesCommand.TabNew] = "agnes.tab.new",
        [AgnesCommand.TabClose] = "agnes.tab.close",
        [AgnesCommand.TabNext] = "agnes.tab.next",
        [AgnesCommand.TabPrevious] = "agnes.tab.previous",
        [AgnesCommand.TabPosition1] = "agnes.tab.position1",
        [AgnesCommand.TabPosition2] = "agnes.tab.position2",
        [AgnesCommand.TabPosition3] = "agnes.tab.position3",
        [AgnesCommand.TabPosition4] = "agnes.tab.position4",
        [AgnesCommand.TabPosition5] = "agnes.tab.position5",
        [AgnesCommand.TabPosition6] = "agnes.tab.position6",
        [AgnesCommand.TabPosition7] = "agnes.tab.position7",
        [AgnesCommand.TabPosition8] = "agnes.tab.position8",
        [AgnesCommand.TabPosition9] = "agnes.tab.position9",
        [AgnesCommand.PaletteOpen] = "agnes.palette.open",
        [AgnesCommand.PaletteNext] = "agnes.palette.next",
        [AgnesCommand.PalettePrevious] = "agnes.palette.previous",
        [AgnesCommand.PaletteRun] = "agnes.palette.run",
        [AgnesCommand.PaletteClose] = "agnes.palette.close",
        [AgnesCommand.DashboardOpen] = "agnes.dashboard.open",
        [AgnesCommand.AllSessionsSearch] = "agnes.search.allSessions",
        [AgnesCommand.SettingsMcpSearch] = "agnes.settings.mcp.search",
        [AgnesCommand.SettingsPluginSearch] = "agnes.settings.plugin.search",
        [AgnesCommand.SettingsSkillSearch] = "agnes.settings.skill.search",
        [AgnesCommand.LaunchProfileRenameCommit] = "agnes.launchProfile.rename.commit",
        [AgnesCommand.LaunchProfileRenameCancel] = "agnes.launchProfile.rename.cancel",
        [AgnesCommand.SessionRenameCommit] = "agnes.session.rename.commit",
        [AgnesCommand.SessionRenameCancel] = "agnes.session.rename.cancel",
        [AgnesCommand.SessionTagAdd] = "agnes.session.tag.add",
        [AgnesCommand.SessionFindOpen] = "agnes.session.find.open",
        [AgnesCommand.SessionFindClose] = "agnes.session.find.close",
        [AgnesCommand.SessionFindNext] = "agnes.session.find.next",
        [AgnesCommand.SessionFindPrevious] = "agnes.session.find.previous",
        [AgnesCommand.SessionPromptNext] = "agnes.session.prompt.next",
        [AgnesCommand.SessionPromptPrevious] = "agnes.session.prompt.previous",
        [AgnesCommand.SessionChangeNext] = "agnes.session.change.next",
        [AgnesCommand.SessionChangePrevious] = "agnes.session.change.previous",
        [AgnesCommand.SessionReferenceAdd] = "agnes.session.reference.add",
        [AgnesCommand.ComposerSend] = "agnes.composer.send",
        [AgnesCommand.ComposerSendNow] = "agnes.composer.sendNow",
        [AgnesCommand.ComposerRecallPrevious] = "agnes.composer.recallPrevious",
        [AgnesCommand.ComposerRecallNext] = "agnes.composer.recallNext",
    };

    private static readonly IReadOnlyDictionary<KeymapContext, string> ContextIds = new Dictionary<KeymapContext, string>
    {
        [KeymapContext.Window] = "window",
        [KeymapContext.PaletteFocus] = "paletteFocus",
        [KeymapContext.AllSessionsSearchFocus] = "allSessionsSearchFocus",
        [KeymapContext.SettingsMcpSearchFocus] = "settingsMcpSearchFocus",
        [KeymapContext.SettingsPluginSearchFocus] = "settingsPluginSearchFocus",
        [KeymapContext.SettingsSkillSearchFocus] = "settingsSkillSearchFocus",
        [KeymapContext.SettingsLaunchProfileRenameFocus] = "settingsLaunchProfileRenameFocus",
        [KeymapContext.Session] = "session",
        [KeymapContext.SessionRenameFocus] = "sessionRenameFocus",
        [KeymapContext.SessionTagFocus] = "sessionTagFocus",
        [KeymapContext.SessionFindFocus] = "sessionFindFocus",
        [KeymapContext.SessionReferenceFocus] = "sessionReferenceFocus",
        [KeymapContext.ComposerFocus] = "composerFocus",
    };

    public static string Id(this AgnesCommand command) => CommandIds[command];
    public static string Id(this KeymapContext context) => ContextIds[context];

    public static bool TryCommand(string value, out AgnesCommand command)
    {
        foreach (var pair in CommandIds)
        {
            if (string.Equals(pair.Value, value, StringComparison.OrdinalIgnoreCase))
            {
                command = pair.Key;
                return true;
            }
        }

        command = default;
        return false;
    }

    public static bool TryContext(string value, out KeymapContext context)
    {
        foreach (var pair in ContextIds)
        {
            if (string.Equals(pair.Value, value, StringComparison.OrdinalIgnoreCase))
            {
                context = pair.Key;
                return true;
            }
        }

        context = default;
        return false;
    }
}
