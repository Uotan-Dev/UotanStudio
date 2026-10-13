using Avalonia.Media;
using UotanStudio.Services.Compare;

namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// Status colour palette for comparison rows: equal, different, left-only, right-only, and an unrecognised fallback.
/// <para>对比行的状态配色：相同、不同、仅左、仅右以及未知状态的兜底颜色。</para>
/// </summary>
internal static class CompareStatusBrushes
{
    /// <summary>
    /// Returns the status colour for an item state, falling back to gray.
    /// <para>按条目状态取对应颜色，未知状态回退为灰色。</para>
    /// </summary>
    /// <param name="status">The item status.<para>条目状态。</para></param>
    /// <returns>The brush for that status.<para>该状态对应的画刷。</para></returns>
    internal static IBrush For(FolderItemStatus status) => status switch
    {
        FolderItemStatus.Equal => Brushes.Green,
        FolderItemStatus.Different => Brushes.IndianRed,
        FolderItemStatus.LeftOnly => Brushes.Orange,
        FolderItemStatus.RightOnly => Brushes.DodgerBlue,
        _ => Brushes.Gray,
    };
}