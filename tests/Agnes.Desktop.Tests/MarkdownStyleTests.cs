using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.VisualTree;
using Markdown.Avalonia;

namespace Agnes.Desktop.Tests;

[Collection("Avalonia headless")]
public sealed class MarkdownStyleTests
{
    [Fact]
    public async Task Fenced_code_uses_the_shared_inset_hairline_and_theme_resources()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(MarkdownStyleTestApp));
        await session.Dispatch(() =>
        {
            var markdown = new MarkdownScrollViewer { Markdown = "```csharp\nvar answer = 42;\n```" };
            markdown.Styles.Add(new StyleInclude(new Uri("avares://Agnes.App.Desktop/"))
            {
                Source = new Uri("avares://Agnes.App.Desktop/Themes/MarkdownStyles.axaml"),
            });

            var window = new Window { Width = 600, Height = 300, Content = markdown };
            window.Show();
            window.UpdateLayout();

            var codeBlock = Assert.Single(markdown.GetVisualDescendants().OfType<Border>(),
                border => border.Classes.Contains("CodeBlock"));
            Assert.Equal(new Thickness(10, 6), codeBlock.Padding);
            Assert.Equal(new Thickness(1), codeBlock.BorderThickness);

            var app = Assert.IsType<Agnes.App.Desktop.App>(Application.Current);
            Assert.True(app.TryGetResource("CodeBg", codeBlock.ActualThemeVariant, out var codeBackground));
            Assert.True(app.TryGetResource("Line", codeBlock.ActualThemeVariant, out var line));
            Assert.Same(codeBackground, codeBlock.Background);
            Assert.Same(line, codeBlock.BorderBrush);

            window.Close();
        }, CancellationToken.None);
    }
}

public static class MarkdownStyleTestApp
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<Agnes.App.Desktop.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia();
}
