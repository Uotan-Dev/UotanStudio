using UotanStudio.DD.Drives;
using UotanStudio.DD.Drives.MacOS;

namespace UotanStudio.DD.Writing.MacOS;

/// <summary>
/// Writes directly to a macOS raw disk device after unmounting it with <c>diskutil</c>.
/// <para>使用 <c>diskutil</c> 卸载后直接写入 macOS 裸磁盘设备。</para>
/// </summary>
internal sealed class MacDiskTarget : IDiskTarget
{
    private const string DiskUtilPath = "/usr/sbin/diskutil";

    private FileStream? _stream;

    /// <summary>
    /// Creates a target for one macOS disk.
    /// <para>为一个 macOS 磁盘创建目标。</para>
    /// </summary>
    /// <param name="drive">Drive to write.<para>要写入的驱动器。</para></param>
    public MacDiskTarget(FlashDrive drive)
    {
        ArgumentNullException.ThrowIfNull(drive);
        Drive = drive;
    }

    /// <inheritdoc />
    public FlashDrive Drive { get; }

    /// <inheritdoc />
    public Stream Stream => _stream ?? throw new InvalidOperationException("The target has not been prepared.");

    /// <inheritdoc />
    public async Task PrepareAsync(CancellationToken cancellationToken = default)
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

        var identifier = GetDiskIdentifier(Drive.DevicePath);
        await MacCommand
            .RunAsync(DiskUtilPath, ["unmountDisk", identifier], cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

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
                $"macOS could not open \"{Drive.DevicePath}\" for writing. Run with root privileges.",
                exception);
        }
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

    private static string GetDiskIdentifier(string devicePath)
    {
        var name = Path.GetFileName(devicePath);
        return name.StartsWith("rdisk", StringComparison.OrdinalIgnoreCase) ? name[1..] : name;
    }
}