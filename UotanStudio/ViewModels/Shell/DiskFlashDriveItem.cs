using CommunityToolkit.Mvvm.ComponentModel;
using UotanStudio.DD.Drives;
using UotanStudio.DD.Images;
using UotanStudio.Services;

namespace UotanStudio.ViewModels.Shell;

/// <summary>
/// One selectable target drive row in the disk-image flashing window.
/// <para>磁盘镜像烧录窗口中的一个可选目标驱动器行。</para>
/// </summary>
public sealed partial class DiskFlashDriveItem : ObservableObject
{
    /// <summary>
    /// Creates a row for a scanned drive.
    /// <para>为已扫描的驱动器创建行。</para>
    /// </summary>
    /// <param name="drive">The scanned drive.<para>已扫描的驱动器。</para></param>
    public DiskFlashDriveItem(FlashDrive drive)
    {
        ArgumentNullException.ThrowIfNull(drive);
        Drive = drive;
        SizeText = drive.Size > 0
            ? ProgressMeter.FormatSize(drive.Size)
            : LanguageService.Instance.GetString("DiskFlash_SizeUnknown");
        KindText = drive.IsRemovable
            ? LanguageService.Instance.GetString("DiskFlash_DriveRemovable")
            : LanguageService.Instance.GetString("DiskFlash_DriveInternal");
    }

    /// <summary>
    /// Gets the underlying scanned drive.
    /// <para>获取底层的已扫描驱动器。</para>
    /// </summary>
    public FlashDrive Drive { get; }

    /// <summary>
    /// Gets the user-facing drive name.
    /// <para>获取面向用户的驱动器名称。</para>
    /// </summary>
    public string DisplayName => Drive.DisplayName;

    /// <summary>
    /// Gets the platform device path.
    /// <para>获取平台设备路径。</para>
    /// </summary>
    public string DevicePath => Drive.DevicePath;

    /// <summary>
    /// Gets the secondary description including the platform path and mount points when present.
    /// <para>获取副标题描述，包含平台路径并在存在时附带挂载点。</para>
    /// </summary>
    public string Subtitle => Drive.MountPoints.Count > 0
        ? $"{Drive.DevicePath} ({string.Join(", ", Drive.MountPoints.Select(m => m.TrimEnd('\\')))})"
        : Drive.DevicePath;

    /// <summary>
    /// Gets or sets whether the row is selected as a flash target.
    /// <para>获取或设置该行是否被选为烧录目标。</para>
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// Gets or sets whether the row cannot be selected with the current image and options.
    /// <para>获取或设置该行在当前镜像与选项下是否不可选。</para>
    /// </summary>
    [ObservableProperty]
    private bool _isBlocked;

    /// <summary>
    /// Gets or sets the formatted capacity text.
    /// <para>获取或设置格式化后的容量文本。</para>
    /// </summary>
    [ObservableProperty]
    private string _sizeText;

    /// <summary>
    /// Gets or sets the removable or internal label.
    /// <para>获取或设置可移动或内置标签。</para>
    /// </summary>
    [ObservableProperty]
    private string _kindText;

    /// <summary>
    /// Gets or sets the joined compatibility findings shown below the row.
    /// <para>获取或设置显示在行下方的兼容性发现文本。</para>
    /// </summary>
    [ObservableProperty]
    private string _issueText = string.Empty;

    /// <summary>
    /// Re-evaluates the row against the current image and options.
    /// <para>按当前镜像与选项重新评估该行。</para>
    /// </summary>
    /// <param name="image">The probed image, or <see langword="null"/> when none is selected.
    /// <para>已探测的镜像；未选择镜像时为 <see langword="null"/>。</para></param>
    /// <param name="allowLargeDrive">Whether large drives were explicitly allowed.
    /// <para>是否已显式允许大容量驱动器。</para></param>
    internal void Refresh(FlashImage? image, bool allowLargeDrive)
    {
        var issues = DriveCompatibilityEvaluator.Evaluate(Drive, image, writing: true);
        IsBlocked = issues.Any(issue => DriveCompatibilityEvaluator.IsBlocking(issue, allowLargeDrive));
        IssueText = string.Join(" · ", issues.Select(issue => IssueLabel(issue.Issue)).Distinct(StringComparer.Ordinal));
        if (IsBlocked)
        {
            IsSelected = false;
        }
    }

    /// <summary>
    /// Resolves the localized label of a compatibility finding.
    /// <para>解析兼容性发现的本地化标签。</para>
    /// </summary>
    /// <param name="issue">The finding to label.<para>待标注的发现。</para></param>
    /// <returns>The localized label.<para>本地化标签。</para></returns>
    internal static string IssueLabel(DriveCompatibilityIssue issue) => issue switch
    {
        DriveCompatibilityIssue.Locked => LanguageService.Instance.GetString("DiskFlash_IssueLocked"),
        DriveCompatibilityIssue.SystemDrive => LanguageService.Instance.GetString("DiskFlash_IssueSystemDrive"),
        DriveCompatibilityIssue.ContainsImage => LanguageService.Instance.GetString("DiskFlash_IssueContainsImage"),
        DriveCompatibilityIssue.LargeDrive => LanguageService.Instance.GetString("DiskFlash_IssueLargeDrive"),
        DriveCompatibilityIssue.TooSmall => LanguageService.Instance.GetString("DiskFlash_IssueTooSmall"),
        _ => issue.ToString(),
    };
}