using Avalonia.Input;
using System.Collections.Immutable;

namespace Agnes.App.Desktop.Keymaps;

public enum KeymapContext
{
    Window,
    PaletteFocus,
    AllSessionsSearchFocus,
    SettingsMcpSearchFocus,
    SettingsPluginSearchFocus,
    SettingsSkillSearchFocus,
    SettingsLaunchProfileRenameFocus,
    Session,
    SessionRenameFocus,
    SessionTagFocus,
    SessionFindFocus,
    SessionReferenceFocus,
    ComposerFocus,
}

public enum AgnesCommand
{
    TabNew,
    TabClose,
    TabNext,
    TabPrevious,
    TabPosition1,
    TabPosition2,
    TabPosition3,
    TabPosition4,
    TabPosition5,
    TabPosition6,
    TabPosition7,
    TabPosition8,
    TabPosition9,
    PaletteOpen,
    PaletteNext,
    PalettePrevious,
    PaletteRun,
    PaletteClose,
    DashboardOpen,
    AllSessionsSearch,
    SettingsMcpSearch,
    SettingsPluginSearch,
    SettingsSkillSearch,
    LaunchProfileRenameCommit,
    LaunchProfileRenameCancel,
    SessionRenameCommit,
    SessionRenameCancel,
    SessionTagAdd,
    SessionFindOpen,
    SessionFindClose,
    SessionFindNext,
    SessionFindPrevious,
    SessionPromptNext,
    SessionPromptPrevious,
    SessionChangeNext,
    SessionChangePrevious,
    SessionReferenceAdd,
    ComposerSend,
    ComposerSendNow,
    ComposerRecallPrevious,
    ComposerRecallNext,
}

public sealed record KeymapRule(KeyGesture Gesture, AgnesCommand? Command, KeymapContext Context);

public sealed record KeymapDiagnostic(string Message, int? Line = null)
{
    public override string ToString() => Line is { } line ? $"Line {line}: {Message}" : Message;
}

public sealed record EffectiveKeymap(ImmutableArray<KeymapRule> Rules)
{
    public IReadOnlyList<KeymapRule> For(KeymapContext context) => Rules.Where(r => r.Context == context).ToArray();

    public KeyGesture? PrimaryGesture(AgnesCommand command, KeymapContext? context = null)
        => Rules.LastOrDefault(r => r.Command == command && (context is null || r.Context == context))?.Gesture;
}
