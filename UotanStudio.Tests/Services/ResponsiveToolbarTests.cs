using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using UotanStudio.Views.Controls;
using Xunit;

namespace UotanStudio.Tests.Services;

public sealed class ResponsiveToolbarTests
{
    [Fact]
    public void MeasureAndArrange_WideWidth_KeepsAllItemsWithoutOverflow()
    {
        var toolbar = CreateToolbar(
            out var search,
            out var highBtn,
            out var lowBtn,
            out var status);

        toolbar.Measure(new Size(460, double.PositiveInfinity));
        toolbar.Arrange(new Rect(0, 0, 460, toolbar.DesiredSize.Height));

        Assert.Equal(4, toolbar.MainItems.Count);
        Assert.Empty(toolbar.OverflowItems);
        Assert.False(toolbar.HasOverflow);
        Assert.Equal(0d, toolbar.MoreButton.Opacity);
        Assert.Equal(180, toolbar.GetAssignedWidth(search), 1);
        Assert.Equal(20, toolbar.GetAssignedWidth(highBtn), 1);
        Assert.Equal(20, toolbar.GetAssignedWidth(lowBtn), 1);
        Assert.Equal(120, toolbar.GetAssignedWidth(status), 1);
    }

    [Fact]
    public void MeasureAndArrange_MediumWidth_ShrinksFlexibleItemsBeforeOverflowing()
    {
        var toolbar = CreateToolbar(
            out var search,
            out var highBtn,
            out var lowBtn,
            out var status);

        toolbar.Measure(new Size(320, double.PositiveInfinity));
        toolbar.Arrange(new Rect(0, 0, 320, toolbar.DesiredSize.Height));

        Assert.Equal(4, toolbar.MainItems.Count);
        Assert.Empty(toolbar.OverflowItems);
        Assert.False(toolbar.HasOverflow);
        Assert.InRange(toolbar.GetAssignedWidth(search), 80, 179.9);
        Assert.InRange(toolbar.GetAssignedWidth(status), 48, 119.9);
        Assert.Equal(20, toolbar.GetAssignedWidth(highBtn), 1);
        Assert.Equal(20, toolbar.GetAssignedWidth(lowBtn), 1);
    }

    [Fact]
    public void MeasureAndArrange_NarrowWidth_EvictsLowerPriorityItemsSynchronouslyWithoutOverlap()
    {
        var toolbar = CreateToolbar(
            out var search,
            out var highBtn,
            out var lowBtn,
            out var status);

        toolbar.Measure(new Size(140, double.PositiveInfinity));
        toolbar.Arrange(new Rect(0, 0, 140, toolbar.DesiredSize.Height));

        Assert.True(toolbar.HasOverflow);
        Assert.Equal(1d, toolbar.MoreButton.Opacity);
        Assert.True(toolbar.MoreButton.IsHitTestVisible);
        Assert.Equal(2, toolbar.MainItems.Count);
        Assert.Contains(search, toolbar.MainItems);
        Assert.Contains(highBtn, toolbar.MainItems);
        Assert.Contains(status, toolbar.OverflowItems);
        Assert.Contains(lowBtn, toolbar.OverflowItems);
        Assert.True(highBtn.Bounds.X >= search.Bounds.Right + 7.9);
        Assert.True(toolbar.MoreButton.Bounds.X >= highBtn.Bounds.Right + 7.9);
        Assert.True(lowBtn.Bounds.X < -1000);
        Assert.True(status.Bounds.X < -1000);
    }

    [Fact]
    public void MeasureAndArrange_InvisibleItem_IsIgnoredInLayoutAndOverflow()
    {
        var toolbar = CreateToolbar(
            out var search,
            out var highBtn,
            out var lowBtn,
            out var status);
        status.IsVisible = false;

        toolbar.Measure(new Size(260, double.PositiveInfinity));
        toolbar.Arrange(new Rect(0, 0, 260, toolbar.DesiredSize.Height));

        Assert.DoesNotContain(status, toolbar.MainItems);
        Assert.DoesNotContain(status, toolbar.OverflowItems);
        Assert.Contains(search, toolbar.MainItems);
        Assert.Contains(highBtn, toolbar.MainItems);
        Assert.Contains(lowBtn, toolbar.MainItems);
        Assert.Empty(toolbar.OverflowItems);
        Assert.False(toolbar.HasOverflow);
    }

