using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UotanStudio.DD.Drives.Windows;

/// <summary>
/// Minimal Win32 device helpers shared by the Windows scanner and writer.
/// <para>Windows 扫描器与写入器共用的最小 Win32 设备辅助方法。</para>
/// </summary>
internal static class WindowsDeviceApi
{
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileReadAttributes = 0x00000080;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint OpenExisting = 3;
    internal const uint FileAttributeNormal = 0x00000080;
    internal const uint DriveRemovable = 2;

    internal const uint IoctlVolumeGetDiskExtents = 0x00560000;
    internal const uint IoctlDiskGetLengthInfo = 0x0007405C;
    internal const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
    internal const uint IoctlDiskIsWritable = 0x00070024;
    internal const uint IoctlStorageQueryProperty = 0x002D1400;
    internal const uint IoctlStorageMediaRemoval = 0x00202004;
    internal const uint FsctlLockVolume = 0x00090018;
    internal const uint FsctlDismountVolume = 0x00090020;

    /// <summary>
    /// Byte offset of <c>DiskSize</c> inside a <c>DISK_GEOMETRY_EX</c> reply: a 24-byte
    /// <c>DISK_GEOMETRY</c> followed by the 8-byte logical size.
    /// <para><c>DISK_GEOMETRY_EX</c> 回复中 <c>DiskSize</c> 的字节偏移：先是 24 字节的
    /// <c>DISK_GEOMETRY</c>，随后是 8 字节的逻辑容量。</para>
    /// </summary>
    private const int DiskGeometryExSizeOffset = 24;

    /// <summary>
    /// Buffer size that always covers the fixed part of a <c>DISK_GEOMETRY_EX</c> reply, whose trailing
    /// partition entries are not needed.
    /// <para>足以覆盖 <c>DISK_GEOMETRY_EX</c> 固定部分的缓冲区大小；其尾部的分区条目并不需要。</para>
    /// </summary>
    private const int GeometryExtendedBufferBytes = DiskGeometryExSizeOffset + 8 + 128;

    private const int ErrorMoreData = 234;
    private const int ErrorWriteProtect = 19;
    private const int StorageDeviceProperty = 0;
    private const int PropertyStandardQuery = 0;

    /// <summary>
    /// Opens a device path with the requested access.
    /// <para>以请求的访问权限打开设备路径。</para>
    /// </summary>
    /// <param name="path">Device or volume path.<para>设备或卷路径。</para></param>
    /// <param name="desiredAccess">Win32 access mask.<para>Win32 访问掩码。</para></param>
    /// <returns>The handle, or <see langword="null"/> when Windows rejected the open.
    /// <para>句柄；Windows 拒绝打开时为 <see langword="null"/>。</para></returns>
    internal static SafeFileHandle? TryOpen(string path, uint desiredAccess)
        => TryOpen(path, desiredAccess, out _);

    /// <summary>
    /// Opens a device path with the requested access and reports the Win32 error when the open failed.
    /// <para>以请求的访问权限打开设备路径，并在打开失败时报告 Win32 错误。</para>
    /// </summary>
    /// <param name="path">Device or volume path.<para>设备或卷路径。</para></param>
    /// <param name="desiredAccess">Win32 access mask.<para>Win32 访问掩码。</para></param>
    /// <param name="errorCode">The Win32 error of the failed call, or zero on success.
    /// <para>调用失败时的 Win32 错误；成功时为零。</para></param>
    /// <returns>The handle, or <see langword="null"/> when Windows rejected the open.
    /// <para>句柄；Windows 拒绝打开时为 <see langword="null"/>。</para></returns>
    internal static SafeFileHandle? TryOpen(string path, uint desiredAccess, out int errorCode)
    {
        var raw = CreateFileW(
            path,
            desiredAccess,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal,
            IntPtr.Zero);
        if (raw.ToInt64() == -1 || raw == IntPtr.Zero)
        {
            errorCode = Marshal.GetLastPInvokeError();
            return null;
        }

        errorCode = 0;
        return new SafeFileHandle(raw, ownsHandle: true);
    }

