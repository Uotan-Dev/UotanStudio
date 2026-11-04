using UotanStudio.DD.Drives;

namespace UotanStudio.DD.Writing;

/// <summary>
/// Factory that selects the raw-device implementation for the current operating system.
/// <para>为当前操作系统选择裸设备实现的目标工厂。</para>
/// </summary>
public static class PlatformDiskTargetFactory
{
    /// <summary>
    /// Gets the shared factory instance.
    /// <para>获取共享的工厂实例。</para>
    /// </summary>
    public static IDiskTargetFactory Instance { get; } = new DefaultFactory();

    private sealed class DefaultFactory : IDiskTargetFactory
    {
        public IDiskTarget Create(FlashDrive drive)
        {
            ArgumentNullException.ThrowIfNull(drive);

            if (OperatingSystem.IsWindows())
            {
                return new Windows.WindowsDiskTarget(drive);
            }

            if (OperatingSystem.IsLinux())
            {
                return new Linux.LinuxDiskTarget(drive);
            }

            if (OperatingSystem.IsMacOS())
            {
                return new MacOS.MacDiskTarget(drive);
            }

            throw new PlatformNotSupportedException("Disk writing is supported on Windows, Linux and macOS only.");
        }
    }
}