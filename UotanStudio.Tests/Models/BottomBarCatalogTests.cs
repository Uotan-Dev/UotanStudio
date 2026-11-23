using UotanStudio.Models;
using Xunit;

namespace UotanStudio.Tests.Models;

/// <summary>
/// Checks the bottom bar's persisted preferences against the catalog rules the bar consumes.
/// <para>校验底栏持久化偏好是否满足底栏消费的目录规则。</para>
/// </summary>
public class BottomBarCatalogTests
{
    [Fact]
    public void Normalize_PartialOrder_KeepsSuppliedOrderAndHidesOnlyUntickedItems()
    {
        var preferences = new List<DisplayPreference>
        {
            new() { Key = BottomBarCatalog.Flash, Visible = false },
            new() { Key = BottomBarCatalog.Workspace, Visible = true }
        };

        var normalized = BottomBarCatalog.Normalize(preferences);

        Assert.Equal(
            [BottomBarCatalog.Flash, BottomBarCatalog.Workspace, .. BottomBarCatalog.DefaultOrder.Skip(2)],
            normalized.Select(item => item.Key).ToArray());
        Assert.False(normalized.Single(item => item.Key == BottomBarCatalog.Flash).Visible);
        Assert.True(normalized.Single(item => item.Key == BottomBarCatalog.Workspace).Visible);
    }

    [Fact]
    public void Normalize_MissingItems_DefaultsThemToVisible()
    {
        var normalized = BottomBarCatalog.Normalize([new DisplayPreference { Key = BottomBarCatalog.Node, Visible = false }]);

        Assert.Equal(BottomBarCatalog.DefaultOrder.Count, normalized.Count);
        Assert.Single(normalized, item => !item.Visible);
    }
}