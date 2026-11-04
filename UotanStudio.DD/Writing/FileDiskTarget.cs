using UotanStudio.DD.Drives;

namespace UotanStudio.DD.Writing;

/// <summary>
/// Writes an image into a regular file, which is useful for tests and for producing image files.
/// <para>把镜像写入普通文件，适用于测试与生成镜像文件。</para>
/// </summary>
public sealed class FileDiskTarget : IDiskTarget
{
    private readonly string _path;
    private FileStream? _stream;

    /// <summary>
    /// Creates a file-backed target.
    /// <para>创建由文件承载的目标。</para>
    /// </summary>
    /// <param name="drive">Drive metadata used for validation and progress.<para>用于校验与进度的驱动器元数据。</para></param>
    /// <param name="path">File that receives the bytes; it is created or truncated on prepare.
    /// <para>接收字节的文件；准备阶段会创建或截断该文件。</para></param>
    public FileDiskTarget(FlashDrive drive, string path)
    {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Drive = drive;
        _path = Path.GetFullPath(path);
    }

    /// <inheritdoc />
    public FlashDrive Drive { get; }

    /// <inheritdoc />
    public Stream Stream => _stream ?? throw new InvalidOperationException("The target has not been prepared.");

    /// <inheritdoc />
    public Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(_path);
        if (directory is { Length: > 0 })
        {
            Directory.CreateDirectory(directory);
        }

        _stream = new FileStream(_path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.None);
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
}

/// <summary>
/// Creates <see cref="FileDiskTarget"/> instances whose file is the drive's <see cref="FlashDrive.DevicePath"/>.
/// <para>创建 <see cref="FileDiskTarget"/> 实例，其文件为驱动器的 <see cref="FlashDrive.DevicePath"/>。</para>
/// </summary>
public sealed class FileDiskTargetFactory : IDiskTargetFactory
{
    /// <summary>
    /// Gets the shared factory instance.
    /// <para>获取共享的工厂实例。</para>
    /// </summary>
    public static FileDiskTargetFactory Instance { get; } = new();

    /// <inheritdoc />
    public IDiskTarget Create(FlashDrive drive)
    {
        ArgumentNullException.ThrowIfNull(drive);
        return new FileDiskTarget(drive, drive.DevicePath);
    }
}