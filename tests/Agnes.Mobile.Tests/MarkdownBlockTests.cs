using Agnes.App.Mobile.Controls;
using Agnes.App.Mobile.Preview;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;

namespace Agnes.Mobile.Tests;

public sealed class MarkdownBlockTests
{
    [Fact]
    public void Markdown_fence_renders_first_and_keeps_its_source_mode_while_streaming()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(PreviewAppBuilder));
        Dispatch(session, () =>
        {
            var block = new MarkdownBlock { Markdown = "```markdown\n# Preview\n```" };
            var toggle = Toggle(block);
            Assert.Equal("Code", toggle.Content);

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Render", toggle.Content);
            Assert.Contains(
                Descendants(block).OfType<SelectableTextBlock>(),
                text => text.Classes.Contains("markdownFenceSource") && text.Text == "# Preview\n");

            block.Markdown = "```markdown\n# Preview\n\nMore text\n```";
            Assert.Equal("Render", Toggle(block).Content);
            Assert.Contains(
                Descendants(block).OfType<SelectableTextBlock>(),
                text => text.Classes.Contains("markdownFenceSource") && text.Text == "# Preview\n\nMore text\n");
        });
    }

    [Fact]
    public void Ordinary_code_fences_keep_the_standard_renderer()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(PreviewAppBuilder));
        Dispatch(session, () =>
        {
            var block = new MarkdownBlock { Markdown = "```csharp\nvar answer = 42;\n```" };
            Assert.DoesNotContain(
                Descendants(block).OfType<Button>(),
                button => button.Classes.Contains("markdownFenceToggle"));
        });
    }

    [Fact]
    public void Each_markdown_fence_has_its_own_mode()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(PreviewAppBuilder));
        Dispatch(session, () =>
        {
            var block = new MarkdownBlock
            {
                Markdown = "```markdown\n# One\n```\n\n```md\n# Two\n```",
            };
            var toggles = Descendants(block)
                .OfType<Button>()
                .Where(button => button.Classes.Contains("markdownFenceToggle"))
                .ToArray();
            Assert.Equal(2, toggles.Length);

            toggles[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Single(toggles, toggle => Equals(toggle.Content, "Render"));
            Assert.Single(toggles, toggle => Equals(toggle.Content, "Code"));
        });
    }

    [Theory]
    [InlineData("~~~md\n## Tilde\n~~~", "## Tilde\n")]
    [InlineData("```markdown\n## Still streaming", "## Still streaming")]
    public void Alternate_and_streaming_markdown_fences_use_the_toggle(string markdown, string source)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(PreviewAppBuilder));
        Dispatch(session, () =>
        {
            var block = new MarkdownBlock { Markdown = markdown };
            var toggle = Toggle(block);
            Assert.Equal("Code", toggle.Content);

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var sourceTexts = Descendants(block)
                .OfType<SelectableTextBlock>()
                .Where(text => text.Classes.Contains("markdownFenceSource"))
                .Select(text => text.Text)
                .ToArray();
            Assert.Contains(source, sourceTexts);
        });
    }

    private static Button Toggle(MarkdownBlock block)
        => Assert.Single(
            Descendants(block).OfType<Button>(),
            button => button.Classes.Contains("markdownFenceToggle"));

    private static void Dispatch(HeadlessUnitTestSession session, Action action)
        => session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();

    private static IEnumerable<Control> Descendants(Control root)
    {
        var seen = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<Control>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            IEnumerable<Control> children = current switch
            {
                Panel panel => panel.Children,
                Decorator { Child: { } child } => [child],
                ContentControl { Content: Control child } => [child],
                _ => [],
            };
            foreach (var child in children)
            {
                if (seen.Add(child))
                {
                    yield return child;
                    pending.Push(child);
                }
            }
        }
    }
}
