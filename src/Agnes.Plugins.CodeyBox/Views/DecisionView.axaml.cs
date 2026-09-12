using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Agnes.Plugins.CodeyBox.Views;

/// <summary>
/// The decision the selected item is asking an operator to make. DataContext: the queue view model.
/// </summary>
/// <remarks>
/// <para>Its own control for the same reason <see cref="RelationsBandView"/> is: everything it draws is a
/// function of one <see cref="Decision"/> record, so every blocked state it covers — a question, a failed
/// merge, an audit at its ceiling, an item abandoned after recovery — can be rendered and looked at from a
/// hand-built one, months before a live orchestrator happens to produce them all on the same afternoon.</para>
///
/// <para>It takes the queue view model rather than the <see cref="Decision"/> itself because the reply box
/// belongs to the card: <c>AnswerText</c> and <c>AnsweringQuestion</c> are the operator's typing, not part
/// of what is being asked.</para>
/// </remarks>
public partial class DecisionView : UserControl
{
    public DecisionView() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
