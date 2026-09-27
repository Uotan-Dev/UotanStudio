using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using UotanStudio.Models;
using UotanStudio.ViewModels.Editors;
using UotanStudio.Views.Controls;
using Xunit;

namespace UotanStudio.Tests.Views;

[Collection("Node editor UI")]
public sealed class PartitionBandPanelLayoutTests(NodeEditorUiFixture fixture)
{
    [Fact]
    public async Task Arrange_ProportionalSegments_LaysOutLeftToRightWithSpacing()
    {
        await Dispatch(() =>
        {
            var gap = Segment(null, 0.10);
            var real = Segment(new PartitionItemViewModel(new PartitionEntry { Name = "system" }, 0, null), 0.90);
            var panel = new PartitionBandPanel { Children = { gap, real } };
            panel.Measure(new Size(400, 40));
            panel.Arrange(new Rect(0, 0, 400, 40));

            Assert.Equal(0, gap.Bounds.X);
            Assert.InRange(gap.Bounds.Width, 35, 45);
            Assert.True(real.Bounds.X > gap.Bounds.Right);
            Assert.InRange(real.Bounds.Width, 340, 370);
            Assert.InRange(real.Bounds.Right, 398, 402);
        });
    }

    [Fact]
    public async Task Arrange_TinySegmentWidth_ExpandsToMinimumSegmentWidth()
    {
        await Dispatch(() =>
        {
            var real = Segment(new PartitionItemViewModel(new PartitionEntry { Name = "tiny" }, 0, null), 0.001);
            var panel = new PartitionBandPanel { Children = { real } };
            panel.Measure(new Size(400, 40));
            panel.Arrange(new Rect(0, 0, 400, 40));

            Assert.Equal(panel.MinSegmentWidth, real.Bounds.Width, precision: 3);
        });
    }

    [Fact]
    public async Task Arrange_ZeroRatioGap_CollapsesToZeroWidth()
    {
        await Dispatch(() =>
        {
            var gap = Segment(null, 0);
            var real = Segment(new PartitionItemViewModel(new PartitionEntry { Name = "only" }, 0, null), 1.0);
            var panel = new PartitionBandPanel { Children = { gap, real } };
            panel.Measure(new Size(400, 40));
            panel.Arrange(new Rect(0, 0, 400, 40));

            Assert.Equal(0, gap.Bounds.Width);
            Assert.Equal(0, real.Bounds.X, precision: 3);
            Assert.InRange(real.Bounds.Right, 398, 402);
        });
    }

    [Fact]
    public async Task Arrange_SaturationKeepsLayoutWithinAvailableWidth()
    {
        await Dispatch(() =>
        {
            var panel = new PartitionBandPanel();
            for (var i = 0; i < 20; i++)
            {
                panel.Children.Add(Segment(new PartitionItemViewModel(new PartitionEntry { Name = "p" + i }, i, null), 0.05));
            }

            panel.Measure(new Size(400, 40));
            panel.Arrange(new Rect(0, 0, 400, 40));

            Assert.InRange(panel.Children[^1].Bounds.Right, 396, 404);
            Assert.All(panel.Children, child => Assert.True(child.Bounds.Width > 0));
        });
    }

    private static Border Segment(PartitionItemViewModel? item, double widthRatio) => new()
    {
        DataContext = new PartitionSegmentViewModel(item, item?.Name ?? string.Empty, item?.Name ?? "free", "0x0", "tip", Brushes.Gray, widthRatio)
    };

    private async Task Dispatch(Action action)
    {
        Task dispatchTask;
        using (ExecutionContext.SuppressFlow())
        {
            dispatchTask = fixture.Session.Dispatch(() =>
            {
                action();
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await dispatchTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }
}