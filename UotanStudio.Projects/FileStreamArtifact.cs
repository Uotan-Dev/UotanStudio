namespace UotanStudio.Services.Projects;

/// <summary>
/// A <see cref="StreamArtifact"/> whose payload is a file the producing node already wrote. Opening the artifact never
/// copies, caches or buffers the file, so the peak memory of a consumer stays bounded by its own buffer.
/// <para>载荷为产生方已写好的文件的 <see cref="StreamArtifact"/>。打开它从不复制、缓存或缓冲该文件，因此消费方的峰值内存只受其自身缓冲区约束。</para>
/// </summary>
/// <remarks>
/// The file is opened with shared read access, so the producing execution may keep it open. A consumer that needs a
/// stable snapshot must copy into its own staging area; the artifact itself promises only that each opening starts at
/// the first byte.
/// <para>文件以共享读打开，产生方可以继续持有它。需要稳定快照的消费方必须自行复制到暂存区；工件本身只承诺每次打开都从首字节开始。</para>
/// </remarks>
public sealed class FileStreamArtifact : StreamArtifact
{
    /// <summary>
    /// Initializes an artifact over one file.
    /// <para>初始化指向单个文件的工件。</para>
    /// </summary>
    /// <param name="path">Path of the payload file. <para>载荷文件路径。</para></param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or whitespace.
    /// <para><paramref name="path"/> 为 <see langword="null"/> 或空白。</para></exception>
    public FileStreamArtifact(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
    }

    /// <summary>
    /// Gets the path of the payload file.
    /// <para>获取载荷文件的路径。</para>
    /// </summary>
    /// <value>The absolute or relative path recorded by the producer. <para>产生方记录的绝对或相对路径。</para></value>
    public string Path { get; }

    /// <inheritdoc />
    public override long? Length
    {
        get
        {
            try
            {
                return new FileInfo(Path).Length;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <inheritdoc />
    public override string? DisplayName => System.IO.Path.GetFileName(Path);

    /// <inheritdoc />
    public override Stream OpenRead() => new FileStream(Path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.SequentialScan);

    /// <inheritdoc />
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}