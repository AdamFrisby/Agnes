using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>Unified-diff classification, against a fragment captured from the live orchestrator.</summary>
public sealed class DiffViewTests
{
    private const string Captured =
        "diff --git a/src/CodeyBox.Agents.Claude.AcpBridge/Bridge.cs b/src/CodeyBox.Agents.Claude.AcpBridge/Bridge.cs\n" +
        "index a1dea646..fb2f7f64 100644\n" +
        "--- a/src/CodeyBox.Agents.Claude.AcpBridge/Bridge.cs\n" +
        "+++ b/src/CodeyBox.Agents.Claude.AcpBridge/Bridge.cs\n" +
        "@@ -124,15 +124,28 @@ internal sealed class Bridge : IAsyncDisposable\n" +
        "         // CoreCLR remembered an ignored startup disposition\n" +
        "-        var old = true;\n" +
        "+        var replacement = false;\n" +
        "+        var added = 1;\n";

    [Fact]
    public void File_headers_are_not_mistaken_for_additions_and_removals()
    {
        // '+++' and '---' start with the same characters as a changed line. Testing the header first is
        // the difference between a readable diff and one where every file boundary is painted as a change.
        var lines = UnifiedDiff.Parse(Captured);

        Assert.Equal(DiffLineKind.File, lines[0].Kind);   // diff --git
        Assert.Equal(DiffLineKind.File, lines[1].Kind);   // index
        Assert.Equal(DiffLineKind.File, lines[2].Kind);   // ---
        Assert.Equal(DiffLineKind.File, lines[3].Kind);   // +++
        Assert.Equal(DiffLineKind.Hunk, lines[4].Kind);
        Assert.Equal(DiffLineKind.Context, lines[5].Kind);
        Assert.Equal(DiffLineKind.Removed, lines[6].Kind);
        Assert.Equal(DiffLineKind.Added, lines[7].Kind);
    }

    [Fact]
    public void Summary_counts_files_and_changed_lines()
        => Assert.Equal("1 file  ·  +2  −1", UnifiedDiff.Summarise(UnifiedDiff.Parse(Captured)));

    [Fact]
    public void An_empty_diff_is_empty_not_a_single_blank_line()
    {
        Assert.Empty(UnifiedDiff.Parse(string.Empty));
        Assert.Equal(string.Empty, UnifiedDiff.Summarise([]));
    }

    [Fact]
    public void Enormous_diffs_are_capped()
    {
        var huge = string.Join('\n', Enumerable.Repeat("+line", 5_000));

        Assert.Equal(2_000, UnifiedDiff.Parse(huge).Count);
        Assert.Equal(10, UnifiedDiff.Parse(huge, maxLines: 10).Count);
    }

    [Fact]
    public void Carriage_returns_do_not_leak_into_the_rendered_text()
        => Assert.Equal("+added", UnifiedDiff.Parse("+added\r\n")[0].Text);
}
