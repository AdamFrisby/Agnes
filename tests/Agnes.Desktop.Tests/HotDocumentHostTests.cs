using System.Collections.ObjectModel;
using Agnes.App.Desktop;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;

namespace Agnes.Desktop.Tests;

/// <summary>
/// The document dock's bounded keep-alive host: the most recently activated views stay attached and
/// hidden, a switch among them is a visibility flip, the least recently used is detached past capacity,
/// and a closed document leaves. The contract that turned a 1.5 s tab switch into a 5 ms one.
/// </summary>
public class HotDocumentHostTests
{
    private sealed class TestApp : Application
    {
        public override void Initialize() => Styles.Add(new FluentTheme());
    }

    public static class TestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia();
    }

    private static (HotDocumentHost host, ObservableCollection<IDockable> docs, PerItemControlRecycling recycler, Dictionary<IDockable, Control> views) Build(int count, int capacity)
    {
        var recycler = new PerItemControlRecycling();
        var docs = new ObservableCollection<IDockable>();
        var views = new Dictionary<IDockable, Control>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < count; i++)
        {
            var doc = new Document { Id = $"d{i}", Title = $"Doc {i}" };
            var view = new Border { Tag = doc.Id };
            recycler.Add(doc, view);
            docs.Add(doc);
            views[doc] = view;
        }

        var host = new HotDocumentHost { Recycling = recycler, Capacity = capacity, ItemsSource = docs };
        return (host, docs, recycler, views);
    }

    private static Control? Shown(HotDocumentHost host)
        => host.Children.FirstOrDefault(c => c.IsVisible)?.GetVisualDescendants().OfType<Border>().FirstOrDefault()
            ?? host.Children.OfType<Panel>().FirstOrDefault(c => c.IsVisible)?.Children.OfType<Border>().FirstOrDefault();

    [Fact]
    public async Task Activating_documents_keeps_their_views_attached_up_to_capacity_least_recent_first_out()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(() =>
        {
            var (host, docs, _, views) = Build(count: 5, capacity: 3);

            host.Active = docs[0];
            host.Active = docs[1];
            host.Active = docs[2];
            Assert.Equal(3, host.Children.Count);
            Assert.Equal(["d0", "d1", "d2"], host.Hot.Select(d => d.Id));
            Assert.Same(views[docs[2]], Shown(host));
            Assert.Single(host.Children, c => c.IsVisible);

            // Going back to d0 promotes it; nothing is built, nothing is detached.
            host.Active = docs[0];
            Assert.Equal(["d1", "d2", "d0"], host.Hot.Select(d => d.Id));
            Assert.Equal(3, host.Children.Count);
            Assert.Same(views[docs[0]], Shown(host));

            // A fourth document evicts the least recently activated, d1, which stays built in the recycler.
            host.Active = docs[3];
            Assert.Equal(["d2", "d0", "d3"], host.Hot.Select(d => d.Id));
            Assert.Equal(3, host.Children.Count);
            Assert.Null(views[docs[1]].GetVisualParent());

            // Back to d1: the same view instance comes back, warm.
            host.Active = docs[1];
            Assert.Same(views[docs[1]], Shown(host));
            Assert.Equal(["d0", "d3", "d1"], host.Hot.Select(d => d.Id));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_closed_document_leaves_the_host_and_a_smaller_capacity_trims_at_once()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(() =>
        {
            var (host, docs, _, _) = Build(count: 4, capacity: 4);
            foreach (var d in docs)
            {
                host.Active = d;
            }
            Assert.Equal(4, host.Children.Count);

            docs.RemoveAt(1);
            Assert.Equal(["d0", "d2", "d3"], host.Hot.Select(d => d.Id));
            Assert.Equal(3, host.Children.Count);

            host.Capacity = 1;
            Assert.Equal(["d3"], host.Hot.Select(d => d.Id));
            Assert.Single(host.Children);
            Assert.True(host.Children[0].IsVisible);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Hidden_views_are_not_measured()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        await session.Dispatch(() =>
        {
            var (host, docs, _, views) = Build(count: 2, capacity: 2);
            host.Active = docs[0];
            host.Active = docs[1];
            host.Measure(new Size(800, 600));
            host.Arrange(new Rect(0, 0, 800, 600));

            Assert.True(views[docs[1]].IsMeasureValid);
            Assert.False(views[docs[0]].IsEffectivelyVisible);
        }, CancellationToken.None);
    }
}
