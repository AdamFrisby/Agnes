using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// Pins the property the queue actually depends on: a refresh that changes nothing must raise nothing,
/// and a refresh that changes one row must disturb only that row. Both were violated by the previous
/// Clear()+re-add, which raised a Reset every five seconds and made the list unreadable while open.
/// </summary>
public sealed class ReconcileTests
{
    private sealed record Row(string Id, string State);

    private static (ObservableCollection<Row> Items, List<NotifyCollectionChangedEventArgs> Changes) Watch(
        params Row[] initial)
    {
        var items = new ObservableCollection<Row>(initial);
        var changes = new List<NotifyCollectionChangedEventArgs>();
        items.CollectionChanged += (_, e) => changes.Add(e);
        return (items, changes);
    }

    [Fact]
    public void UnchangedListRaisesNothing()
    {
        var (items, changes) = Watch(new Row("a", "Queued"), new Row("b", "Running"));

        Reconcile.Apply(items, [new Row("a", "Queued"), new Row("b", "Running")], r => r.Id);

        // Not merely "no Reset" — no notification at all, so a bound list is not touched.
        Assert.Empty(changes);
    }

    [Fact]
    public void ChangedRowIsReplacedInPlaceAndNeighboursAreUntouched()
    {
        var (items, changes) = Watch(new Row("a", "Queued"), new Row("b", "Running"), new Row("c", "Done"));

        Reconcile.Apply(items, [new Row("a", "Queued"), new Row("b", "Failed"), new Row("c", "Done")], r => r.Id);

        var change = Assert.Single(changes);
        Assert.Equal(NotifyCollectionChangedAction.Replace, change.Action);
        Assert.Equal(1, change.NewStartingIndex);
        Assert.Equal("Failed", items[1].State);
    }

    [Fact]
    public void AppendCostsOneAddAndLeavesTheHistoryAlone()
    {
        // The timeline case the operator hit: a long list being read while a new run lands on the end.
        var (items, changes) = Watch(new Row("a", "x"), new Row("b", "x"), new Row("c", "x"));

        Reconcile.Apply(items, [new Row("a", "x"), new Row("b", "x"), new Row("c", "x"), new Row("d", "x")], r => r.Id);

        var change = Assert.Single(changes);
        Assert.Equal(NotifyCollectionChangedAction.Add, change.Action);
        Assert.Equal(3, change.NewStartingIndex);
    }

    [Fact]
    public void PrependAlsoLeavesTheRestAlone()
    {
        // Runs are ordered newest-first, so a new run arrives at the TOP — the case a naive
        // append-only optimisation would get wrong.
        var (items, changes) = Watch(new Row("b", "x"), new Row("c", "x"));

        Reconcile.Apply(items, [new Row("a", "x"), new Row("b", "x"), new Row("c", "x")], r => r.Id);

        var change = Assert.Single(changes);
        Assert.Equal(NotifyCollectionChangedAction.Add, change.Action);
        Assert.Equal(0, change.NewStartingIndex);
    }

    [Fact]
    public void RemovalTakesOnlyTheRemovedRow()
    {
        var (items, changes) = Watch(new Row("a", "x"), new Row("b", "x"), new Row("c", "x"));

        Reconcile.Apply(items, [new Row("a", "x"), new Row("c", "x")], r => r.Id);

        var change = Assert.Single(changes);
        Assert.Equal(NotifyCollectionChangedAction.Remove, change.Action);
        Assert.Equal(["a", "c"], items.Select(i => i.Id));
    }

    [Fact]
    public void ReorderMovesRatherThanRebuilds()
    {
        var (items, changes) = Watch(new Row("a", "x"), new Row("b", "x"), new Row("c", "x"));

        Reconcile.Apply(items, [new Row("c", "x"), new Row("a", "x"), new Row("b", "x")], r => r.Id);

        Assert.Equal(["c", "a", "b"], items.Select(i => i.Id));
        Assert.All(changes, c => Assert.NotEqual(NotifyCollectionChangedAction.Reset, c.Action));
        Assert.Contains(changes, c => c.Action == NotifyCollectionChangedAction.Move);
    }

    [Fact]
    public void NeverRaisesReset()
    {
        // The whole point. A Reset is what drops containers, scroll position and focus.
        var (items, changes) = Watch(new Row("a", "x"), new Row("b", "y"));

        Reconcile.Apply(items, [new Row("z", "1"), new Row("y", "2")], r => r.Id);
        Reconcile.Apply(items, [], r => r.Id);

        Assert.Empty(items);
        Assert.DoesNotContain(changes, c => c.Action == NotifyCollectionChangedAction.Reset);
    }

    [Fact]
    public void FullReplacementStillEndsInTheRightState()
    {
        var items = new ObservableCollection<Row>([new Row("a", "x"), new Row("b", "y")]);

        Reconcile.Apply(items, [new Row("c", "1"), new Row("d", "2"), new Row("e", "3")], r => r.Id);

        Assert.Equal(["c", "d", "e"], items.Select(i => i.Id));
        Assert.Equal(["1", "2", "3"], items.Select(i => i.State));
    }
}
