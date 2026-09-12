using Agnes.Plugins.CodeyBox.Views.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Agnes.Plugins.CodeyBox.Tests;

/// <summary>
/// The card panel's promised height is the height it uses. A WrapPanel in the same place described two
/// rows of a three-row grid, and the page's scroll extent stopped short of the last card.
/// </summary>
[Collection("avalonia-headless")]
public class CardFlowTests
{
    [Fact]
    public async Task Nine_cards_at_four_per_row_measure_as_three_rows_and_arrange_that_way()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(OverviewViewTests.TestAppBuilder));
        await session.Dispatch(() =>
        {
            var panel = new CardFlow();
            for (var i = 0; i < 9; i++)
            {
                panel.Children.Add(new Border { Width = 312, Height = 120, Margin = new Thickness(0, 0, 10, 10) });
            }
            var window = new Window { Width = 1463, Height = 800, Content = new StackPanel { Children = { panel } } };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Four 322px cards fit in 1463px; nine cards are three rows of 130px.
            Assert.Equal(3 * 130, panel.DesiredSize.Height, 0.5);
            Assert.Equal(3 * 130, panel.Bounds.Height, 0.5);
            Assert.Equal(2 * 130, panel.Children[8].Bounds.Y, 0.5);
            Assert.Equal(0, panel.Children[8].Bounds.X, 0.5);
            window.Close();
        }, CancellationToken.None);
    }
}
