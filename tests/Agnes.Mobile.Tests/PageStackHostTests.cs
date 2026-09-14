using System.Collections.ObjectModel;
using Agnes.App.Mobile.Controls;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Agnes.Mobile.Tests;

/// <summary>
/// The page stack keeps every page built and attached behind the one on top: pushing hides the page
/// underneath, popping shows the same view again, and a page that leaves the stack leaves the host.
/// </summary>
[Collection(AvaloniaCollection.Name)]
public sealed class PageStackHostTests(AvaloniaSession avalonia)
{
    private sealed record Page(string Name);

    private static PageStackHost Host(ObservableCollection<object> stack)
    {
        var host = new PageStackHost();
        host.DataTemplates.Add(new FuncDataTemplate<Page>((page, _) => new Border { Tag = page.Name }, supportsRecycling: false));
        host.Pages = stack;
        return host;
    }

    private static Border? Shown(PageStackHost host) => host.Children.OfType<Border>().FirstOrDefault(b => b.IsVisible);

    [Fact]
    public async Task Pushing_keeps_the_page_beneath_built_and_popping_shows_the_same_view()
    {
        await avalonia.Run(() =>
        {
            var stack = new ObservableCollection<object>();
            var host = Host(stack);

            var session = new Page("session");
            stack.Add(session);
            host.Current = session;
            var sessionView = Shown(host);
            Assert.Equal("session", sessionView!.Tag);

            var detail = new Page("detail");
            stack.Add(detail);
            host.Current = detail;
            Assert.Equal("detail", Shown(host)!.Tag);
            Assert.Equal(2, host.Children.Count);
            Assert.False(sessionView.IsVisible);

            stack.Remove(detail);
            host.Current = session;
            Assert.Same(sessionView, Shown(host));
            Assert.Single(host.Children);
            Assert.Equal([session], host.Built);
        });
    }

    [Fact]
    public async Task No_current_page_shows_nothing_and_a_cleared_stack_empties_the_host()
    {
        await avalonia.Run(() =>
        {
            var stack = new ObservableCollection<object>();
            var host = Host(stack);
            var a = new Page("a");
            var b = new Page("b");
            stack.Add(a);
            host.Current = a;
            stack.Add(b);
            host.Current = b;
            Assert.Equal(2, host.Children.Count);

            host.Current = null;
            Assert.Null(Shown(host));

            stack.Clear();
            Assert.Empty(host.Children);
        });
    }
}