    /// <summary>
    /// Gets the Win32 error for the most recent failed call.
    /// <para>获取最近一次失败调用的 Win32 错误。</para>
    /// </summary>
    /// <returns>A <see cref="Win32Exception"/> carrying the current error code.
    /// <para>携带当前错误码的 <see cref="Win32Exception"/>。</para></returns>
    internal static Win32Exception LastError() => new(Marshal.GetLastPInvokeError());

    /// <summary>
    /// Enumerates the mount points attached to one volume.
    /// <para>枚举附加到一个卷的挂载点。</para>
    /// </summary>
    /// <param name="volumeName">Volume GUID path with a trailing separator.
    /// <para>带尾分隔符的卷 GUID 路径。</para></param>
    /// <returns>Mount points such as <c>C:\</c>, or an empty list.
    /// <para>形如 <c>C:\</c> 的挂载点，或空列表。</para></returns>
    internal static IReadOnlyList<string> GetVolumeMountPoints(string volumeName)
    {
        var buffer = new char[512];
        while (true)
        {
            if (GetVolumePathNamesForVolumeNameW(volumeName, buffer, (uint)buffer.Length, out var required))
            {
                var paths = new List<string>();
                var index = 0;
                while (index < buffer.Length)
                {
                    var end = Array.IndexOf(buffer, '\0', index);
                    if (end < 0 || end == index)
                    {
                        break;
                    }

                    paths.Add(new string(buffer, index, end - index));
                    index = end + 1;
                }

                return paths;
            }

            if (Marshal.GetLastPInvokeError() != ErrorMoreData || required <= buffer.Length)
            {
                return [];
            }

            buffer = new char[required];
        }
    }

    /// <summary>
    /// Resolves the physical disk number that backs a volume.
    /// <para>解析卷背后的物理磁盘编号。</para>
    /// </summary>
    /// <param name="volumeName">Volume GUID path with a trailing separator.
    /// <para>带尾分隔符的卷 GUID 路径。</para></param>
    /// <returns>The disk number, or <see langword="null"/> when it cannot be queried.
    /// <para>磁盘编号；无法查询时为 <see langword="null"/>。</para></returns>
    internal static int? TryGetDiskNumber(string volumeName)
    {
        using var handle = TryOpen(volumeName, FileReadAttributes)
            ?? TryOpen(volumeName.TrimEnd('\\'), FileReadAttributes)
            ?? TryOpen(volumeName, 0)
            ?? TryOpen(volumeName.TrimEnd('\\'), 0);
        if (handle is null)
        {
            return null;
        }

        var buffer = new byte[8 + 24 * 4];
        if (!DeviceIoControl(handle, IoctlVolumeGetDiskExtents, null, 0, buffer, (uint)buffer.Length, out var returned, IntPtr.Zero)
            || returned < 8)
        {
            return null;
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        if (count <= 0)
        {
            return null;
        }

        return BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(8));
    }

    /// <summary>
    /// Reads the physical capacity of a disk.
    /// <para>读取磁盘的物理容量。</para>
    /// </summary>
    /// <param name="devicePath">Path such as <c>\\.\PhysicalDrive2</c>.<para>形如 <c>\\.\PhysicalDrive2</c> 的路径。</para></param>
    /// <returns>The capacity in bytes, or <see langword="null"/> when it cannot be queried.
    /// <para>容量（字节）；无法查询时为 <see langword="null"/>。</para></returns>
    /// <remarks>
    /// <c>IOCTL_DISK_GET_LENGTH_INFO</c> is refused with <c>ERROR_ACCESS_DENIED</c> whenever the process does
    /// not hold a token that may write the disk, so the geometry-extended query is used as the fallback; it
    /// reports the same physical length to an ordinary user.
    /// <para>
    /// 当进程不持有可写该磁盘的令牌时，<c>IOCTL_DISK_GET_LENGTH_INFO</c> 会以
    /// <c>ERROR_ACCESS_DENIED</c> 被拒绝；因此回退到几何扩展查询，普通用户用它也能读到相同的物理容量。
    /// </para>
    /// </remarks>
    internal static long? TryGetDiskLength(string devicePath)
    {
        using var handle = TryOpen(devicePath, FileReadAttributes);
        if (handle is null)
        {
            return null;
        }

        var buffer = new byte[8];
        if (DeviceIoControl(handle, IoctlDiskGetLengthInfo, null, 0, buffer, (uint)buffer.Length, out var returned, IntPtr.Zero)
            && returned >= 8)
        {
            return BinaryPrimitives.ReadInt64LittleEndian(buffer);
        }

        var geometry = new byte[GeometryExtendedBufferBytes];
        return DeviceIoControl(handle, IoctlDiskGetDriveGeometryEx, null, 0, geometry, (uint)geometry.Length, out returned, IntPtr.Zero)
            && returned >= DiskGeometryExSizeOffset + sizeof(long)
            ? BinaryPrimitives.ReadInt64LittleEndian(geometry.AsSpan(DiskGeometryExSizeOffset))
            : null;
    }

