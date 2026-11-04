using Microsoft.Win32.SafeHandles;
using UotanStudio.DD.Drives;
using UotanStudio.DD.Drives.Windows;

namespace UotanStudio.DD.Writing.Windows;

/// <summary>
/// Writes directly to a Windows physical disk after locking and dismounting its volumes.
/// <para>锁定并卸载卷后直接写入 Windows 物理磁盘。</para>
/// </summary>
internal sealed class WindowsDiskTarget : IDiskTarget
{
    private readonly List<SafeFileHandle> _volumeHandles = [];
    private FileStream? _stream;

    /// <summary>
    /// Creates a target for one physical disk.
    /// <para>为一个物理磁盘创建目标。</para>
    /// </summary>
    /// <param name="drive">Drive to write.<para>要写入的驱动器。</para></param>
    public WindowsDiskTarget(FlashDrive drive)
    {
        ArgumentNullException.ThrowIfNull(drive);
        Drive = drive;
    }

    /// <inheritdoc />
    public FlashDrive Drive { get; }

    /// <inheritdoc />
    public Stream Stream => _stream ?? throw new InvalidOperationException("The target has not been prepared.");

    /// <inheritdoc />
    public Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Drive.IsSystem)
        {
            throw new FlashException($"Refusing to dismount the system drive {Drive.DisplayName} ({Drive.DevicePath}).");
        }

        if (Drive.IsReadOnly)
        {
            throw new FlashException($"The drive {Drive.DisplayName} ({Drive.DevicePath}) is write-protected.");
        }

        foreach (var mountPoint in Drive.MountPoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGetDriveRoot(mountPoint, out var root))
            {
                throw new FlashException(
                    $"The volume mount point \"{mountPoint}\" cannot be locked automatically. Unmount it before flashing.");
            }

            _volumeHandles.Add(WindowsDeviceApi.LockAndDismountVolume(root));
        }

        var handle = WindowsDeviceApi.TryOpen(Drive.DevicePath, WindowsDeviceApi.GenericRead | WindowsDeviceApi.GenericWrite);
        if (handle is null)
        {
            throw new FlashException(
                $"Windows could not open \"{Drive.DevicePath}\" for writing. Run with administrator rights.",
                WindowsDeviceApi.LastError());
        }

        _stream = new FileStream(handle, FileAccess.ReadWrite, 4096, isAsync: false);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_stream is null)
        {
            return Task.CompletedTask;
        }

        _stream.Flush(flushToDisk: true);
        foreach (var handle in _volumeHandles)
        {
            WindowsDeviceApi.AllowMediaRemoval(handle);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _stream = null;
        foreach (var handle in _volumeHandles)
        {
            handle.Dispose();
        }

        _volumeHandles.Clear();
        return ValueTask.CompletedTask;
    }

    private static bool TryGetDriveRoot(string mountPoint, out string root)
    {
        var trimmed = mountPoint.TrimEnd('\\', '/');
        if (trimmed.Length == 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':')
        {
            root = @"\\.\" + trimmed;
            return true;
        }

        root = string.Empty;
        return false;
    }
}