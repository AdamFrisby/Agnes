using Agnes.App.Desktop.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace Agnes.Desktop.Tests;

public sealed class MarkdownViewerTests
{
    [Fact]
    public void Standalone_code_fence_has_no_second_surface()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopMarkdownAppBuilder));
        Dispatch(session, () =>
        {
            var viewer = new MarkdownMessageViewer { Markdown = "```csharp\nConsole.WriteLine();\n```" };
            var window = new Window { Content = viewer };
            window.Show();

            var codeBlock = Assert.Single(
                Descendants(viewer).OfType<Border>(),
                border => border.Classes.Contains("CodeBlock"));
            var editor = Assert.Single(Descendants(viewer).OfType<AvaloniaEdit.TextEditor>());
            var toolbar = Assert.Single(
                Descendants(viewer).OfType<Grid>(),
                grid => grid.Classes.Contains("markdownMessageToolbar"));
            Assert.Equal(new Thickness(0), codeBlock.BorderThickness);
            Assert.Equal(new Thickness(0), codeBlock.Margin);
            Assert.Equal(0, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(codeBlock.Background).Color.A);
            Assert.Equal(codeBlock.Background?.ToString(), editor.Background?.ToString());
            Assert.False(toolbar.IsVisible);
            window.Close();
        });
    }

    [Fact]
    public void Code_fence_embedded_in_markdown_keeps_its_code_surface()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopMarkdownAppBuilder));
        Dispatch(session, () =>
        {
            var viewer = new MarkdownMessageViewer
            {
                Markdown = "# Example Markdown\n\nBefore\n\n```csharp\nConsole.WriteLine();\n```\n\nAfter",
            };
            var window = new Window { Content = viewer };
            window.Show();

            var renderer = Assert.Single(
                Descendants(viewer).OfType<MarkdownViewer>(),
                markdown => markdown.Name == "RenderedMarkdown");
            var codeBlock = Assert.Single(
                Descendants(viewer).OfType<Border>(),
                border => border.Classes.Contains("CodeBlock"));
            var editor = Assert.Single(Descendants(viewer).OfType<AvaloniaEdit.TextEditor>());
            var toolbar = Assert.Single(
                Descendants(viewer).OfType<Grid>(),
                grid => grid.Classes.Contains("markdownMessageToolbar"));

            Assert.DoesNotContain("standaloneCodeMessage", renderer.Classes);
            Assert.True(toolbar.IsVisible);
            Assert.Equal(new Thickness(1), codeBlock.BorderThickness);
            Assert.NotEqual(0, Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(codeBlock.Background).Color.A);
            Assert.Equal(codeBlock.Background?.ToString(), editor.Background?.ToString());
            window.Close();
        });
    }

    [Fact]
    public void Streaming_change_to_standalone_code_hides_toggle_and_returns_to_rendered_mode()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopMarkdownAppBuilder));
        Dispatch(session, () =>
        {
            var viewer = new MarkdownMessageViewer { Markdown = "# Rich Markdown" };
            var toggle = Assert.Single(
                Descendants(viewer).OfType<Button>(),
                button => button.Classes.Contains("markdownMessageToggle"));
            var toolbar = Assert.Single(
                Descendants(viewer).OfType<Grid>(),
                grid => grid.Classes.Contains("markdownMessageToolbar"));
            var renderer = Assert.Single(
                Descendants(viewer).OfType<MarkdownViewer>(),
                markdown => markdown.Name == "RenderedMarkdown");

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(renderer.IsVisible);

            viewer.Markdown = "```bash\ndotnet test\n```";

            Assert.False(toolbar.IsVisible);
            Assert.True(renderer.IsVisible);
            Assert.Equal("Code", toggle.Content);
        });
    }

    [Fact]
    public void Code_fence_gets_a_background_only_inside_explicit_markdown_render()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopMarkdownAppBuilder));
        Dispatch(session, () =>
        {
            var viewer = new MarkdownViewer
            {
                Markdown = "~~~~markdown\n# Rendered\n\n```bash\ndotnet test\n```\n~~~~",
                SelectionEnabled = true,
            };
            var window = new Window { Content = viewer };
            window.Show();

            var markdownRender = Assert.Single(
                Descendants(viewer).OfType<Border>(),
                border => border.Classes.Contains("markdownFence"));
            var nestedCode = Assert.Single(
                Descendants(markdownRender).OfType<Border>(),
                border => border.Classes.Contains("CodeBlock"));
            var nestedEditor = Assert.Single(Descendants(nestedCode).OfType<AvaloniaEdit.TextEditor>());
            var nestedColor = Assert.IsAssignableFrom<Avalonia.Media.ISolidColorBrush>(nestedCode.Background).Color;

            Assert.NotEqual(0, nestedColor.A);
            Assert.Equal(new Thickness(1), nestedCode.BorderThickness);
            Assert.Equal(new Thickness(0, 4), nestedCode.Margin);
            Assert.Equal(nestedCode.Background?.ToString(), nestedEditor.Background?.ToString());
            window.Close();
        });
    }

    [Fact]
    public void Assistant_message_wraps_normal_markdown_and_toggles_its_exact_source()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopMarkdownAppBuilder));
        Dispatch(session, () =>
        {
            const string source = "# Project Aurora\n\n| Area | Status |\n| --- | --- |\n| API | Complete |";
            var viewer = new MarkdownMessageViewer { Markdown = source };
            var toggle = Assert.Single(
                Descendants(viewer).OfType<Button>(),
                button => button.Classes.Contains("markdownMessageToggle"));

            Assert.Equal("Code", toggle.Content);
            Assert.True(Assert.Single(
                Descendants(viewer).OfType<MarkdownViewer>(),
                markdown => markdown.Name == "RenderedMarkdown").IsVisible);

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal("Render", toggle.Content);
            Assert.Contains(
                Descendants(viewer).OfType<SelectableTextBlock>(),
                text => text.Classes.Contains("markdownMessageSource") && text.Text == source);

            viewer.Markdown = source + "\n| Desktop | In progress |";

            Assert.Equal("Render", toggle.Content);
            Assert.Contains(
                Descendants(viewer).OfType<SelectableTextBlock>(),
                text => text.Classes.Contains("markdownMessageSource")
                    && text.Text == source + "\n| Desktop | In progress |");
        });
    }

    [Fact]
    public void Markdown_fence_has_an_independent_source_preview_toggle()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(DesktopMarkdownAppBuilder));
        Dispatch(session, () =>
        {
            var viewer = new MarkdownViewer
            {
                Markdown = "```md\n# Rendered\n```\n\n```\nordinary code\n```",
                SelectionEnabled = true,
            };
            var toggle = Assert.Single(
                Descendants(viewer).OfType<Button>(),
                button => button.Classes.Contains("markdownFenceToggle"));
            Assert.Equal("Code", toggle.Content);

            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("Render", toggle.Content);
            Assert.Contains(
                Descendants(viewer).OfType<SelectableTextBlock>(),
                text => text.Classes.Contains("markdownFenceSource") && text.Text == "# Rendered\n");

            viewer.Markdown = "```md\n# Rendered\n\nStill streaming\n```\n\n```\nordinary code\n```";

            var replacementToggle = Assert.Single(
                Descendants(viewer).OfType<Button>(),
                button => button.Classes.Contains("markdownFenceToggle"));
            Assert.Equal("Render", replacementToggle.Content);
            Assert.Contains(
                Descendants(viewer).OfType<SelectableTextBlock>(),
                text => text.Classes.Contains("markdownFenceSource")
                    && text.Text == "# Rendered\n\nStill streaming\n");
        });
    }

    private static void Dispatch(HeadlessUnitTestSession session, Action action)
        => session.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();

    private static IEnumerable<Control> Descendants(Control root)
    {
        var seen = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<Control>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            var children = current.GetVisualChildren()
                .Concat(current.GetLogicalChildren())
                .OfType<Control>();
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

public static class DesktopMarkdownAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Agnes.App.Desktop.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
