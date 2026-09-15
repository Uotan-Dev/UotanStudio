using Avalonia.Media;
using UotanStudio.Services.Compare;

namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// <para>对比状态配色：相同=绿、不同=印度红、仅左=橙、仅右=道奇蓝，其余灰。对比结果行与
/// 目录树节点的状态标记共用同一份映射，避免两处各写一遍。</para>
/// Comparison-status colours: equal=green, different=indian red, left-only=orange, right-only=dodger
/// blue, anything else gray. The result rows and the directory-tree status markers share this single
/// mapping instead of writing it twice.
/// </summary>
internal static class CompareStatusBrushes
{
    /// <summary>按条目状态取状态色；未知状态返回灰色。</summary>
    /// <param name="status"><para>条目状态。</para>The item status.</param>
    /// <returns><para>该状态对应的画刷。</para>The brush for the status.</returns>
    internal static IBrush For(FolderItemStatus status) => status switch
    {
        FolderItemStatus.Equal => Brushes.Green,
        FolderItemStatus.Different => Brushes.IndianRed,
        FolderItemStatus.LeftOnly => Brushes.Orange,
        FolderItemStatus.RightOnly => Brushes.DodgerBlue,
        _ => Brushes.Gray,
    };
}
