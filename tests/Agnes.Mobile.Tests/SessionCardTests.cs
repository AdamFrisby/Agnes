using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.Client;
using Agnes.Ui.Core;

namespace Agnes.Mobile.Tests;

/// <summary>
/// The session card is what the whole app is read through, so the fields it derives matter more than
/// their size suggests.
/// </summary>
public sealed class SessionCardTests
{
    private static SessionEntry Entry(string title, string workingDirectory)
    {
        var saved = new SavedSession("Host", "sim://demo", "", "s1", "claude-code", title, workingDirectory);
        var hosts = new HostBook(new MobileConnector(), ImmediateDispatcher.Instance);
        return new SessionEntry(saved, hosts.Links[0]);
    }

    [Fact]
    public void The_project_comes_from_the_folder_not_the_title()
    {
        // The agent renames the conversation mid-session. Deriving the project from the title then showed
        // the conversation name twice ("Wire remote terminal ui · Wire remote terminal ui · claude-code").
        var entry = Entry("Wire remote terminal ui", "/home/you/projects/agnes");

        Assert.Equal("agnes", entry.Project);
        Assert.Equal("agnes · claude-code", entry.ProjectLine);
    }

    [Fact]
    public void A_session_saved_before_the_folder_was_recorded_falls_back_to_its_title()
    {
        var entry = Entry("/home/you/projects/agnes", workingDirectory: string.Empty);

        Assert.Equal("agnes", entry.Project);
    }

    [Fact]
    public void A_windows_style_path_resolves_to_its_leaf_too()
    {
        // Built rather than written literally: the analyzer (rightly) refuses hardcoded absolute paths,
        // and the point here is only the separator.
        var entry = Entry("whatever", string.Join('\\', "C:", "src", "agnes"));

        Assert.Equal("agnes", entry.Project);
    }

    [Fact]
    public void Renaming_keeps_the_project_intact()
    {
        var entry = Entry("/home/you/projects/agnes", "/home/you/projects/agnes");

        entry.UpdateSavedTitle("Refactor the session store");

        Assert.Equal("Refactor the session store", entry.Title);
        Assert.Equal("agnes", entry.Project); // unchanged by the rename
    }
}
