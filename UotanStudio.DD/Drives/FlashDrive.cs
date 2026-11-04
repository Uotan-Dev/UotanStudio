namespace UotanStudio.DD.Drives;

/// <summary>
/// A physical disk that can receive a disk image.
/// <para>可接收磁盘镜像的物理磁盘。</para>
/// </summary>
public sealed record FlashDrive
{
    /// <summary>
    /// Gets the platform path used to open the raw device for writing.
    /// <para>获取用于以写方式打开裸设备的平台路径。</para>
    /// </summary>
    /// <value>Examples: <c>\\.\PhysicalDrive2</c>, <c>/dev/mmcblk0</c>, <c>/dev/rdisk4</c>.
    /// <para>示例：<c>\\.\PhysicalDrive2</c>、<c>/dev/mmcblk0</c>、<c>/dev/rdisk4</c>。</para></value>
    public required string DevicePath { get; init; }

    /// <summary>
    /// Gets the user-facing drive name.
    /// <para>获取面向用户的驱动器名称。</para>
    /// </summary>
    /// <value>For example <c>SD Card Reader</c> or <c>/dev/mmcblk0</c>.
    /// <para>例如 <c>SD Card Reader</c> 或 <c>/dev/mmcblk0</c>。</para></value>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Gets the physical capacity in bytes, or zero when the platform cannot report it.
    /// <para>获取物理容量（字节）；平台无法上报时为零。</para>
    /// </summary>
    public long Size { get; init; }

    /// <summary>
    /// Gets whether the platform reports removable media.
    /// <para>获取平台是否报告该介质可移动。</para>
    /// </summary>
    public bool IsRemovable { get; init; }

    /// <summary>
    /// Gets whether the device is write-protected.
    /// <para>获取设备是否处于写保护状态。</para>
    /// </summary>
    public bool IsReadOnly { get; init; }

    /// <summary>
    /// Gets whether the drive hosts the running operating system.
    /// <para>获取该驱动器是否承载当前运行的操作系统。</para>
    /// </summary>
    public bool IsSystem { get; init; }

    /// <summary>
    /// Gets an optional human-readable description such as vendor and model.
    /// <para>获取可选的易读描述，例如厂商与型号。</para>
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Gets the mount points currently attached to this drive.
    /// <para>获取当前挂载到该驱动器的挂载点。</para>
    /// </summary>
    /// <value>Windows uses <c>C:\</c> style roots, Unix uses paths such as <c>/media/card</c>.
    /// <para>Windows 使用 <c>C:\</c> 形式的根路径，Unix 使用 <c>/media/card</c> 等路径。</para></value>
    public IReadOnlyList<string> MountPoints { get; init; } = [];
}