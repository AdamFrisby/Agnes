using System.Text.Json;
using Agnes.Plugins.CodeyBox;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// The queue view model as a runway: horizons instead of filter chips, chains as rows, and every steering
/// action expressed as the HTTP calls it actually makes.
/// </summary>
/// <remarks>
/// <para>The tests marked <c>Skip</c> below are real and finished; they are waiting on the pure model in
/// <c>BoardModel.cs</c>, which is landing alongside this and today throws
/// <see cref="NotImplementedException"/> from every entry point. <b>Integrator: remove the Skip.</b> They
/// are written against the real functions on purpose — stubbing the model would test the stub, and what
/// is worth pinning here is that a click produces exactly the priority rewrites the model asked for.</para>
///
/// <para>Everything that does not need the model runs now, which is most of the steering: the view model
/// is handed a <see cref="Board"/> and the assertions are about what it sends.</para>
/// </remarks>
public class BoardViewModelTests
{

    private static (CodeyBoxQueueViewModel Vm, RoutingHandler Http) New(Action<RoutingHandler>? arrange = null)
    {
        var http = new RoutingHandler();
        arrange?.Invoke(http);
        var vm = new CodeyBoxQueueViewModel(
            new CodeyBoxClient(new CodeyBoxOptions("http://codeybox.test", "k"), http),
            action => { action(); return Task.CompletedTask; });
        return (vm, http);
    }

    // ---- the board ----

    [Fact]
    public async Task The_board_is_built_on_every_refresh()
    {
        var (vm, _) = New(h => h.ItemsBody = JsonSerializer.Serialize(new[]
        {
            Fake.Row("aaaa1111", "Working"),
            Fake.Row("bbbb2222"),
        }));

        await vm.RefreshAsync();
        await vm.BoardReady;

        Assert.True(vm.HasBoard);
        Assert.NotNull(vm.Board);
    }

    [Fact]
    public async Task Search_narrows_every_horizon_and_surfaces_history()
    {
        // One narrowing feeds all four horizons — a search that moved Next but left Landed describing the
        // whole fleet would be worse than no search at all. And history, which is hidden by default,
        // becomes reachable exactly when someone searches.
        var (vm, _) = New(h => h.ItemsBody = JsonSerializer.Serialize(new[]
        {
            Fake.Row("aaaa1111", "Working", title: "Fix quota detection"),
            Fake.Row("bbbb2222", title: "Circuit breaker"),
            Fake.Row("cccc3333", "Cancelled", title: "Old quota attempt", ageDays: 90),
        }));

        await vm.RefreshAsync();
        await vm.BoardReady;

        vm.Search = "quota";
        await vm.BoardReady;

        Assert.DoesNotContain(vm.Board!.Now.Concat(vm.Board.Next), c => c.Title.Contains("Circuit"));
        Assert.Contains(vm.HistoryMatches, c => c.Steps.Any(s => s.Item.Id == "cccc3333"));
    }

    [Fact]
    public async Task History_is_empty_when_nothing_is_being_searched_for()
    {
        var (vm, _) = New(h => h.ItemsBody = JsonSerializer.Serialize(new[]
        {
            Fake.Row("cccc3333", "Cancelled", ageDays: 90),
        }));

        await vm.RefreshAsync();
        await vm.BoardReady;

        Assert.Empty(vm.HistoryMatches);
    }

    [Fact]
    public async Task Selecting_recomputes_the_relations_band()
    {
        var (vm, _) = New(h => h.ItemsBody = JsonSerializer.Serialize(new[]
        {
            Fake.Row("aaaa1111", "Failed", title: "Parent"),
            Fake.Row("bbbb2222", title: "Child", depsOk: false, dependsOn: ["aaaa1111"]),
        }));

        await vm.RefreshAsync();
        await vm.BoardReady;

        vm.Selected = vm.Items.First(i => i.Id == "bbbb2222");
        await vm.BoardReady;

        Assert.NotNull(vm.Relations);
        Assert.Contains(vm.Relations!.Parents, p => p.Item.Id == "aaaa1111");
    }

