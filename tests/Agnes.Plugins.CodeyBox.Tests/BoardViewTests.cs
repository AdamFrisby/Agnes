using Agnes.Plugins.CodeyBox.Views;
using Agnes.Plugins.CodeyBox.Views.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// Renders the runway, the relations band, the composer and the chain strip, and captures a PNG of each.
/// </summary>
/// <remarks>
/// The board's failure mode is not an exception. A row whose command binding resolves to nothing renders
/// perfectly and does nothing; a strip whose pips are drawn but not hit-testable looks identical to one
/// that works; a horizon with no rows and no sentence looks like a request that failed. None of that is
/// visible from reading the markup, so these attach the controls for real, press the things a person
/// would press, and assert on what came back.
/// </remarks>
[Collection("avalonia-headless")]
public class BoardViewTests
{
    /// <summary>Just enough theme for the roles to resolve — see <see cref="OverviewViewTests"/>.</summary>
    private sealed class TestApp : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            RequestedThemeVariant = ThemeVariant.Dark;
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                ["Bg"] = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x16)),
                ["Panel"] = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x20)),
                ["PanelAlt"] = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x2C)),
                ["Line"] = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x38)),
                ["Fg"] = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xF2)),
                ["FgDim"] = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xB6)),
                ["FgFaint"] = new SolidColorBrush(Color.FromRgb(0x76, 0x76, 0x86)),
                ["Accent"] = new SolidColorBrush(Color.FromRgb(0x8B, 0x5C, 0xF6)),
                ["Danger"] = new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0x9B)),
                ["StatusWorking"] = new SolidColorBrush(Color.FromRgb(0x5A, 0xB8, 0xF0)),
                ["StatusAttention"] = new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x39)),
                ["StatusDone"] = new SolidColorBrush(Color.FromRgb(0x5A, 0xD6, 0xA8)),
                ["StatusError"] = new SolidColorBrush(Color.FromRgb(0xF4, 0x72, 0x9B)),
                ["StatusIdle"] = new SolidColorBrush(Color.FromRgb(0x76, 0x76, 0x86)),
            });
        }
    }

    public static class TestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }

    private static readonly string ShotDir =
        Environment.GetEnvironmentVariable("AGNES_BOARD_SHOTS")
        ?? Path.Combine(Path.GetTempPath(), "board-preview");

    private static void Render(Func<Control> build, string shot, double width, double height,
                               Action<Window>? inspect = null)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));
        session.Dispatch(() =>
        {
            var window = new Window
            {
                Width = width,
                Height = height,
                Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x16)),
                Content = build(),
            };

            window.Show();
            Dispatcher.UIThread.RunJobs();
            Capture(window, shot);
            inspect?.Invoke(window);
            window.Close();
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void Capture(Window window, string name)
    {
        WriteableBitmap? frame;
        try
        {
            frame = window.CaptureRenderedFrame();
        }
        catch (NotSupportedException)
        {
            // Headless drawing has no frame to hand back, and nothing is wrong. The scratchpad harness
            // adds Skia when pixels are actually wanted.
            return;
        }

        if (frame is null)
        {
            return;
        }

        Directory.CreateDirectory(ShotDir);
        using (frame)
        using (var file = File.Create(Path.Combine(ShotDir, name + ".png")))
        {
            frame.Save(file, new PngBitmapEncoderOptions());
        }
    }

    private static BoardView Runway(BoardStub stub) => new()
    {
        DataContext = stub,
        RunNextCommand = stub.RunNextCommand,
        BeginRunAfterCommand = stub.BeginRunAfterCommand,
        RunAfterCommand = stub.RunAfterCommand,
    };

    private static List<Button> Rows(Window window, string cls)
        => [.. window.GetVisualDescendants().OfType<Button>()
                 .Where(b => b.Classes.Contains(cls) && b.IsEffectivelyVisible)];

    private static List<string> Words(Window window)
        => [.. window.GetVisualDescendants().OfType<TextBlock>()
                 .Where(t => t.IsEffectivelyVisible)
                 .Select(t => t.Text ?? string.Empty)];

    [Fact]
    public void The_whole_runway_renders_from_a_hand_built_board()
    {
        var stub = new BoardStub { HistoryMatches = BoardSamples.History() };
        var board = stub.Board!;

        Render(() => Runway(stub), "board-runway", 1000, 1180, window =>
        {
            // Every chain on the board gets a row: two running, four queued, five waiting, three landed,
            // one history match. A horizon that silently renders nothing is the failure this catches.
            var expected = board.Now.Count
                           + board.Next.Count
                           + board.Waiting.Sum(g => g.Chains.Count)
                           + board.Landed.Sum(d => d.Chains.Count)
                           + stub.HistoryMatches.Count;

            // Yesterday's landed day is folded away, and so is every waiting group except the one that
            // needs a person, so their rows are not realised.
            var folded = board.Landed.Where(d => d.Title != "Today").Sum(d => d.Chains.Count)
                         + board.Waiting.Where(g => !g.OpenByDefault).Sum(g => g.Chains.Count);
            Assert.Equal(expected - folded, Rows(window, "chainbutton").Count);

            // Each horizon states its own count in its header rather than leaving it to be inferred.
            var said = Words(window);
            Assert.Contains(board.NowHeader, said);
            Assert.Contains(board.NextHeader, said);
            Assert.Contains(board.WaitingHeader, said);
            Assert.Contains(board.LandedHeader, said);
            Assert.Contains(board.HistoryLabel, said);

            // Waiting is grouped by reason, because a reason is an unblock.
            foreach (var group in board.Waiting)
            {
                Assert.Contains(group.Header, said);
            }
        });
    }

    [Fact]
    public void A_row_hands_the_select_command_the_chain_it_names()
    {
        var stub = new BoardStub();

        Render(() => Runway(stub), "board-select", 1000, 1180, window =>
        {
            var rows = Rows(window, "chainbutton");
            Assert.NotEmpty(rows);

            // Not one of them may be dead: a null command renders as a permanently disabled row and
            // throws nothing at all.
            Assert.All(rows, row => Assert.Same(stub.SelectChainCommand, row.Command));

            rows[0].Command!.Execute(rows[0].CommandParameter);
        });

        Assert.Same(stub.Board!.Now[0], Assert.Single(stub.SelectChainCommand.Parameters));
    }

    [Fact]
    public void Queued_rows_carry_the_four_ways_to_reorder_them()
    {
        var stub = new BoardStub();

        Render(() => Runway(stub), "board-reorder", 1000, 1180, window =>
        {
            // The action cluster is hover-revealed with opacity, so it is present and pressable even
            // when it is not being looked at — which is what lets this assert on it at all.
            var actions = window.GetVisualDescendants().OfType<StackPanel>()
                .Where(p => p.Classes.Contains("rowactions") && p.IsEffectivelyVisible)
                .ToList();

            Assert.Equal(stub.Board!.Next.Count, actions.Count);

            foreach (var button in actions.SelectMany(a => a.Children.OfType<Button>()))
            {
                Assert.NotNull(button.Command);
                button.Command!.Execute(button.CommandParameter);
            }
        });

        Assert.Equal(stub.Board!.Next.Count, stub.RunNextCommand.Parameters.Count);
        Assert.Equal(stub.Board.Next.Count, stub.MoveUpCommand.Parameters.Count);
        Assert.Equal(stub.Board.Next.Count, stub.MoveDownCommand.Parameters.Count);
        Assert.Equal(stub.Board.Next.Count, stub.BeginRunAfterCommand.Parameters.Count);
        Assert.All(stub.RunNextCommand.Parameters, p => Assert.IsType<Chain>(p));
    }

    [Fact]
    public void An_armed_move_says_which_chain_it_is_moving_and_offers_the_other_rows()
    {
        var moving = BoardSamples.Fleet().Next[2];
        var stub = new BoardStub { PendingMove = moving };

        Render(() => Runway(stub), "board-runafter", 1000, 1180, window =>
        {
            Assert.Contains($"Choose where {moving.Title} should run", Words(window));

            // Every queued row EXCEPT the one being moved becomes a destination: offering the moved row
            // itself would be an instruction to put it after itself.
            var destinations = Rows(window, "putafter");
            Assert.Equal(stub.Board!.Next.Count - 1, destinations.Count);
            Assert.DoesNotContain(destinations, d => ReferenceEquals(d.CommandParameter, moving));

            destinations[0].Command!.Execute(destinations[0].CommandParameter);
        });

        Assert.Single(stub.RunAfterCommand.Parameters);
    }

    [Fact]
    public void Dropping_a_queued_row_onto_another_arms_the_move_and_lands_it()
    {
        var stub = new BoardStub();
        var board = stub.Board!;

        Render(() => Runway(stub), "board-drop", 1000, 1180, window =>
        {
            var rows = window.GetVisualDescendants().OfType<Control>()
                .Where(c => c.Classes.Contains("nextrow") && c.DataContext is Chain)
                .ToList();

            Assert.Equal(board.Next.Count, rows.Count);

            // The platform's drag LOOP cannot run headlessly — DoDragDropAsync needs a real drag source
            // — but the drop itself is an ordinary routed event, so the half that decides what a drop
            // MEANS is exercised here for real.
            Drop(moved: board.Next[3], onto: rows[1], above: true);

            // Above row 1 means "run after row 0", expressed as the same two commands the buttons use.
            Assert.Same(board.Next[3], Assert.Single(stub.BeginRunAfterCommand.Parameters));
            Assert.Same(board.Next[0], Assert.Single(stub.RunAfterCommand.Parameters));

            // Above the first row there is nothing to follow, so it is "run next" instead.
            Drop(moved: board.Next[3], onto: rows[0], above: true);
            Assert.Same(board.Next[3], Assert.Single(stub.RunNextCommand.Parameters));

            // Dropping a row back where it already is changes nothing.
            Drop(moved: board.Next[1], onto: rows[1], above: true);
            Assert.Single(stub.RunAfterCommand.Parameters);
        });
    }

    private static void Drop(Chain moved, Control onto, bool above)
    {
        var payload = new DataTransfer();
        payload.Add(DataTransferItem.Create(BoardView.ChainFormat, moved));

        var at = new Point(12, above ? 2 : onto.Bounds.Height - 2);
        onto.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, payload, onto, at, KeyModifiers.None));
    }

    [Fact]
    public void A_chain_expands_to_its_steps_and_each_one_selects_itself()
    {
        var stub = new BoardStub();
        var series = stub.Board!.Now[0];

        Render(() => Runway(stub), "board-expanded", 1000, 1180, window =>
        {
            // The progress count is the disclosure, and it now says so with a chevron beside the count.
            // A singleton has neither, which is why the count of toggles is the count of multi-step
            // chains rather than of rows.
            var toggles = window.GetVisualDescendants().OfType<ToggleButton>()
                .Where(t => t.Classes.Contains("progress") && t.IsEffectivelyVisible)
                .ToList();

            Assert.NotEmpty(toggles);
            Assert.Contains(series.Progress, Words(window));
            Assert.All(toggles, t => Assert.NotEmpty(
                t.GetVisualDescendants().OfType<FluentIcons.Avalonia.SymbolIcon>()));

            var first = toggles.First(t => t.GetVisualDescendants().OfType<TextBlock>()
                                            .Any(b => b.Text == series.Progress));
            first.IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            var steps = Rows(window, "steprow");
            Assert.Equal(series.Count, steps.Count);

            steps[2].Command!.Execute(steps[2].CommandParameter);
            Assert.Same(series.Steps[2], Assert.Single(stub.SelectStepCommand.Parameters));
        });
    }

    [Fact]
    public void An_empty_board_says_so_in_words_per_horizon()
    {
        var stub = new BoardStub { Board = new Board([], [], [], [], 0, (0, 2)) };

        Render(() => Runway(stub), "board-empty", 760, 520, window =>
        {
            var said = Words(window);
            Assert.Contains("Nothing running.", said);
            Assert.Contains("Nothing queued — add work with New.", said);
            Assert.Contains("Nothing waiting.", said);
            Assert.Contains("Nothing landed in the last 7 days.", said);
        });
    }

    [Fact]
    public void The_runway_tolerates_having_no_board_at_all()
        => Render(() => new BoardView { DataContext = new BoardStub { Board = null } },
                  "board-null", 760, 400);

    [Fact]
    public void The_whole_tab_composes_the_runway_the_composer_and_the_relations_band()
    {
        // The three new controls are hosted by CodeyBoxQueueView, which is where a Grid.Row or a
        // RowSpan being wrong stops being invisible. Rendering them individually cannot see that.
        var stub = new BoardStub
        {
            Relations = BoardSamples.Held(),
            Composer = new ComposerStub { IsOpen = true },
        };

        stub.Selected = stub.Relations!.Item;

        Render(() => new CodeyBoxQueueView { DataContext = stub }, "board-in-tab", 1500, 1000, window =>
        {
            Assert.Single(window.GetVisualDescendants().OfType<BoardView>());
            Assert.Single(window.GetVisualDescendants().OfType<RelationsBandView>());

            // The composer takes the column while it is open, and the runway waits underneath it.
            var composer = Assert.Single(window.GetVisualDescendants().OfType<ComposerView>());
            Assert.True(composer.IsEffectivelyVisible);

            // And the item pane it sits beside still shows the item's own facts.
            Assert.Contains(stub.Selected!.Title, Words(window));
        });
    }

    [Fact]
    public void The_relations_band_names_the_blocker_and_offers_its_unblock()
    {
        var stub = new BoardStub { Relations = BoardSamples.Held() };
        var relations = stub.Relations!;

        Render(() => new RelationsBandView { DataContext = stub }, "board-relations", 720, 340, window =>
        {
            var said = Words(window);
            Assert.Contains(relations.PositionLabel, said);
            Assert.Contains($"Blocked by {relations.BlockingRoot!.Item.Title} (failed)", said);

            // A failed parent is retried; a cancelled one is uncancelled; the two are never both offered.
            var buttons = Rows(window, "small");
            Assert.Contains(buttons, b => Equals(b.Content, "Retry it"));
            Assert.DoesNotContain(buttons, b => Equals(b.Content, "Uncancel it"));

            buttons.First(b => Equals(b.Content, "Retry it")).Command!.Execute(relations.BlockingRoot);

            // Dropping an edge acts on the RELATION, not on the item the pane is showing.
            var remove = Rows(window, "chipaction");
            Assert.Equal(relations.Parents.Count, remove.Count);
            remove[0].Command!.Execute(remove[0].CommandParameter);

            // And the four ways to make more work from here all reach the composer.
            foreach (var create in buttons.Where(b => b.CommandParameter is ComposerIntent))
            {
                create.Command!.Execute(create.CommandParameter);
            }
        });

        Assert.Same(stub.Relations!.BlockingRoot, Assert.Single(stub.RetryParentCommand.Parameters));
        Assert.IsType<Relation>(Assert.Single(stub.RemoveDependencyCommand.Parameters));
        Assert.Equal(
            [ComposerIntent.FollowUp, ComposerIntent.Sibling, ComposerIntent.Split, ComposerIntent.Duplicate],
            stub.OpenComposerCommand.Parameters);
    }

    [Fact]
    public void The_relations_band_opens_the_dependency_picker_in_place()
    {
        var stub = new BoardStub { Relations = BoardSamples.Clear(), IsAddingDependency = true };

        Render(() => new RelationsBandView { DataContext = stub }, "board-picker", 720, 420, window =>
        {
            var ticks = window.GetVisualDescendants().OfType<CheckBox>()
                .Where(c => c.Classes.Contains("candidate") && c.IsEffectivelyVisible)
                .ToList();

            Assert.Equal(stub.Picker.Candidates.Count, ticks.Count);
            ticks[1].Command!.Execute(ticks[1].CommandParameter);

            var add = Rows(window, "small").First(b => Equals(b.Content, "Add 1"));
            Assert.True(add.IsEnabled);
            add.Command!.Execute(null);
        });

        Assert.IsType<DependencyCandidate>(Assert.Single(stub.Picker.TickCommand.Parameters));
        Assert.True(stub.ApplyAddDependencyCommand.Ran);
    }

    [Fact]
    public void The_composer_renders_a_single_item_with_every_inference_as_a_chip()
    {
        var composer = new ComposerStub { IsOpen = true, Intent = ComposerIntent.FollowUp };

        Render(() => new ComposerView { DataContext = composer }, "composer-single", 720, 700, window =>
        {
            var said = Words(window);
            Assert.Contains("Project:", said);
            Assert.Contains("Agent:", said);
            Assert.Contains("Branch:", said);
            Assert.Contains("Ceiling:", said);
            Assert.Contains("Position:", said);
            Assert.Contains("Depends on:", said);

            // Inherited values say so, which is the whole difference between "chosen" and "not asked".
            Assert.Equal(3, said.Count(t => t == "· default"));

            // One item, so the button does not claim to be creating steps.
            var create = window.GetVisualDescendants().OfType<Button>()
                .First(b => b.Classes.Contains("accent"));
            Assert.Equal("Create", create.Content);
            Assert.True(create.IsEnabled);
            create.Command!.Execute(null);

            // A chip opens exactly one editor, and pressing it again closes it.
            var view = window.GetVisualDescendants().OfType<ComposerView>().Single();
            var chip = window.GetVisualDescendants().OfType<Button>()
                .First(b => b.Classes.Contains("chip") && Equals(b.CommandParameter, "agent"));

            chip.Command!.Execute(chip.CommandParameter);
            Assert.Equal("agent", view.OpenField);
            chip.Command.Execute(chip.CommandParameter);
            Assert.Null(view.OpenField);
        });

        Assert.True(composer.CreateCommand.Ran);
    }

    [Fact]
    public void A_pasted_plan_becomes_a_chain_with_retickable_edges()
    {
        var drafts = Enumerable.Range(1, 7)
            .Select(i => new Draft("codeybox-self", $"Step {i}", "do it", $"plan-{i}", [], null, null, null, null, null, false))
            .ToList();

        var composer = new ComposerStub
        {
            IsOpen = true,
            Plan = new Plan(drafts, ["step 4 waits on step 9, which does not exist"], "plan-20260906-1412"),
            Steps = [.. Enumerable.Range(1, 7).Select(i => new PlanStepStub
            {
                Index = i,
                Title = $"Step {i} — carve the {i}th piece",
                Parents = i == 1 ? string.Empty : $"after: {i - 1}",
            })],
            Priority = 40,
            PriorityIsManual = true,
            Position = Position.Next,
        };

        Render(() => new ComposerView { DataContext = composer }, "composer-plan", 720, 860, window =>
        {
            var said = Words(window);
            Assert.Contains("7 steps as a chain", said);
            Assert.Contains("step 4 waits on step 9, which does not exist", said);

            // The size of what is about to be created is on the button, because this is the one press
            // here that cannot be taken back.
            var create = window.GetVisualDescendants().OfType<Button>()
                .First(b => b.Classes.Contains("accent"));
            Assert.Equal("Create 7 steps", create.Content);

            // Six toggles per step: the edges a step could be given, 1 to N−1.
            var toggles = window.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("tiny") && b.IsEffectivelyVisible)
                .ToList();

            Assert.Equal(composer.Steps.Count * 6, toggles.Count);
            Assert.All(toggles, t => Assert.NotNull(t.Command));

            toggles[0].Command!.Execute(toggles[0].CommandParameter);
        });

        Assert.Equal(1, Assert.Single(composer.Steps[0].ToggleParentCommand.Parameters));
    }

    [Fact]
    public void A_chain_strip_draws_every_state_and_a_pip_selects_its_own_step()
    {
        var chain = BoardSamples.Series();
        var every = Enum.GetValues<StepState>()
            .Select((s, i) => new Step(chain.Steps[i % chain.Count].Item, i, s, $"{i + 1}/8"))
            .ToList();

        var fired = new Fired();

        Render(() => new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new ChainStrip { Steps = chain.Steps, SelectedId = chain.Steps[3].Item.Id, StepCommand = fired },
                new ChainStrip { Steps = every },
                new ChainStrip { Steps = [chain.Steps[0]] },
                // Nothing at all must draw nothing rather than throw.
                new ChainStrip(),
            },
        }, "chain-strip", 220, 120, window =>
        {
            var strips = window.GetVisualDescendants().OfType<ChainStrip>().ToList();
            Assert.Equal(4, strips.Count);

            // Sized from the steps, so a seven-step chain asks for visibly more room than a one-step one
            // and an empty strip asks for none at all.
            Assert.Equal(7 * 11 - 3, strips[0].DesiredSize.Width, 1);
            Assert.Equal(8, strips[2].DesiredSize.Width, 1);
            Assert.Equal(0, strips[3].DesiredSize.Width);

            // Naming the steps is what stops the strip being a colour puzzle.
            var tip = ToolTip.GetTip(strips[0]) as string;
            Assert.NotNull(tip);
            Assert.Contains(chain.Steps[3].Item.Title, tip, StringComparison.Ordinal);
            Assert.Contains("running", tip, StringComparison.Ordinal);

            strips[0].RaiseEvent(Press(strips[0], new Point(2 * 11 + 4, 5)));
        });

        Assert.Same(chain.Steps[2], Assert.Single(fired.Parameters));
    }

    [Fact]
    public void A_long_chain_draws_a_capped_strip_that_still_names_and_selects_every_step()
    {
        var chain = BoardSamples.Long("Test selection (RTS)", 32, 27);
        var fired = new Fired();

        Render(() => new ChainStrip { Steps = chain.Steps, StepCommand = fired }, "chain-strip-capped", 260, 60,
               window =>
        {
            var strip = window.GetVisualDescendants().OfType<ChainStrip>().Single();

            // Eleven pips and one gap, not thirty-two: the strip that squeezed a row's title to nothing
            // was 349px wide, and this one is a picture that fits beside its caption.
            Assert.Equal((11 * 8) + 11 + (11 * 3), strip.DesiredSize.Width, 1);
            Assert.True(strip.DesiredSize.Width < 150);

            // Folding is a drawing decision, so the tooltip still lists every step, including the ones
            // the gap stands for.
            var tip = ToolTip.GetTip(strip) as string;
            Assert.NotNull(tip);
            Assert.Equal(32, tip!.Split('\n').Length);
            Assert.Contains(chain.Steps[20].Item.Title, tip, StringComparison.Ordinal);

            // The first pip is the first step; the last pip is the LAST step, not the twelfth.
            strip.RaiseEvent(Press(strip, new Point(4, 6)));
            strip.RaiseEvent(Press(strip, new Point(strip.DesiredSize.Width - 4, 6)));

            // …and the gap selects nothing, rather than whichever step the arithmetic lands on.
            strip.RaiseEvent(Press(strip, new Point((8 * 11) + 5, 6)));
        });

        Assert.Equal([chain.Steps[0], chain.Steps[^1]], fired.Parameters);
    }

    [Fact]
    public void A_long_queue_shows_its_front_and_opens_the_rest_on_request()
    {
        var stub = new BoardStub { Board = BoardSamples.Busy() };
        var board = stub.Board!;

        Render(() => Runway(stub), "board-folded", 1000, 1180, window =>
        {
            Assert.Equal(Board.NextPreview, Queued(window).Count);

            var more = window.GetVisualDescendants().OfType<ToggleButton>()
                .Single(t => t.Classes.Contains("showmore") && t.IsEffectivelyVisible);
            Assert.Contains(board.NextMoreLabel, Words(window));

            more.IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            // The whole dispatch order, once asked for — and the front of it still first.
            Assert.Equal(board.Next.Count, Queued(window).Count);
            Assert.Equal(board.Next[0].Id, ((Chain)Queued(window)[0].DataContext!).Id);
        });
    }

    /// <summary>The queued rows actually realised, in order — the thing the fold changes.</summary>
    private static List<Control> Queued(Window window)
        => [.. window.GetVisualDescendants().OfType<Control>()
                 .Where(c => c.Classes.Contains("nextrow") && c.DataContext is Chain && c.IsEffectivelyVisible)];

    [Fact]
    public void Only_what_needs_a_person_is_open_when_the_board_arrives()
    {
        var stub = new BoardStub { Board = BoardSamples.Busy() };
        var board = stub.Board!;

        Render(() => Runway(stub), "board-waiting-folded", 1000, 1180, window =>
        {
            // Every group states itself in a header…
            var said = Words(window);
            Assert.All(board.Waiting, g => Assert.Contains(g.Header, said));

            // …and only the one an operator can act on has drawn its rows.
            var shown = window.GetVisualDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("chainbutton") && b.IsEffectivelyVisible)
                .Select(b => b.DataContext)
                .OfType<Chain>()
                .Where(c => c.IsWaiting)
                .ToList();

            Assert.Equal(
                board.Waiting.Where(g => g.OpenByDefault).SelectMany(g => g.Chains).Select(c => c.Id),
                shown.Select(c => c.Id));
        });
    }

    [Fact]
    public void The_key_names_every_mark_it_draws_and_draws_them_the_way_the_rows_do()
    {
        var stub = new BoardStub();

        Render(() => Runway(stub), "board-key", 1000, 1180, window =>
        {
            var key = window.GetVisualDescendants().OfType<Button>()
                .Single(b => b.Classes.Contains("legend"));

            var flyout = Assert.IsType<Flyout>(key.Flyout);
            var content = Assert.IsType<StackPanel>(flyout.Content);
            var words = content.GetLogicalDescendants().OfType<TextBlock>()
                .Select(t => t.Text ?? string.Empty)
                .ToList();

            // Every state a pip can be drawn in is in the key, said in the same words the chips use.
            foreach (var state in Enum.GetValues<StepState>())
            {
                Assert.Contains(StepMarks.Word(state), words);
            }

            // …and it is drawn by the real control, so it cannot drift from the board.
            var marks = content.GetLogicalDescendants().OfType<StepDot>().ToList();
            Assert.Equal(Enum.GetValues<StepState>().Length, marks.Count);
            Assert.Equal([.. Enum.GetValues<StepState>().OrderBy(s => s)],
                         [.. marks.Select(m => m.State).OrderBy(s => s)]);

            Assert.Equal(2, content.GetLogicalDescendants().OfType<ChainStrip>().Count());
        });
    }

    [Fact]
    public void Nothing_in_a_row_is_drawn_outside_the_pane_it_belongs_to()
    {
        // The original complaint, as an assertion: at half width the reason text was being cut off by the
        // pane boundary, which no amount of reading the markup would have shown.
        var stub = new BoardStub { Board = BoardSamples.Busy() };

        Render(() => Runway(stub), "board-fits", 700, 1180, window =>
        {
            // The runway's own scroller, not the search box's: a TextBox carries one of these too.
            var scroller = window.GetVisualDescendants().OfType<ScrollViewer>()
                .First(s => s.GetVisualDescendants().OfType<Control>()
                             .Any(c => c.Classes.Contains("chainrow")));
            var width = scroller.Bounds.Width;
            Assert.True(width > 0);

            // Horizontal overflow is disabled, so a row wider than the viewport would be silently clipped
            // rather than scrollable — which is the failure this is here to catch.
            Assert.Equal(ScrollBarVisibility.Disabled, scroller.HorizontalScrollBarVisibility);
            Assert.True(scroller.Extent.Width <= scroller.Viewport.Width + 0.5,
                        $"the runway wants {scroller.Extent.Width} of {scroller.Viewport.Width}");

            var rows = window.GetVisualDescendants().OfType<Control>()
                .Where(c => c.Classes.Contains("chainrow") && c.IsEffectivelyVisible)
                .ToList();

            Assert.NotEmpty(rows);
            foreach (var row in rows)
            {
                var right = row.TranslatePoint(new Point(row.Bounds.Width, 0), scroller);
                Assert.NotNull(right);
                Assert.True(right!.Value.X <= width + 0.5,
                            $"a row reaches {right.Value.X} of {width}, in \"{(row.DataContext as Chain)?.Title}\"");
            }
        });
    }

    private static PointerPressedEventArgs Press(Control target, Point at)
    {
        var pointer = new Pointer(0, PointerType.Mouse, isPrimary: true);
        var properties = new PointerPointProperties(RawInputModifiers.LeftMouseButton,
                                                    PointerUpdateKind.LeftButtonPressed);

        return new PointerPressedEventArgs(target, pointer, target, at, 0, properties, KeyModifiers.None);
    }
}
