using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.Transcript;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Ui.Core.Tests;

/// <summary>
/// A file the agent sends: the card the transcript builds for it, the session-level list, and the rule that
/// replaying a log is not the same as receiving something — the mistake that once re-answered every
/// permission in a session's history is the same mistake as re-announcing every file in it.
/// </summary>
public class SharedFileTests
{
    private static readonly byte[] Bytes = [1, 2, 3, 4, 5, 6, 7, 8];

    private static FileSharedEvent Shared(string id = "f1", string name = "shot.png", string? caption = "before vs after")
        => new(id, name, $".agnes/shared/{id}/{name}", Bytes.Length, "image/png", caption);

    // ---- the transcript card ----

    [Fact]
    public void A_shared_file_becomes_a_card_carrying_the_events_fields_and_its_place_in_the_log()
    {
        var t = new TranscriptBuilder();
        t.Apply(new MessageChunkEvent(MessageRole.Assistant, new TextContent("here you go")) { Sequence = 6 });
        t.Apply(Shared() with { Sequence = 7 });

        var card = Assert.IsType<SharedFileItem>(t.Items[1]);
        Assert.Equal("f1", card.FileId);
        Assert.Equal("shot.png", card.FileName);
        Assert.Equal(".agnes/shared/f1/shot.png", card.RelativePath);
        Assert.Equal(Bytes.Length, card.Size);
        Assert.Equal("image/png", card.MimeType);
        Assert.Equal("before vs after", card.Caption);
        Assert.True(card.IsImage);
        Assert.Equal("PNG", card.Extension);
        // The sequence is what a shared link addresses, so the builder must stamp it like any other item.
        Assert.Equal(7, card.Sequence);
    }

    [Fact]
    public void The_card_closes_the_open_bubble_rather_than_landing_inside_it()
    {
        var t = new TranscriptBuilder();
        t.Apply(new MessageChunkEvent(MessageRole.Assistant, new TextContent("one")));
        t.Apply(Shared());
        t.Apply(new MessageChunkEvent(MessageRole.Assistant, new TextContent("two")));

        Assert.Equal(3, t.Items.Count);
        Assert.Equal("one", ((MessageBubbleItem)t.Items[0]).Text);
        Assert.IsType<SharedFileItem>(t.Items[1]);
        Assert.Equal("two", ((MessageBubbleItem)t.Items[2]).Text);
    }

    // ---- the session-level list ----

    [Fact]
    public void Replayed_history_populates_the_list_but_announces_nothing()
    {
        var received = new List<SharedFileItem>();
        var notified = new List<AppNotification>();
        var vm = Session(new FakeFileHost(), history: [Shared("f1"), Shared("f2", "log.txt")], out _,
            onReceived: received.Add, onNotified: notified.Add);

        Assert.Equal(2, vm.SharedFiles.Count);
        Assert.Equal(["f1", "f2"], vm.SharedFiles.Select(f => f.FileId));
        Assert.True(vm.HasSharedFiles);
        // Reconnecting to a session that received a file yesterday must not ring the doorbell again.
        Assert.Empty(received);
        Assert.Empty(notified);
    }

    [Fact]
    public void A_file_arriving_live_is_appended_announced_and_raised_once()
    {
        var received = new List<SharedFileItem>();
        var notified = new List<AppNotification>();
        var vm = Session(new FakeFileHost(), history: [], out var view,
            onReceived: received.Add, onNotified: notified.Add);

        view.Apply(Shared("f3", "chart.png", "the p95 after the fix") with { Sequence = 40 });

        var card = Assert.Single(vm.SharedFiles);
        Assert.Equal("f3", card.FileId);
        Assert.Same(card, Assert.Single(received));
        // The list holds the very item the transcript rendered, so a jump from either surface lands together.
        Assert.Same(card, vm.Items.OfType<SharedFileItem>().Single());

        var note = Assert.Single(notified);
        Assert.Equal(NotificationKind.File, note.Kind);
        Assert.Contains("sent you a file", note.Title, StringComparison.Ordinal);
        Assert.Contains("chart.png", note.Body, StringComparison.Ordinal);
        Assert.Contains("the p95 after the fix", note.Body, StringComparison.Ordinal);
        Assert.Equal(card.AnchorId, note.AnchorId);
    }

    [Fact]
    public void Without_a_caption_the_notification_body_is_just_the_file_name()
    {
        var notified = new List<AppNotification>();
        var vm = Session(new FakeFileHost(), history: [], out var view, onNotified: notified.Add);

        view.Apply(Shared("f4", "build.zip", caption: null) with { Sequence = 12 });

        Assert.Equal("build.zip", Assert.Single(notified).Body);
        Assert.Single(vm.SharedFiles);
    }

    // ---- downloading and previewing ----

    [Fact]
    public async Task Downloading_asks_the_host_for_the_cards_own_path_and_keeps_its_name_and_type()
    {
        var host = new FakeFileHost();
        var vm = Session(host, history: [Shared()], out _);

        var file = await vm.DownloadSharedFileAsync(vm.SharedFiles[0]);

        Assert.Equal(("s1", ".agnes/shared/f1/shot.png"), host.LastDownload);
        Assert.Equal("shot.png", file.FileName);
        Assert.Equal("image/png", file.MimeType);
        Assert.Equal(Bytes, file.Bytes);
    }