    [Fact]
    public async Task Run_next_sends_exactly_the_priority_patches_the_model_asked_for()
    {
        // The orchestrator's own /workitems/reorder writes a hint the dispatcher ignores, so a move IS a
        // set of priority rewrites. What this pins is that the view model sends the model's answer
        // verbatim: same ids, same values, in the same order, and nothing else.
        var rows = new[]
        {
            Fake.Row("aaaa1111", title: "First", priority: 50),
            Fake.Row("bbbb2222", title: "Second", priority: 20),
            Fake.Row("cccc3333", title: "Third", priority: 10),
        };
        var (vm, http) = New(h => h.ItemsBody = JsonSerializer.Serialize(rows));

        await vm.RefreshAsync();
        await vm.BoardReady;

        var last = vm.Board!.Next[^1];
        var expected = BoardModel.Reorder(
            [.. vm.Board.Next.Select(c => c.Head)], last.Head.Id, 0,
            new Dictionary<string, int> { ["codeybox-self"] = 1000 });

        http.Clear();
        await vm.RunNextCommand.ExecuteAsync(last);

        var patches = http.Of("PATCH").Where(r => r.Path.EndsWith("/priority", StringComparison.Ordinal)).ToList();
        Assert.Equal(expected.Count, patches.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal($"/workitems/{expected[i].Id}/priority", patches[i].Path);
            Assert.Contains($"\"priority\":{expected[i].To}", patches[i].Body, StringComparison.Ordinal);
        }
    }

    // ---- steering, against a board handed in by hand ----

    [Fact]
    public void Selecting_a_chain_selects_the_member_it_stands_for()
    {
        // A chain is what you read and an item is what you act on, so the row's click has to land on the
        // step the row is actually about — the running one, or whichever is holding it up.
        var (vm, _) = New();
        var head = Fake.Row("bbbb2222", "Working", title: "Step 2");
        var chain = Fake.Chain(head, [Fake.Row("aaaa1111", "Done", title: "Step 1"), head]);

        vm.SelectChainCommand.Execute(chain);

        Assert.Equal("bbbb2222", vm.Selected?.Id);
    }

    [Fact]
    public void Selecting_a_step_selects_that_step()
    {
        var (vm, _) = New();
        var row = Fake.Row("aaaa1111", "Done");

        vm.SelectStepCommand.Execute(Fake.Step(row, 0, StepState.Done));

        Assert.Equal("aaaa1111", vm.Selected?.Id);
    }

    [Fact]
    public async Task A_move_that_touches_a_few_items_is_not_confirmed()
    {
        // Moving one chain a place is visible and reversible. A dialog on it would only teach the operator
        // to dismiss dialogs, which is exactly what makes the one on a bulk renumber worth having.
        var (vm, http) = New();
        vm.Load([Fake.Row("aaaa1111", priority: 50), Fake.Row("bbbb2222", priority: 20)]);
        vm.Board = Fake.Board(
            Fake.Chain(Fake.Row("aaaa1111", priority: 50)),
            Fake.Chain(Fake.Row("bbbb2222", priority: 20)));

        http.Clear();
        await vm.MoveUpCommand.ExecuteAsync(vm.Board.Next[1]);

        Assert.False(vm.Confirmation.IsPending);
    }