    [Fact]
    public void ResizeAcrossOverflowBoundary_KeepsAllItemsAttachedAndRestoresBoundsImmediately()
    {
        var toolbar = CreateToolbar(
            out var search,
            out var highBtn,
            out var lowBtn,
            out var status);

        toolbar.Measure(new Size(140, double.PositiveInfinity));
        toolbar.Arrange(new Rect(0, 0, 140, toolbar.DesiredSize.Height));
        Assert.Contains(lowBtn, toolbar.OverflowItems);
        Assert.Same(toolbar, lowBtn.Parent);

        toolbar.OpenOverflowForTesting();
        toolbar.Measure(new Size(140, double.PositiveInfinity));
        toolbar.Arrange(new Rect(0, 0, 140, toolbar.DesiredSize.Height));
        Assert.Contains(lowBtn, toolbar.OverflowPanel.Children);
        Assert.Contains(status, toolbar.OverflowPanel.Children);

        toolbar.CloseOverflowForTesting();
        Assert.Empty(toolbar.OverflowPanel.Children);
        Assert.Same(toolbar, lowBtn.Parent);

        toolbar.Measure(new Size(460, double.PositiveInfinity));
        toolbar.Arrange(new Rect(0, 0, 460, toolbar.DesiredSize.Height));

        Assert.Equal(20, lowBtn.Bounds.Width, 1);
        Assert.True(lowBtn.Bounds.X >= highBtn.Bounds.Right + 7.9);
        Assert.True(status.Bounds.X >= lowBtn.Bounds.Right + 7.9);
        Assert.False(toolbar.HasOverflow);
    }

    [Fact]
    public void MeasureAndArrange_OverflowWithLeftoverSpace_ExpandsStretchFlexibleItemToFillGap()
    {
        var search = new Border
        {
            MinWidth = 120,
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ResponsiveToolbar.SetPriority(search, 3);
        ResponsiveToolbar.SetIsFlexible(search, true);
        ResponsiveToolbar.SetPreferredWidth(search, 200);

        var level = new Border { Width = 120, Height = 22 };
        ResponsiveToolbar.SetPriority(level, 2);

        var toolbar = new ResponsiveToolbar { Spacing = 8 };
        toolbar.Items.Add(level);
        toolbar.Items.Add(search);

        toolbar.Measure(new Size(240, double.PositiveInfinity));
        toolbar.Arrange(new Rect(0, 0, 240, toolbar.DesiredSize.Height));

        Assert.True(toolbar.HasOverflow);
        Assert.Single(toolbar.MainItems);
        Assert.Equal(212, search.Bounds.Width, 1);
    }

    private static ResponsiveToolbar CreateToolbar(
        out Border search,
        out Border highBtn,
        out Border lowBtn,
        out Border status)
    {
        search = new Border
        {
            MinWidth = 80,
            MaxWidth = 180,
            Height = 22,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ResponsiveToolbar.SetPriority(search, 3);
        ResponsiveToolbar.SetIsFlexible(search, true);

        highBtn = new Border { Width = 20, Height = 20 };
        ResponsiveToolbar.SetPriority(highBtn, 2);

        lowBtn = new Border { Width = 20, Height = 20 };
        ResponsiveToolbar.SetPriority(lowBtn, 0);

        status = new Border
        {
            Width = 120,
            MinWidth = 48,
            MaxWidth = 120,
            Height = 16
        };
        ResponsiveToolbar.SetPriority(status, -1);
        ResponsiveToolbar.SetIsFlexible(status, true);

        var toolbar = new ResponsiveToolbar { Spacing = 8 };
        toolbar.Items.Add(search);
        toolbar.Items.Add(highBtn);
        toolbar.Items.Add(lowBtn);
        toolbar.Items.Add(status);
        return toolbar;
    }
}