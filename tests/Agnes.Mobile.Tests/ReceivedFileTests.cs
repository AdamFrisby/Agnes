using Agnes.Abstractions;
using Agnes.App.Mobile.Services;
using Agnes.App.Mobile.ViewModels;
using Agnes.Client;
using Agnes.Ui.Core;
using Agnes.Ui.Core.Transcript;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Mobile.Tests;

/// <summary>
/// A file an agent sent, on a phone: the naming rules that stand between an agent's string and this
/// device's filesystem, and the sheet that offers to share, save or open it.
///
/// The Android handler itself is a platform type and can't run here, so what's covered is everything up
/// to the moment it's called — which is where the decisions actually are.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class ReceivedFileTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "agnes-mobile-tests-" + Guid.NewGuid().ToString("n"));

    public ReceivedFileTests() => JsonStore.UseDirectory(_state);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_state, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    // ---- naming and type ----

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("notes/build\\log.txt", "log.txt")]
    [InlineData("  spaced.png  ", "spaced.png")]
    [InlineData(".hidden", "hidden")]
    [InlineData("", "file")]
    [InlineData("   ", "file")]
    [InlineData("//", "file")]
    public void A_name_from_an_agent_becomes_one_safe_leaf(string given, string expected)
        // The agent types this string. It must never be able to steer where the phone writes.
        => Assert.Equal(expected, ReceivedFileNaming.Sanitize(given));

    [Fact]
    public void A_very_long_name_is_shortened_but_keeps_its_extension()
    {
        var name = new string('a', 400) + ".png";

        var safe = ReceivedFileNaming.Sanitize(name);

        Assert.True(safe.Length <= 96);
        // The extension is what tells Android which app to offer — truncating that instead would leave a
        // perfectly good screenshot with nothing willing to open it.
        Assert.EndsWith(".png", safe, StringComparison.Ordinal);
    }

    [Fact]
    public void The_declared_type_wins_and_a_bare_word_does_not_count()
    {
        Assert.Equal("image/png", ReceivedFileNaming.MimeFor("shot.bin", "image/png"));
        Assert.Equal("text/plain", ReceivedFileNaming.MimeFor("notes.txt", declared: null));

        // An Intent whose type is a bare word resolves to nothing at all, which reads as "sharing is
        // broken" rather than "that type is unknown".
        Assert.Equal("application/pdf", ReceivedFileNaming.MimeFor("paper.pdf", "pdf"));
        Assert.Equal("*/*", ReceivedFileNaming.MimeFor("artifact.qqq", declared: null));
    }

    [Fact]
    public void A_charset_suffix_is_dropped_because_an_intent_type_has_no_room_for_one()
        => Assert.Equal("text/csv", ReceivedFileNaming.MimeFor("rows.csv", "text/csv; charset=utf-8"));

    // ---- the sheet ----

    [Fact]
    public async Task The_sheet_offers_exactly_what_the_device_can_do()
    {
        var shell = NewShell(new FakeHandler());
        var (session, _) = await SeededSessionAsync(shell);
        var file = Image();
        session.Items.Add(file);

        var sheet = new ReceivedFileSheetViewModel(shell, session, file);

        Assert.True(sheet.CanShare);
        Assert.True(sheet.CanSave);
        Assert.True(sheet.CanOpen);
        Assert.False(sheet.CanDoNothing);
        Assert.Equal("screenshot.png", sheet.Title);
        Assert.Contains("PNG", sheet.Subtitle);
        Assert.True(sheet.HasCaption);
    }

    [Fact]
    public async Task A_head_that_can_do_nothing_says_so_rather_than_showing_dead_buttons()
    {
        var shell = NewShell(NullReceivedFileHandler.Instance);
        var (session, _) = await SeededSessionAsync(shell);
        var file = Image();

        var sheet = new ReceivedFileSheetViewModel(shell, session, file);

        Assert.True(sheet.CanDoNothing);
        Assert.False(sheet.ShareCommand.CanExecute(null));
    }

    [Fact]
    public async Task Saving_hands_the_bytes_to_the_device_and_says_where_they_went()
    {
        var handler = new FakeHandler();
        var shell = NewShell(handler);
        var (session, _) = await SeededSessionAsync(shell);
        var file = Image();
        // The simulated host serves no file bytes; the card that showed this image already fetched them,
        // which is the same cache the sheet's download consults.
        SharedFilePreviews.Seed(session, file, [1, 2, 3, 4]);

        var sheet = new ReceivedFileSheetViewModel(shell, session, file);
        await sheet.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Save", handler.LastVerb);
        Assert.Equal("screenshot.png", handler.LastFile?.FileName);
        Assert.Equal(4, handler.LastFile?.Bytes.Length);
        Assert.Equal("Saved to Downloads", sheet.Status);
        Assert.False(sheet.StatusIsError);
    }

    [Fact]
    public async Task A_file_the_host_cannot_serve_is_reported_in_words_and_never_reaches_the_device()
    {
        var handler = new FakeHandler();
        var shell = NewShell(handler);
        var (session, _) = await SeededSessionAsync(shell);
        // Nothing seeded, and the simulated host has no file browser — the state an old host, or a
        // cleaned-up workspace, actually produces.
        var file = Image("gone.png");

        var sheet = new ReceivedFileSheetViewModel(shell, session, file);
        await sheet.ShareCommand.ExecuteAsync(null);

        Assert.Null(handler.LastVerb);
        Assert.True(sheet.StatusIsError);
        Assert.Contains("couldn't", sheet.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_device_that_fails_the_save_says_why()
    {
        var handler = new FakeHandler { Fail = "Android needs permission to write to Downloads." };
        var shell = NewShell(handler);
        var (session, _) = await SeededSessionAsync(shell);
        var file = Image();
        SharedFilePreviews.Seed(session, file, [7]);

        var sheet = new ReceivedFileSheetViewModel(shell, session, file);
        await sheet.SaveCommand.ExecuteAsync(null);

        Assert.True(sheet.StatusIsError);
        Assert.Contains("permission", sheet.Status, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the inbox section ----

    [Fact]
    public async Task Sent_to_you_is_hidden_until_a_session_has_actually_been_sent_something()
    {
        var shell = NewShell(new FakeHandler());
        var (session, _) = await SeededSessionAsync(shell);

        shell.Inbox.SharedFiles.Clear();
        Assert.False(shell.Inbox.HasSharedFiles);

        session.Items.Add(Image());

        Assert.True(shell.Inbox.HasSharedFiles);
        var row = Assert.Single(shell.Inbox.SharedFiles);
        Assert.Equal("screenshot.png", row.FileName);
        Assert.Equal("Demo session", row.SessionTitle);
    }

    [Fact]
    public async Task The_newest_file_is_first_and_the_list_is_capped()
    {
        var shell = NewShell(new FakeHandler());
        var (session, _) = await SeededSessionAsync(shell);

        for (var n = 0; n < 30; n++)
        {
            session.Items.Add(Image($"shot-{n}.png", DateTimeOffset.Now.AddMinutes(-n)));
        }

        Assert.Equal(20, shell.Inbox.SharedFiles.Count);
        Assert.Equal("shot-0.png", shell.Inbox.SharedFiles[0].FileName);
    }

    [Fact]
    public async Task Tapping_a_row_opens_its_session()
    {
        var shell = NewShell(new FakeHandler());
        var (session, entry) = await SeededSessionAsync(shell);
        session.Items.Add(Image());

        shell.Inbox.OpenSharedFileCommand.Execute(shell.Inbox.SharedFiles[0]);

        var page = Assert.IsType<SessionPageViewModel>(shell.CurrentPage);
        Assert.Same(entry, page.Entry);
    }

    // ---- fixtures ----

    private static ShellViewModel NewShell(IReceivedFileHandler handler)
        => new(new MobileConnector(), ImmediateDispatcher.Instance, new MobileSettings(), "Test device",
            receivedFiles: handler);

    /// <summary>A live session on the built-in offline host, registered with the shell's session list so
    /// the Inbox's projection can see it.</summary>
    private static async Task<(SessionViewModel Session, SessionEntry Entry)> SeededSessionAsync(ShellViewModel shell)
    {
        var link = shell.Hosts.Links.First();
        var host = await link.ConnectAsync() ?? throw new InvalidOperationException("no demo host");
        var info = await host.OpenSessionAsync("claude-code-native", "/home/you/projects/agnes");
        var view = await host.SubscribeAsync(info.SessionId);

        var session = shell.Sessions.Build(host, view, "Demo session");
        var saved = new SavedSession(link.Name, link.Url, string.Empty, info.SessionId,
            "claude-code-native", "Demo session", info.WorkingDirectory);
        var entry = shell.Sessions.Adopt(link, session, saved, open: false);
        return (session, entry);
    }

    private static SharedFileItem Image(string name = "screenshot.png", DateTimeOffset? when = null)
        => new(new FileSharedEvent(name, name, "shared/" + name, 76_000, "image/png", "The dashboard after the fix."))
        {
            Sequence = 42,
            Timestamp = when ?? DateTimeOffset.Now,
        };

    /// <summary>Stands in for the device: records what it was asked to do, and can be told to fail.</summary>
    private sealed class FakeHandler : IReceivedFileHandler
    {
        public bool CanSave => true;
        public bool CanOpen => true;
        public bool CanShare => true;

        public string? LastVerb { get; private set; }
        public ReceivedFile? LastFile { get; private set; }
        public string? Fail { get; init; }

        public Task SaveAsync(ReceivedFile file, CancellationToken cancellationToken = default)
            => Record("Save", file);

        public Task OpenAsync(ReceivedFile file, CancellationToken cancellationToken = default)
            => Record("Open", file);

        public Task ShareAsync(ReceivedFile file, CancellationToken cancellationToken = default)
            => Record("Share", file);

        private Task Record(string verb, ReceivedFile file)
        {
            if (Fail is { } message)
            {
                return Task.FromException(new IOException(message));
            }

            LastVerb = verb;
            LastFile = file;
            return Task.CompletedTask;
        }
    }
}
