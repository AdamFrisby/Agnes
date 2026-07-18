using Agnes.Abstractions;
using Agnes.Client;
using Agnes.Client.Simulation;
using Agnes.Protocol;
using Agnes.Ui.Core;
using Agnes.Ui.Core.Diff;
using Agnes.Ui.Core.Transcript;
using Agnes.Ui.Core.ViewModels;

namespace Agnes.Desktop.Tests;

public class DiffParserTests
{
    [Fact]
    public void Parses_headers_hunks_and_line_kinds_with_numbers()
    {
        const string diff = "--- a/f.ts\n+++ b/f.ts\n@@ -5,3 +5,4 @@\n ctx\n-old\n+new1\n+new2\n";
        var lines = DiffParser.Parse(diff);

        Assert.Contains(lines, l => l.Kind == DiffLineKind.FileHeader);
        Assert.Contains(lines, l => l.Kind == DiffLineKind.Hunk);

        var added = lines.Where(l => l.IsAdded).ToList();
        Assert.Equal(["new1", "new2"], added.Select(l => l.Text));
        Assert.Equal(6, added[0].NewLine); // new file starts at 5: ctx=5, new1=6

        var removed = Assert.Single(lines, l => l.IsRemoved);
        Assert.Equal("old", removed.Text);

        var context = lines.First(l => l.Kind == DiffLineKind.Context);
        Assert.Equal(5, context.OldLine);
        Assert.Equal(5, context.NewLine);
    }

    [Fact]
    public void LooksLikeDiff_recognises_unified_diffs()
    {
        Assert.True(DiffParser.LooksLikeDiff("--- a\n+++ b\n@@ -1 +1 @@"));
        Assert.False(DiffParser.LooksLikeDiff("just a normal answer"));
    }
}

public class PreviewAndListTests
{
    private static SessionViewModel Build(out SessionView view)
    {
        view = new SessionView("s1");
        view.ApplySnapshot(new SessionSnapshot(new SessionInfo("s1", "opencode", string.Empty, 0), [], 0));
        return new SessionViewModel(new SimulatedHost(), view, ImmediateDispatcher.Instance, "OpenCode");
    }

    private static SessionEvent Seq(SessionEvent e, long n) => e with { Sequence = n };

    [Fact]
    public void Non_file_tools_go_to_tools_run_list()
    {
        var vm = Build(out var view);
        view.Apply(Seq(new ToolCallEvent("t1", "config", ToolKind.Search, ToolCallStatus.Completed, []), 1));

        Assert.True(vm.HasTools);
        Assert.Empty(vm.ModifiedFiles);
        Assert.Single(vm.ToolActivity);
    }

    [Fact]
    public void File_tool_preview_is_parsed_as_a_diff()
    {
        var vm = Build(out var view);
        view.Apply(Seq(new ToolCallEvent("t1", "f.ts", ToolKind.Edit, ToolCallStatus.Completed,
            [new TextContent("--- a/f.ts\n+++ b/f.ts\n@@ -1 +1 @@\n-x\n+y")]), 1));

        vm.ShowFilePreviewCommand.Execute(vm.ModifiedFiles[0]);

        Assert.True(vm.ShowRightPanel);
        Assert.True(vm.SelectedPreview!.IsDiff);
        Assert.NotNull(vm.SelectedPreview.Diff);
    }

    [Fact]
    public void Long_assistant_message_is_condensed_and_previewable()
    {
        var vm = Build(out var view);
        var text = string.Concat(Enumerable.Repeat("word ", 120)); // > 360 chars
        view.Apply(Seq(new MessageChunkEvent(MessageRole.Assistant, new TextContent(text)), 1));

        var message = vm.Items.OfType<MessageBubbleItem>().Single();
        Assert.True(message.IsLong);
        Assert.True(message.CondensedText.Length < text.Length);

        vm.ShowMessagePreviewCommand.Execute(message);
        Assert.True(vm.ShowRightPanel);
        Assert.True(vm.SelectedPreview!.IsText);
    }
}