    /// <summary>
    /// Queries the storage descriptor for a disk.
    /// <para>查询磁盘的存储描述符。</para>
    /// </summary>
    /// <param name="devicePath">Path such as <c>\\.\PhysicalDrive2</c>.<para>形如 <c>\\.\PhysicalDrive2</c> 的路径。</para></param>
    /// <returns>Removable flag and vendor/product description, or <see langword="null"/> when unavailable.
    /// <para>可移动标志与厂商/产品描述；不可用时为 <see langword="null"/>。</para></returns>
    internal static (bool Removable, string Description)? TryGetStorageDescriptor(string devicePath)
    {
        using var handle = TryOpen(devicePath, FileReadAttributes);
        if (handle is null)
        {
            return null;
        }

        var query = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(query, StorageDeviceProperty);
        BinaryPrimitives.WriteInt32LittleEndian(query.AsSpan(4), PropertyStandardQuery);

        var output = new byte[1024];
        if (!DeviceIoControl(handle, IoctlStorageQueryProperty, query, (uint)query.Length, output, (uint)output.Length, out var returned, IntPtr.Zero)
            || returned < 32)
        {
            return null;
        }

        var descriptorSize = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(4));
        var limit = Math.Min(descriptorSize, (int)returned);
        var removable = output[10] != 0;
        var vendor = ReadAnsi(output, BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(12)), limit);
        var product = ReadAnsi(output, BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(16)), limit);
        var description = string.Join(' ', new[] { vendor, product }.Where(part => part.Length > 0));
        return (removable, description);
    }

    /// <summary>
    /// Queries whether a disk accepts writes.
    /// <para>查询磁盘是否接受写入。</para>
    /// </summary>
    /// <param name="devicePath">Path such as <c>\\.\PhysicalDrive2</c>.<para>形如 <c>\\.\PhysicalDrive2</c> 的路径。</para></param>
    /// <returns><see langword="true"/> or <see langword="false"/> when known, otherwise <see langword="null"/>.
    /// <para>已知时返回 <see langword="true"/> 或 <see langword="false"/>，否则返回 <see langword="null"/>。</para></returns>
    internal static bool? TryGetWritable(string devicePath)
    {
        using var handle = TryOpen(devicePath, FileReadAttributes);
        if (handle is null)
        {
            return null;
        }

        if (DeviceIoControl(handle, IoctlDiskIsWritable, null, 0, null, 0, out _, IntPtr.Zero))
        {
            return true;
        }

        return Marshal.GetLastPInvokeError() == ErrorWriteProtect ? false : null;
    }

    /// <summary>
    /// Locks and dismounts a mounted volume so its sectors can be overwritten safely.
    /// <para>锁定并卸载已挂载卷，以便安全覆盖其扇区。</para>
    /// </summary>
    /// <param name="root">Drive root such as <c>C:\</c>.<para>形如 <c>C:\</c> 的驱动器根路径。</para></param>
    /// <returns>The open volume handle that must stay alive until writing finishes.
    /// <para>必须保持打开直到写入完成的卷句柄。</para></returns>
    /// <exception cref="FlashException">The volume could not be locked or dismounted.
    /// <para>卷无法锁定或卸载。</para></exception>
    internal static SafeFileHandle LockAndDismountVolume(string root)
    {
        var handle = TryOpen(root, GenericRead | GenericWrite);
        if (handle is null)
        {
            throw new FlashException($"Windows could not open the volume {root} for exclusive writing.", LastError());
        }

        try
        {
            if (!DeviceIoControl(handle, FsctlLockVolume, null, 0, null, 0, out _, IntPtr.Zero))
            {
                throw new FlashException($"Windows could not lock the volume {root}.", LastError());
            }

            if (!DeviceIoControl(handle, FsctlDismountVolume, null, 0, null, 0, out _, IntPtr.Zero))
            {
                throw new FlashException($"Windows could not dismount the volume {root}.", LastError());
            }

            byte[] preventRemoval = [1];
            DeviceIoControl(handle, IoctlStorageMediaRemoval, preventRemoval, 1, null, 0, out _, IntPtr.Zero);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Re-enables media removal before a volume handle is released.
    /// <para>在释放卷句柄之前重新允许介质移除。</para>
    /// </summary>
    /// <param name="handle">Handle returned by <see cref="LockAndDismountVolume"/>.
    /// <para>由 <see cref="LockAndDismountVolume"/> 返回的句柄。</para></param>
    internal static void AllowMediaRemoval(SafeFileHandle handle)
    {
        byte[] preventRemoval = [0];
        DeviceIoControl(handle, IoctlStorageMediaRemoval, preventRemoval, 1, null, 0, out _, IntPtr.Zero);
    }

    /// <summary>
    /// Reads the total capacity reported by a mounted volume.
    /// <para>读取已挂载卷上报的总容量。</para>
    /// </summary>
    /// <param name="root">Drive root such as <c>C:\</c>.<para>形如 <c>C:\</c> 的驱动器根路径。</para></param>
    /// <returns>The capacity in bytes, or zero when it cannot be queried.
    /// <para>容量（字节）；无法查询时为零。</para></returns>
    internal static long GetVolumeCapacity(string root)
    {
        return GetDiskFreeSpaceExW(root, out _, out var totalBytes, out _) ? checked((long)totalBytes) : 0;
    }

    /// <summary>
    /// Gets the Win32 drive type for a root path.
    /// <para>获取根路径的 Win32 驱动器类型。</para>
    /// </summary>
    /// <param name="root">Drive root such as <c>C:\</c>.<para>形如 <c>C:\</c> 的驱动器根路径。</para></param>
    /// <returns>A <c>DRIVE_*</c> value.<para>一个 <c>DRIVE_*</c> 值。</para></returns>
    internal static uint GetDriveType(string root) => GetDriveTypeW(root);

    private static string ReadAnsi(byte[] buffer, int offset, int limit)
    {
        if (offset <= 0 || offset >= limit)
        {
            return string.Empty;
        }

        var end = Array.IndexOf(buffer, (byte)0, offset, limit - offset);
        if (end < 0)
        {
            end = limit;
        }

        return Encoding.ASCII.GetString(buffer, offset, end - offset).Trim();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstVolumeW(StringBuilder volumeName, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextVolumeW(IntPtr findHandle, StringBuilder volumeName, uint bufferLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindVolumeClose(IntPtr findHandle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumePathNamesForVolumeNameW(
        string volumeName,
        [Out] char[] buffer,
        uint bufferLength,
        out uint returnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        byte[]? inBuffer,
        uint inBufferSize,
        byte[]? outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetDriveTypeW(string rootPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceExW(
        string directoryName,
        out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    internal static IntPtr FindFirstVolume(StringBuilder volumeName, uint bufferLength) =>
        FindFirstVolumeW(volumeName, bufferLength);

    internal static bool FindNextVolume(IntPtr findHandle, StringBuilder volumeName, uint bufferLength) =>
        FindNextVolumeW(findHandle, volumeName, bufferLength);

    internal static void CloseVolumeEnumeration(IntPtr findHandle) => FindVolumeClose(findHandle);
}