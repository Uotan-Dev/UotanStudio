using System.Runtime.InteropServices;
using UotanStudio.DD.Drives;

namespace UotanStudio.DD.Writing.Linux;

/// <summary>
/// Writes directly to a Linux block device after unmounting its mounted partitions.
/// <para>卸载已挂载分区后直接写入 Linux 块设备。</para>
/// </summary>
internal sealed class LinuxDiskTarget : IDiskTarget
{
    private const int MntDetach = 2;
    private const int ErrorNoEntry = 2;
    private const int ErrorBusy = 16;
    private const int ErrorInvalidArgument = 22;

    private FileStream? _stream;

    /// <summary>
    /// Creates a target for one block device.
    /// <para>为一个块设备创建目标。</para>
    /// </summary>
    /// <param name="drive">Drive to write.<para>要写入的驱动器。</para></param>
    public LinuxDiskTarget(FlashDrive drive)
    {
        ArgumentNullException.ThrowIfNull(drive);
        Drive = drive;
    }

    /// <inheritdoc />
    public FlashDrive Drive { get; }

    /// <inheritdoc />
    public Stream Stream => _stream ?? throw new InvalidOperationException("The target has not been prepared.");

    /// <inheritdoc />
    public int ChunkAlignmentBytes => RawDiskChunkAlignment.Bytes;

    /// <inheritdoc />
    public Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Drive.IsSystem)
        {
            throw new FlashException($"Refusing to unmount the system drive {Drive.DisplayName} ({Drive.DevicePath}).");
        }

        if (Drive.IsReadOnly)
        {
            throw new FlashException($"The drive {Drive.DisplayName} ({Drive.DevicePath}) is read-only.");
        }

        foreach (var mountPoint in Drive.MountPoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Unmount(mountPoint);
        }

        try
        {
            _stream = new FileStream(
                Drive.DevicePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                4096,
                FileOptions.None);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new FlashAccessDeniedException(
                $"Linux could not open \"{Drive.DevicePath}\" for writing. Run with root privileges.",
                exception);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _stream?.Flush(flushToDisk: true);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _stream = null;
        return ValueTask.CompletedTask;
    }

    private static void Unmount(string mountPoint)
    {
        if (umount2(mountPoint, 0) == 0)
        {
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error is ErrorNoEntry or ErrorInvalidArgument)
        {
            return;
        }

        if (error == ErrorBusy && umount2(mountPoint, MntDetach) == 0)
        {
            return;
        }

        throw new FlashException(
            $"Linux could not unmount \"{mountPoint}\" (errno {error}). Detach it manually before flashing.");
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int umount2(string target, int flags);
}