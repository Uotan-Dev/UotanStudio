using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using UotanStudio.Views.Controls;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class JustifiedWrapPanelTests
{
    [Theory]
    [InlineData(220)]
    [InlineData(260)]
    [InlineData(360)]
    [InlineData(480)]
    public void MeasureAndArrange_FourChildrenWithDifferentDesiredWidths_AlignsColumnGaps(double availableWidth)
    {
        var desiredWidths = new[] { 60d, 130d, 70d, 115d };
        var panel = CreatePanel(110, 6, 6, desiredWidths);

        panel.Measure(new Size(availableWidth, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, availableWidth, panel.DesiredSize.Height));

        AssertAllRowGapsAligned(panel.Children, availableWidth, 6);
    }

    [Theory]
    [InlineData(150)]
    [InlineData(180)]
    [InlineData(260)]
    [InlineData(350)]
    [InlineData(440)]
    [InlineData(520)]
    [InlineData(610)]
    [InlineData(720)]
    public void MeasureAndArrange_EightChildren_AlignsPartialRowGapsWithFullRowColumns(double availableWidth)
    {
        var desiredWidths = new[] { 55d, 55d, 70d, 55d, 65d, 65d, 110d, 110d };
        var panel = CreatePanel(80, 6, 6, desiredWidths);

        panel.Measure(new Size(availableWidth, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, availableWidth, panel.DesiredSize.Height));

        AssertAllRowGapsAligned(panel.Children, availableWidth, 6);
    }

    [Fact]
    public void MeasureAndArrange_InvisibleChild_IsIgnoredInColumnCount()
    {
        var panel = CreatePanel(110, 6, 6, [80d, 80d, 80d]);
        panel.Children[1].IsVisible = false;

        panel.Measure(new Size(300, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 300, panel.DesiredSize.Height));

        var first = panel.Children[0].Bounds;
        var third = panel.Children[2].Bounds;

        Assert.Equal(0, first.Y, 3);
        Assert.Equal(0, third.Y, 3);
        Assert.Equal(first.Width, third.Width, 3);
        Assert.Equal((300 - 6) / 2.0, first.Width, 3);
    }

    [Fact]
    public void MeasureAndArrange_FourChildrenWhenThreeFit_BalancesIntoTwoByTwo()
    {
        var panel = CreatePanel(110, 6, 6, [80d, 80d, 80d, 80d]);

        panel.Measure(new Size(360, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 360, panel.DesiredSize.Height));

        var rows = panel.Children
            .GroupBy(c => Math.Round(c.Bounds.Y, 3))
            .OrderBy(g => g.Key)
            .ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows[0].Count());
        Assert.Equal(2, rows[1].Count());
        AssertAllRowGapsAligned(panel.Children, 360, 6);
    }

    [Fact]
    public void MeasureAndArrange_EightChildrenWhenSevenFit_BalancesIntoFourByFour()
    {
        var panel = CreatePanel(80, 6, 6, [55d, 55d, 70d, 55d, 65d, 65d, 75d, 75d]);

        panel.Measure(new Size(610, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, 610, panel.DesiredSize.Height));

        var rows = panel.Children
            .GroupBy(c => Math.Round(c.Bounds.Y, 3))
            .OrderBy(g => g.Key)
            .ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal(4, rows[0].Count());
        Assert.Equal(4, rows[1].Count());
        AssertAllRowGapsAligned(panel.Children, 610, 6);
    }

    private static JustifiedWrapPanel CreatePanel(
        double minItemWidth,
        double itemSpacing,
        double lineSpacing,
        double[] desiredWidths)
    {
        var panel = new JustifiedWrapPanel
        {
            MinItemWidth = minItemWidth,
            ItemSpacing = itemSpacing,
            LineSpacing = lineSpacing,
            UseLayoutRounding = false
        };

        foreach (var width in desiredWidths)
        {
            panel.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                UseLayoutRounding = false,
                Child = new Border
                {
                    Width = width,
                    Height = 28
                }
            });
        }

        return panel;
    }

    private static void AssertAllRowGapsAligned(Controls children, double availableWidth, double itemSpacing)
    {
        var rows = children
            .Where(c => c.IsVisible)
            .GroupBy(c => Math.Round(c.Bounds.Y, 3))
            .OrderBy(g => g.Key)
            .Select(g => g.OrderBy(c => c.Bounds.X).ToList())
            .ToList();

        Assert.NotEmpty(rows);

        var fullRow = rows[0];
        var fullRowGapCenters = new HashSet<double>();
        for (var i = 0; i < fullRow.Count - 1; i++)
        {
            var gapStart = fullRow[i].Bounds.Right;
            var gapEnd = fullRow[i + 1].Bounds.X;
            Assert.Equal(itemSpacing, gapEnd - gapStart, 3);
            fullRowGapCenters.Add(Math.Round((gapStart + gapEnd) / 2.0, 2));
        }

        foreach (var row in rows)
        {
            Assert.Equal(0, row[0].Bounds.X, 3);
            Assert.Equal(availableWidth, row[^1].Bounds.Right, 3);

            for (var i = 0; i < row.Count - 1; i++)
            {
                var gapStart = row[i].Bounds.Right;
                var gapEnd = row[i + 1].Bounds.X;
                Assert.Equal(itemSpacing, gapEnd - gapStart, 3);
                var center = Math.Round((gapStart + gapEnd) / 2.0, 2);
                Assert.Contains(center, fullRowGapCenters);
            }
        }
    }
}