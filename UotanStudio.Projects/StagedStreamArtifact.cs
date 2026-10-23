namespace UotanStudio.Projects;

/// <summary>
/// A <see cref="StreamArtifact"/> over a staging file this process created for one run. Disposing it deletes the
/// staging file, so a streamed node output never outlives the execution that produced it.
/// <para>指向本进程为单次运行创建的暂存文件的 <see cref="StreamArtifact"/>。释放它会删除暂存文件，因此流式节点产物不会活过产生它的那次执行。</para>
/// </summary>
public sealed class StagedStreamArtifact : StreamArtifact
{
    private readonly long _length;

    /// <summary>
    /// Initializes an artifact over a staging file.
    /// <para>初始化指向暂存文件的工件。</para>
    /// </summary>
    /// <param name="path">Path of the staging file this artifact owns. <para>本工件拥有的暂存文件路径。</para></param>
    /// <param name="length">Payload length in bytes recorded when the file was written.
    /// <para>写入文件时记录的载荷字节长度。</para></param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or whitespace.
    /// <para><paramref name="path"/> 为 <see langword="null"/> 或空白。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> is negative.
    /// <para><paramref name="length"/> 为负数。</para></exception>
    public StagedStreamArtifact(string path, long length)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Path = path;
        _length = length;
    }

    /// <summary>
    /// Gets the path of the staging file.
    /// <para>获取暂存文件的路径。</para>
    /// </summary>
    /// <value>The staging file path owned by this artifact. <para>本工件拥有的暂存文件路径。</para></value>
    public string Path { get; }

    /// <inheritdoc />
    public override long? Length => _length;

    /// <inheritdoc />
    public override string? DisplayName => System.IO.Path.GetFileName(Path);

    /// <inheritdoc />
    public override Stream OpenRead() => new FileStream(Path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.SequentialScan);

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }
}