    [Fact]
    public async Task A_chain_that_is_not_queued_cannot_be_reordered()
    {
        var (vm, http) = New();
        vm.Board = Fake.Board(Fake.Chain(Fake.Row("aaaa1111")));

        http.Clear();
        await vm.RunNextCommand.ExecuteAsync(Fake.Chain(Fake.Row("zzzz9999", "Working")));

        Assert.Empty(http.Of("PATCH"));
        Assert.Contains("queued", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_after_is_armed_and_can_be_abandoned()
    {
        // Two clicks, not a drag: arming is what puts "put it here" on every row, and Escape has to be
        // able to take it away again without moving anything.
        var (vm, _) = New();
        var chain = Fake.Chain(Fake.Row("aaaa1111"));

        vm.BeginRunAfterCommand.Execute(chain);
        Assert.True(vm.HasPendingMove);
        Assert.Equal(chain, vm.PendingMove);

        vm.CancelPendingMoveCommand.Execute(null);
        Assert.False(vm.HasPendingMove);
        Assert.Null(vm.PendingMove);
    }

    [Fact]
    public async Task Run_after_with_nothing_armed_does_nothing()
    {
        var (vm, http) = New();
        vm.Board = Fake.Board(Fake.Chain(Fake.Row("aaaa1111")));

        http.Clear();
        await vm.RunAfterCommand.ExecuteAsync(vm.Board.Next[0]);

        Assert.Empty(http.Of("PATCH"));
    }

    // ---- dependencies ----

    [Fact]
    public async Task Removing_a_dependency_sends_the_whole_remaining_set()
    {
        // dependsOn is a REPLACE-SET: what goes on the wire is the list that should be true afterwards,
        // not a delta. Sending only the dropped id would clear every other edge on the item.
        var (vm, http) = New();
        var child = Fake.Row("cccc3333", dependsOn: ["aaaa1111", "bbbb2222"], depsOk: false);
        vm.Load([Fake.Row("aaaa1111", "Failed"), Fake.Row("bbbb2222"), child]);
        vm.Selected = child;

        http.Clear();
        await vm.RemoveDependencyCommand.ExecuteAsync(Fake.Relation(Fake.Row("aaaa1111", "Failed")));

        // Confirmed first, and named: the prompt has to say which edge is about to go.
        Assert.True(vm.Confirmation.IsPending);
        Assert.Contains("aaaa1111", vm.Confirmation.Prompt, StringComparison.Ordinal);

        await vm.Confirmation.ConfirmCommand.ExecuteAsync(null);

        var patch = Assert.Single(http.Of("PATCH"), r => r.Path == "/workitems/cccc3333");
        Assert.Contains("\"dependsOn\":[\"bbbb2222\"]", patch.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Adding_dependencies_sends_the_existing_ones_back_with_the_new()
    {
        var (vm, http) = New();
        var subject = Fake.Row("cccc3333", dependsOn: ["aaaa1111"], depsOk: false);
        vm.Load([Fake.Row("aaaa1111", "Failed"), Fake.Row("bbbb2222"), subject]);
        vm.Selected = subject;

        vm.OpenAddDependencyCommand.Execute(null);
        Assert.True(vm.IsAddingDependency);

        vm.Picker.TickCommand.Execute(vm.Picker.Candidates.First(c => c.Item.Id == "bbbb2222"));

        http.Clear();
        await vm.ApplyAddDependencyCommand.ExecuteAsync(null);

        var patch = Assert.Single(http.Of("PATCH"), r => r.Path == "/workitems/cccc3333");
        Assert.Contains("aaaa1111", patch.Body, StringComparison.Ordinal);
        Assert.Contains("bbbb2222", patch.Body, StringComparison.Ordinal);
        Assert.False(vm.IsAddingDependency);
    }

    [Fact]
    public void The_add_dependency_picker_opens_pre_ticked_with_what_the_item_already_waits_on()
    {
        // Opening the picker on an item with two parents and seeing nothing ticked would read as "it has
        // no dependencies" — and applying it would then silently clear both.
        var (vm, _) = New();
        var subject = Fake.Row("cccc3333", dependsOn: ["aaaa1111"], depsOk: false);
        vm.Load([Fake.Row("aaaa1111", "Failed"), Fake.Row("bbbb2222"), subject]);
        vm.Selected = subject;

        vm.OpenAddDependencyCommand.Execute(null);

        Assert.Contains("aaaa1111", vm.Picker.Ticked);
        Assert.DoesNotContain(vm.Picker.Candidates, c => c.Item.Id == "cccc3333");
    }

    [Fact]
    public async Task Retrying_the_blocking_parent_hits_the_parent_and_not_the_child()
    {
        // A dependency is satisfied only by Done, so a Failed parent blocks its children until someone
        // retries it. Getting the target wrong here retries the item that is already fine.
        var (vm, http) = New();
        vm.Load([Fake.Row("aaaa1111", "Failed"), Fake.Row("cccc3333", dependsOn: ["aaaa1111"])]);
        vm.Selected = vm.Items.First(i => i.Id == "cccc3333");

        http.Clear();
        await vm.RetryParentCommand.ExecuteAsync(Fake.Relation(Fake.Row("aaaa1111", "Failed")));

        Assert.Contains(http.Requests, r => r is { Method: "POST", Path: "/workitems/aaaa1111/retry" });
        Assert.DoesNotContain(http.Requests, r => r.Path.Contains("cccc3333/retry", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Uncancelling_the_blocking_parent_hits_the_uncancel_route()
    {
        var (vm, http) = New();
        vm.Load([Fake.Row("aaaa1111", "Cancelled"), Fake.Row("cccc3333", dependsOn: ["aaaa1111"])]);
        vm.Selected = vm.Items.First(i => i.Id == "cccc3333");

        http.Clear();
        await vm.UncancelParentCommand.ExecuteAsync(
            Fake.Relation(Fake.Row("aaaa1111", "Cancelled"), StepState.Cancelled));

        Assert.Contains(http.Requests, r => r is { Method: "POST", Path: "/workitems/aaaa1111/uncancel" });
    }

    // ---- the composer's entry points ----

    [Fact]
    public void Opening_the_composer_carries_the_selection_and_the_project_filter()
    {
        var (vm, _) = New();
        vm.Load([Fake.Row("aaaa1111", title: "Fix quota detection")]);
        vm.ProjectFilter = "codeybox-self";
        vm.Selected = vm.Items[0];

        vm.OpenComposerCommand.Execute(ComposerIntent.FollowUp);

        Assert.True(vm.Composer.IsOpen);
        Assert.Equal(ComposerIntent.FollowUp, vm.Composer.Intent);
        Assert.Equal("codeybox-self", vm.Composer.ProjectId);

        // A follow-up waits on what it follows — pre-ticked, so the edge is the default rather than a
        // step someone has to remember.
        Assert.Contains("aaaa1111", vm.Composer.Picker.Ticked);
    }

    [Fact]
    public async Task Promoting_a_suggestion_opens_the_composer_instead_of_creating_outright()
    {
        // The orchestrator's own promote creates an item on the spot with every field left to the
        // project. That is the right default and the wrong only option.
        var (vm, http) = New();
        var suggestion = new Suggestion(
            "s1", "src", "codeybox-self", "Cache the audit reports", "because they are re-read",
            "performance", "important", "small", DateTimeOffset.UtcNow, "open", null, ["a.cs"]);

        http.Clear();
        await vm.Sections.PromoteSuggestionCommand.ExecuteAsync(suggestion);

        Assert.True(vm.Composer.IsOpen);
        Assert.Equal(ComposerIntent.Promote, vm.Composer.Intent);
        Assert.DoesNotContain(http.Requests, r => r.Path.Contains("/promote", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_sections_view_model_with_no_composer_still_promotes_the_old_way()
    {
        // The hook is optional, so a sections view model built on its own — in a test, or a future
        // screen — keeps the behaviour it had rather than silently doing nothing.
        var http = new RoutingHandler();
        await using var client = new CodeyBoxClient(new CodeyBoxOptions("http://codeybox.test", "k"), http);
        var sections = new CodeyBoxSectionsViewModel(client, action => { action(); return Task.CompletedTask; });

        await sections.PromoteSuggestionCommand.ExecuteAsync(new Suggestion(
            "s1", null, "codeybox-self", "A suggestion", null, null, null, null,
            DateTimeOffset.UtcNow, "open", null, null));

        Assert.Contains(http.Requests, r => r.Path == "/suggestions/s1/promote");
    }
}
