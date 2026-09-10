using CommunityToolkit.Mvvm.ComponentModel;

namespace UotanStudio.ViewModels.Workspace;

/// <summary>
/// <para>镜像条目视图模型，承载镜像的显示名称、项目内相对路径、大小、格式与选中状态。</para>
/// Image entry view model, carrying the image's display name, project-relative path, size, format and selection state.
/// </summary>
public sealed partial class ImageEntryViewModel : ObservableObject
{
    /// <summary>
    /// <para>获取或设置显示名称。</para>
    /// Gets or sets the display name.
    /// </summary>
    [ObservableProperty]
    private string _displayName = string.Empty;

    /// <summary>
    /// <para>获取或设置相对工程根目录的镜像路径。</para>
    /// Gets or sets the image path relative to the project root directory.
    /// </summary>
    [ObservableProperty]
    private string _relativePath = string.Empty;

    /// <summary>
    /// <para>获取或设置镜像文件大小（字节）。</para>
    /// Gets or sets the image file size in bytes.
    /// </summary>
    [ObservableProperty]
    private long _sizeBytes;

    /// <summary>
    /// <para>获取自动换算到最大合适单位的文件大小。</para>
    /// Gets the file size converted to the largest suitable unit.
    /// </summary>
    public string DisplaySize => FormatSize(SizeBytes);

    partial void OnSizeBytesChanged(long value) => OnPropertyChanged(nameof(DisplaySize));

    /// <summary>
    /// <para>获取或设置镜像格式标识（boot/payload/cpio/oppo/sparse/raw/unknown）。</para>
    /// Gets or sets the image format identifier (boot/payload/cpio/oppo/sparse/raw/unknown).
    /// </summary>
    [ObservableProperty]
    private string _format = "unknown";

    /// <summary>
    /// <para>获取或设置是否加入批量操作选择集。</para>
    /// Gets or sets whether the entry is included in the batch-operation selection.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// <para>获取或设置此条目是否通过当前筛选条件（供 DataGrid 行可见性绑定）。</para>
    /// Gets or sets whether this entry passes the current filter (bound to DataGrid row visibility).
    /// </summary>
    [ObservableProperty]
    private bool _isVisible = true;

    /// <summary>
    /// <para>获取或设置此镜像是否已解包（子项目根目录下存在同名目录）。</para>
    /// Gets or sets whether this image has been unpacked (a same-name directory exists at the sub-project root).
    /// </summary>
    [ObservableProperty]
    private bool _isUnpacked;

    /// <summary>
    /// <para>获取或设置此条目是否为目录（打包模式下的解包产物文件夹）。</para>
    /// Gets or sets whether this entry is a directory (unpacked result folder in pack mode).
    /// </summary>
    [ObservableProperty]
    private bool _isDirectory;

    private static string FormatSize(long bytes)
    {
        ReadOnlySpan<string> units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
        var size = (double)Math.Max(0, bytes);
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024d;
            unitIndex++;
        }

        return $"{size:0.##} {units[unitIndex]}";
    }
}
