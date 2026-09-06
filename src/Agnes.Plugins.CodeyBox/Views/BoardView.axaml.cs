using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace Agnes.Plugins.CodeyBox.Views;

/// <summary>
/// The runway. Its DataContext is the queue view model; its code-behind exists for one reason —
/// dragging a queued chain to a new place in the dispatch order.
/// </summary>
/// <remarks>
/// <para>Dragging is the gesture people already have for "this one goes there", and the alternative the
/// board previously offered was hand-typing priority numbers: the fleet this was designed against had
/// ~70 distinct ones, which is what a queue looks like when reordering is arithmetic. It cannot be
/// expressed in markup, so it is here.</para>
///
/// <para>A drop is deliberately expressed in the same two commands as the buttons — arm a move, then
/// name its destination — rather than in a third path of its own. That keeps one definition of what a
/// reorder means (and one place where the priority rewrites are computed), and it is why the button path
/// and the drag path cannot drift apart.</para>
///
/// <para>The three reorder commands arrive as properties rather than being pulled out of the DataContext
/// by name: code that reaches into a view model by string is code the compiler stops checking, and this
/// file already has the harder half of the problem to get right.</para>
/// </remarks>
public partial class BoardView : UserControl
{
    /// <summary>How far the pointer must travel before a press becomes a drag rather than a click.</summary>
    private const double DragThreshold = 5;

    /// <summary>Puts a chain at the front of the queue. Parameter: the <see cref="Chain"/>.</summary>
    public static readonly StyledProperty<ICommand?> RunNextCommandProperty =
        AvaloniaProperty.Register<BoardView, ICommand?>(nameof(RunNextCommand));

    /// <summary>Arms a move. Parameter: the <see cref="Chain"/> being moved.</summary>
    public static readonly StyledProperty<ICommand?> BeginRunAfterCommandProperty =
        AvaloniaProperty.Register<BoardView, ICommand?>(nameof(BeginRunAfterCommand));

    /// <summary>Lands the armed move. Parameter: the <see cref="Chain"/> it should run after.</summary>
    public static readonly StyledProperty<ICommand?> RunAfterCommandProperty =
        AvaloniaProperty.Register<BoardView, ICommand?>(nameof(RunAfterCommand));

    public ICommand? RunNextCommand
    {
        get => GetValue(RunNextCommandProperty);
        set => SetValue(RunNextCommandProperty, value);
    }

    public ICommand? BeginRunAfterCommand
    {
        get => GetValue(BeginRunAfterCommandProperty);
        set => SetValue(BeginRunAfterCommandProperty, value);
    }

    public ICommand? RunAfterCommand
    {
        get => GetValue(RunAfterCommandProperty);
        set => SetValue(RunAfterCommandProperty, value);
    }

    private Point _pressedAt;
    private PointerPressedEventArgs? _pressed;
    private Chain? _pressedChain;
    private bool _dragging;

