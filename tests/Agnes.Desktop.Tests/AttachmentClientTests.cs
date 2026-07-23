using Agnes.Abstractions.Events;
using Agnes.Client;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Desktop.Tests;

/// <summary>Client-side attach: a picked file's bytes go to the host's UploadAttachment and come back as a
/// reference chip (git-and-files/03) — the agent gets a path, never inline binary.</summary>
public class AttachmentClientTests
{
    private static SessionView Live(string id = "s1")
    {
        var view = new SessionView(id);
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo(id, "opencode", string.Empty, 0), [], 0));
        return view;
    }

    [Fact]
    public async Task Attaching_a_file_uploads_the_bytes_and_adds_a_reference_chip()
    {
        var host = new FakeHost();
        var vm = new SessionViewModel(host, Live(), ImmediateDispatcher.Instance, "OpenCode", eventBus: new EventBus());

        await vm.AttachFileAsync("shot.png", [1, 2, 3]);

        var upload = Assert.Single(host.Uploads);
        Assert.Equal("shot.png", upload.FileName);
        Assert.Equal(3, upload.Bytes);
        Assert.True(vm.HasAttachments);
        Assert.Single(vm.Attachments);
    }
}
