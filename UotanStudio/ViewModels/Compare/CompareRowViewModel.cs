using Avalonia.Media;
using UotanStudio.Services.Compare;

namespace UotanStudio.ViewModels.Compare;

/// <summary>
/// <para>对比面板的统一行模型：以一行数据同时驱动左右两栏渲染
/// （文本行的行号/内容、文件夹条目的文件名/大小、十六进制行的偏移/字节串/ASCII 串），
/// 构造后所有属性只读。</para>
/// Unified row model for the compare panel: one row drives both the left and right pane rendering
/// (line numbers/text for text lines, file names/sizes for folder items, and offsets/byte
/// strings/ASCII for hex lines); all properties are read-only after construction.
/// </summary>
public sealed class CompareRowViewModel
{
    /// <summary>
    /// <para>仅左侧条目的左侧淡色背景画刷（橙色调，半透明叠加可辨识）。</para>
    /// Left tint brush for left-only rows (orange, translucent so the theme shows through).
    /// </summary>
    private static readonly IBrush LeftOnlyTintBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xE6, 0x7E, 0x22));

    /// <summary>
    /// <para>仅右侧条目的右侧淡色背景画刷（蓝色调，半透明叠加可辨识）。</para>
    /// Right tint brush for right-only rows (blue, translucent so the theme shows through).
    /// </summary>
    private static readonly IBrush RightOnlyTintBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x1E, 0x90, 0xFF));

    /// <summary>
    /// <para>创建对比行视图模型，所有属性在构造时一次性赋值。</para>
    /// Creates a compare-row view model, assigning every property exactly once at construction.
    /// </summary>
    /// <param name="status">行状态。<para>Row status.</para></param>
    /// <param name="isDirectory">是否为目录。<para>Whether the row is a directory.</para></param>
    /// <param name="depth">文件夹模式的缩进深度（0 起）。<para>Folder-mode indentation depth (0-based).</para></param>
    /// <param name="leftLine">左侧行号或十六进制偏移文本；缺失侧为空串。<para>Left line number or hex offset text; empty when the side is absent.</para></param>
    /// <param name="leftText">左侧主文本：行内容/文件名/十六进制字节串。<para>Left primary text: line content / file name / hex byte string.</para></param>
    /// <param name="leftSub">左侧副文本：大小文本或 ASCII 串。<para>Left secondary text: size text or ASCII string.</para></param>
    /// <param name="isLeftDiff">是否高亮左侧。<para>Whether the left side is highlighted.</para></param>
    /// <param name="rightLine">右侧行号或十六进制偏移文本；缺失侧为空串。<para>Right line number or hex offset text; empty when the side is absent.</para></param>
    /// <param name="rightText">右侧主文本：行内容/文件名/十六进制字节串。<para>Right primary text: line content / file name / hex byte string.</para></param>
    /// <param name="rightSub">右侧副文本：大小文本或 ASCII 串。<para>Right secondary text: size text or ASCII string.</para></param>
    /// <param name="isRightDiff">是否高亮右侧。<para>Whether the right side is highlighted.</para></param>
    /// <param name="leftTint">仅左侧条目的左侧淡色背景画刷；非单侧为 <c>null</c>。
    /// <para>Left tint brush for left-only rows; <c>null</c> otherwise.</para></param>
    /// <param name="rightTint">仅右侧条目的右侧淡色背景画刷；非单侧为 <c>null</c>。
    /// <para>Right tint brush for right-only rows; <c>null</c> otherwise.</para></param>
    public CompareRowViewModel(
        FolderItemStatus status,
        bool isDirectory,
        int depth,
        string leftLine,
        string leftText,
        string leftSub,
        bool isLeftDiff,
        string rightLine,
        string rightText,
        string rightSub,
        bool isRightDiff,
        IBrush? leftTint = null,
        IBrush? rightTint = null)
    {
        Status = status;
        IsDirectory = isDirectory;
        Depth = depth;
        LeftLine = leftLine;
        LeftText = leftText;
        LeftSub = leftSub;
        IsLeftDiff = isLeftDiff;
        RightLine = rightLine;
        RightText = rightText;
        RightSub = rightSub;
        IsRightDiff = isRightDiff;
        LeftTintBrush = leftTint;
        RightTintBrush = rightTint;
        StatusBrush = status switch
        {
            FolderItemStatus.Equal => Brushes.Green,
            FolderItemStatus.Different => Brushes.IndianRed,
            FolderItemStatus.LeftOnly => Brushes.Orange,
            FolderItemStatus.RightOnly => Brushes.DodgerBlue,
            _ => Brushes.Gray,
        };
    }

    /// <summary>
    /// <para>行状态（相同/不同/仅左/仅右）。</para>
    /// Row status (equal/different/left-only/right-only).
    /// </summary>
    public FolderItemStatus Status { get; }

    /// <summary>
    /// <para>是否为目录（文件夹模式）。</para>
    /// Whether the row is a directory (folder mode).
    /// </summary>
    public bool IsDirectory { get; }

    /// <summary>
    /// <para>文件夹模式的缩进深度（0 起）。</para>
    /// Folder-mode indentation depth (0-based).
    /// </summary>
    public int Depth { get; }

    /// <summary>
    /// <para>左侧行号或十六进制偏移文本；缺失侧为空串。</para>
    /// Left line number or hex offset text; empty when the side is absent.
    /// </summary>
    public string LeftLine { get; }

    /// <summary>
    /// <para>左侧主文本：行内容/文件名/十六进制字节串；缺失侧为空串。</para>
    /// Left primary text: line content / file name / hex byte string; empty when the side is absent.
    /// </summary>
    public string LeftText { get; }

    /// <summary>
    /// <para>左侧副文本：大小文本或 ASCII 串；缺失侧为空串。</para>
    /// Left secondary text: size text or ASCII string; empty when the side is absent.
    /// </summary>
    public string LeftSub { get; }

    /// <summary>
    /// <para>左侧是否差异高亮。</para>
    /// Whether the left side is diff-highlighted.
    /// </summary>
    public bool IsLeftDiff { get; }

    /// <summary>
    /// <para>右侧行号或十六进制偏移文本；缺失侧为空串。</para>
    /// Right line number or hex offset text; empty when the side is absent.
    /// </summary>
    public string RightLine { get; }

    /// <summary>
    /// <para>右侧主文本：行内容/文件名/十六进制字节串；缺失侧为空串。</para>
    /// Right primary text: line content / file name / hex byte string; empty when the side is absent.
    /// </summary>
    public string RightText { get; }

    /// <summary>
    /// <para>右侧副文本：大小文本或 ASCII 串；缺失侧为空串。</para>
    /// Right secondary text: size text or ASCII string; empty when the side is absent.
    /// </summary>
    public string RightSub { get; }

    /// <summary>
    /// <para>右侧是否差异高亮。</para>
    /// Whether the right side is diff-highlighted.
    /// </summary>
    public bool IsRightDiff { get; }

    /// <summary>
    /// <para>仅左侧条目的左侧淡色背景画刷；其他状态为 <c>null</c>（视图据此渲染单侧淡色背景）。</para>
    /// Left tint brush for left-only rows; <c>null</c> for other statuses (drives the single-side
    /// tint background in the view).
    /// </summary>
    public IBrush? LeftTintBrush { get; }

    /// <summary>
    /// <para>仅右侧条目的右侧淡色背景画刷；其他状态为 <c>null</c>（视图据此渲染单侧淡色背景）。</para>
    /// Right tint brush for right-only rows; <c>null</c> for other statuses (drives the single-side
    /// tint background in the view).
    /// </summary>
    public IBrush? RightTintBrush { get; }

    /// <summary>
    /// <para>状态色块画刷（相同=绿、不同=印度红、仅左=橙、仅右=道奇蓝，其余灰）。</para>
    /// Status color-block brush (equal=green, different=indian red, left-only=orange,
    /// right-only=dodger blue, anything else gray).
    /// </summary>
    public IBrush StatusBrush { get; }

    /// <summary>
    /// <para>左侧是否为空行（主文本为空，视图据此渲染缺失侧空行）。</para>
    /// Whether the left side is an empty row (empty primary text), letting the view render a blank
    /// row for the missing side.
    /// </summary>
    public bool IsLeftEmpty => string.IsNullOrEmpty(LeftText);

    /// <summary>
    /// <para>右侧是否为空行（主文本为空，视图据此渲染缺失侧空行）。</para>
    /// Whether the right side is an empty row (empty primary text), letting the view render a blank
    /// row for the missing side.
    /// </summary>
    public bool IsRightEmpty => string.IsNullOrEmpty(RightText);

    /// <summary>
    /// <para>从文件夹比对条目构造对比行：Equal/Different 两侧均显示文件名与大小；
    /// 仅左侧条目只在左侧显示名称与大小、右侧单元格置空（不再跨栏重复显示），且左侧带橙色调淡色背景；
    /// 仅右侧条目只在右侧显示、左侧单元格置空，且右侧带蓝色调淡色背景；
    /// Different 两侧整行差异高亮（<see cref="IsLeftDiff"/> / <see cref="IsRightDiff"/>）。</para>
    /// Builds a compare row from a folder-comparison item: Equal/Different show the file name and
    /// size on both panes; left-only items show their name and size on the left only with the right
    /// cell left empty (no longer duplicated across panes) and an orange tint on the left; right-only
    /// items show on the right only with a blue tint on the right; Different rows are highlighted on
    /// both sides via <see cref="IsLeftDiff"/> / <see cref="IsRightDiff"/>.
    /// </summary>
    /// <param name="item">文件夹比对条目。<para>Folder comparison item.</para></param>
    /// <returns><para>渲染该条目所需的对比行。</para> The compare row that renders the item.</returns>
    public static CompareRowViewModel ForFolder(FolderCompareItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var name = Path.GetFileName(item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var depth = Math.Max(0, item.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length - 1);
        var isLeftOnly = item.Status == FolderItemStatus.LeftOnly;
        var isRightOnly = item.Status == FolderItemStatus.RightOnly;
        var isDifferent = item.Status == FolderItemStatus.Different;

        return new CompareRowViewModel(
            item.Status,
            item.IsDirectory,
            depth,
            string.Empty,
            isRightOnly ? string.Empty : name,
            isRightOnly ? string.Empty : (item.LeftSize < 0 ? "-" : FormatSize(item.LeftSize)),
            isDifferent,
            string.Empty,
            isLeftOnly ? string.Empty : name,
            isLeftOnly ? string.Empty : (item.RightSize < 0 ? "-" : FormatSize(item.RightSize)),
            isDifferent,
            isLeftOnly ? LeftOnlyTintBrush : null,
            isRightOnly ? RightOnlyTintBrush : null);
    }

    /// <summary>
    /// <para>从文本差异行构造对比行：Equal→相同、Added→仅右、Deleted→仅左、Modified→不同；
    /// 行号 1-based 转文本，缺失侧行号与文本保持为空。</para>
    /// Builds a compare row from a text-diff line: Equal→Equal, Added→RightOnly, Deleted→LeftOnly,
    /// Modified→Different; 1-based line numbers become text, and the missing side keeps empty line
    /// numbers and text.
    /// </summary>
    /// <param name="line">文本差异行。<para>Text diff line.</para></param>
    /// <returns><para>渲染该行所需的对比行。</para> The compare row that renders the line.</returns>
    public static CompareRowViewModel ForText(TextDiffLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var status = line.Kind switch
        {
            DiffLineKind.Equal => FolderItemStatus.Equal,
            DiffLineKind.Added => FolderItemStatus.RightOnly,
            DiffLineKind.Deleted => FolderItemStatus.LeftOnly,
            DiffLineKind.Modified => FolderItemStatus.Different,
            _ => FolderItemStatus.Equal,
        };

        return new CompareRowViewModel(
            status,
            isDirectory: false,
            depth: 0,
            leftLine: line.LeftLine > 0 ? line.LeftLine.ToString() : string.Empty,
            leftText: line.LeftText,
            leftSub: string.Empty,
            isLeftDiff: line.Kind is DiffLineKind.Deleted or DiffLineKind.Modified,
            rightLine: line.RightLine > 0 ? line.RightLine.ToString() : string.Empty,
            rightText: line.RightText,
            rightSub: string.Empty,
            isRightDiff: line.Kind is DiffLineKind.Added or DiffLineKind.Modified);
    }

    /// <summary>
    /// <para>将字节数格式化为可读大小文本（如 "1.5 MB"）；0 字节显示 "0 B"。</para>
    /// Formats a byte count into a human-readable size string (e.g. "1.5 MB"); 0 bytes shows "0 B".
    /// </summary>
    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        var abs = Math.Abs(bytes);
        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var unit = 0;
        var value = (double)abs;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[0]}"
            : $"{value:0.##} {units[unit]}";
    }
}