    public BoardView()
    {
        InitializeComponent();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(PointerPressedEvent, OnPointerPressedAnywhere, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMovedAnywhere, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleasedAnywhere, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// The format the board's own drags carry: in-process, so the payload is the <see cref="Chain"/>
    /// itself rather than a serialised id that would have to be looked up again on the way out.
    /// </summary>
    internal static readonly DataFormat<Chain> ChainFormat =
        DataFormat.CreateInProcessFormat<Chain>("agnes.codeybox.chain");

    private void OnPointerPressedAnywhere(object? sender, PointerPressedEventArgs e)
    {
        _dragging = false;
        _pressedChain = null;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _pressedAt = e.GetPosition(this);
        _pressed = e;
        _pressedChain = ChainUnder(e.Source as Visual);
    }

    private void OnPointerReleasedAnywhere(object? sender, PointerReleasedEventArgs e)
    {
        _dragging = false;
        _pressed = null;
        _pressedChain = null;
    }

    private void OnPointerMovedAnywhere(object? sender, PointerEventArgs e)
    {
        if (_dragging || _pressedChain is not { } chain || _pressed is not { } pressed)
        {
            return;
        }

        var moved = e.GetPosition(this) - _pressedAt;
        if (Math.Abs(moved.X) < DragThreshold && Math.Abs(moved.Y) < DragThreshold)
        {
            return;
        }

        // Latched before the await: DoDragDrop pumps its own input loop, and re-entering here would
        // start a second drag for the same press.
        _dragging = true;
        _pressedChain = null;
        _pressed = null;

        var payload = new DataTransfer();
        payload.Add(DataTransferItem.Create(ChainFormat, chain));
        _ = DragDrop.DoDragDropAsync(pressed, payload, DragDropEffects.Move);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = Dropping(e) is null ? DragDropEffects.None : DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        _dragging = false;

        if (Dropping(e) is not { } move)
        {
            return;
        }

        e.Handled = true;

        // Dropped above the first queued row: there is nothing to run after, so this is "run next".
        if (move.After is null)
        {
            Run(RunNextCommand, move.Moved);
            return;
        }

        // Same two steps the buttons take, in the same order, so a drag and a click produce one outcome.
        Run(BeginRunAfterCommand, move.Moved);
        Run(RunAfterCommand, move.After);
    }

    private static void Run(ICommand? command, Chain chain)
    {
        if (command is not null && command.CanExecute(chain))
        {
            command.Execute(chain);
        }
    }

    /// <summary>
    /// What this drop means: the chain being moved, and the chain it should follow (null for the front).
    /// </summary>
    /// <remarks>
    /// The insertion point is read from the pointer's half of the row it is over, which is how every
    /// list-reorder in every other application behaves; the chain to run after is then simply whatever
    /// row precedes that point. Rows are found in the visual tree rather than by index into
    /// <c>Board.Next</c>, so this works against a view model of any shape — including the stub the render
    /// tests use.
    /// </remarks>
    private (Chain Moved, Chain? After)? Dropping(DragEventArgs e)
    {
        if (e.DataTransfer.TryGetValue(ChainFormat) is not { } moved || !moved.IsNext)
        {
            return null;
        }

        var rows = QueuedRows();
        var target = RowUnder(e.Source as Visual);
        if (target is null)
        {
            return null;
        }

        var index = rows.FindIndex(r => ReferenceEquals(r.Row, target));
        if (index < 0)
        {
            return null;
        }

        var above = e.GetPosition(target).Y < target.Bounds.Height / 2;
        var insertAt = above ? index : index + 1;

        // Landing either side of the row it came from is not a move.
        var from = rows.FindIndex(r => string.Equals(r.Chain.Id, moved.Id, StringComparison.Ordinal));
        if (from >= 0 && (insertAt == from || insertAt == from + 1))
        {
            return null;
        }

        if (insertAt == 0)
        {
            return (moved, null);
        }

        // Stepping over the row's own slot: the chain that will precede it once it has left its place.
        var beforeIndex = from >= 0 && from < insertAt ? insertAt : insertAt - 1;
        if (beforeIndex < 0 || beforeIndex >= rows.Count)
        {
            return null;
        }

        var after = rows[beforeIndex].Chain;
        return string.Equals(after.Id, moved.Id, StringComparison.Ordinal) ? null : (moved, after);
    }

    private List<(Control Row, Chain Chain)> QueuedRows()
        => [.. this.GetVisualDescendants()
                .OfType<Control>()
                .Where(c => c.Classes.Contains("nextrow") && c.DataContext is Chain)
                .Select(c => (c, (Chain)c.DataContext!))];

    private static Control? RowUnder(Visual? from)
    {
        for (var walk = from; walk is not null; walk = walk.GetVisualParent())
        {
            if (walk is Control control && control.Classes.Contains("nextrow"))
            {
                return control;
            }
        }

        return null;
    }

    private static Chain? ChainUnder(Visual? from) => RowUnder(from)?.DataContext as Chain;
}