    [Fact]
    public async Task A_preview_that_the_host_cannot_serve_is_null_rather_than_an_exception()
    {
        var host = new FakeFileHost { FailReads = true };
        var vm = Session(host, history: [Shared()], out _);

        Assert.Null(await vm.PreviewSharedFileAsync(vm.SharedFiles[0]));
    }

    // ---- the three verbs ----

    [Fact]
    public async Task Each_command_hands_the_downloaded_bytes_to_the_head_s_handler()
    {
        var handler = new RecordingHandler { CanSave = true, CanOpen = true, CanShare = true };
        var vm = Session(new FakeFileHost(), history: [Shared()], out _, handler);
        var card = vm.SharedFiles[0];

        await vm.SaveSharedFileCommand.ExecuteAsync(card);
        await vm.OpenSharedFileCommand.ExecuteAsync(card);
        await vm.ShareSharedFileCommand.ExecuteAsync(card);

        Assert.Equal(["save", "open", "share"], handler.Calls.Select(c => c.Verb));
        Assert.All(handler.Calls, c =>
        {
            Assert.Equal("shot.png", c.File.FileName);
            Assert.Equal(Bytes, c.File.Bytes);
        });
        Assert.Same(handler, vm.ReceivedFiles);
    }

    [Fact]
    public void A_verb_the_head_cannot_do_is_not_executable_so_no_dead_button_renders()
    {
        var vm = Session(new FakeFileHost(), history: [Shared()], out _,
            new RecordingHandler { CanSave = true, CanOpen = false, CanShare = false });
        var card = vm.SharedFiles[0];

        Assert.True(vm.SaveSharedFileCommand.CanExecute(card));
        Assert.False(vm.OpenSharedFileCommand.CanExecute(card));
        Assert.False(vm.ShareSharedFileCommand.CanExecute(card));
    }

    [Fact]
    public void A_head_that_wired_no_handler_gets_the_null_one_and_offers_nothing()
    {
        var vm = Session(new FakeFileHost(), history: [Shared()], out _);

        Assert.Same(NullReceivedFileHandler.Instance, vm.ReceivedFiles);
        Assert.False(vm.SaveSharedFileCommand.CanExecute(vm.SharedFiles[0]));
    }

    [Fact]
    public async Task A_failed_download_lands_in_the_transcript_as_an_error_notice_not_an_exception()
    {
        var handler = new RecordingHandler { CanSave = true };
        var vm = Session(new FakeFileHost { FailDownloads = true }, history: [Shared()], out _, handler);

        await vm.SaveSharedFileCommand.ExecuteAsync(vm.SharedFiles[0]);

        var notice = Assert.Single(vm.Items.OfType<NoticeItem>());
        Assert.True(notice.IsError);
        Assert.Contains("shot.png", notice.Text, StringComparison.Ordinal);
        Assert.Empty(handler.Calls);
    }

    // ---- helpers ----

    private static SessionViewModel Session(
        IAgnesHost host,
        IReadOnlyList<FileSharedEvent> history,
        out SessionView view,
        IReceivedFileHandler? handler = null,
        Action<SharedFileItem>? onReceived = null,
        Action<AppNotification>? onNotified = null)
    {
        view = new SessionView("s1");
        var events = history.Select((e, i) => (SessionEvent)(e with { Sequence = i + 1 })).ToArray();
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo("s1", "opencode", string.Empty, 0), events, events.Length));

        var vm = new SessionViewModel(host, view, ImmediateDispatcher.Instance, "OpenCode", receivedFiles: handler);
        if (onReceived is not null)
        {
            vm.SharedFileReceived += onReceived;
        }

        if (onNotified is not null)
        {
            vm.NotificationRaised += onNotified;
        }

        return vm;
    }

    // Re-lists IAgnesHost on purpose: the two file calls are *default* interface members, so a derived class
    // that only declares matching methods does not take over the mapping — the default still runs and the
    // fake silently hands back nothing. Re-implementing the interface here is what binds them.
    private sealed class FakeFileHost : StubAgnesHost, IAgnesHost
    {
        public bool FailReads { get; init; }
        public bool FailDownloads { get; init; }
        public (string Session, string Path)? LastDownload { get; private set; }

        public Task<byte[]> DownloadFileAsync(string sessionId, string relativePath)
        {
            LastDownload = (sessionId, relativePath);
            return FailDownloads
                ? Task.FromException<byte[]>(new IOException("the host lost the file"))
                : Task.FromResult(Bytes);
        }

        public Task<FileContent> ReadFileAsync(string sessionId, string relativePath)
            => FailReads
                ? Task.FromException<FileContent>(new FileNotFoundException(relativePath))
                : Task.FromResult(new FileContent(relativePath, FileContentKind.Image, null, Bytes, "image/png", Bytes.Length));
    }

    private sealed record HandlerCall(string Verb, ReceivedFile File);

    private sealed class RecordingHandler : IReceivedFileHandler
    {
        public List<HandlerCall> Calls { get; } = [];

        public bool CanSave { get; init; }
        public bool CanOpen { get; init; }
        public bool CanShare { get; init; }

        public Task SaveAsync(ReceivedFile file, CancellationToken cancellationToken = default) => Record("save", file);
        public Task OpenAsync(ReceivedFile file, CancellationToken cancellationToken = default) => Record("open", file);
        public Task ShareAsync(ReceivedFile file, CancellationToken cancellationToken = default) => Record("share", file);

        private Task Record(string verb, ReceivedFile file)
        {
            Calls.Add(new HandlerCall(verb, file));
            return Task.CompletedTask;
        }
    }
}
