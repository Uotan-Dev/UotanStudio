namespace UotanStudio.Services.Projects;

/// <summary>
/// A <see cref="StreamArtifact"/> whose payload already lives in memory. It adapts an existing byte buffer to the same
/// contract as a file-backed artifact, so a node never has to branch on where the payload happens to be.
/// <para>载荷已在内存中的 <see cref="StreamArtifact"/>。它把既有的字节缓冲适配到与文件承载工件相同的契约上，节点因此不必按载荷所在位置分支。</para>
/// </summary>
/// <remarks>
/// The buffer is not copied on construction: each opening reads the same immutable snapshot, so the caller must not
/// mutate the buffer afterwards. Large payloads belong in a <see cref="FileStreamArtifact"/> instead.
/// <para>构造时不复制缓冲：每次打开都读同一份不可变快照，因此调用方此后不得修改该缓冲。大载荷应改用 <see cref="FileStreamArtifact"/>。</para>
/// </remarks>
public sealed class MemoryStreamArtifact : StreamArtifact
{
    private readonly ReadOnlyMemory<byte> _payload;

    /// <summary>
    /// Initializes an artifact over an in-memory payload.
    /// <para>初始化以内存载荷为内容的工件。</para>
    /// </summary>
    /// <param name="payload">The immutable payload bytes. <para>不可变的载荷字节。</para></param>
    /// <param name="displayName">Optional caption shown by panels and logs. <para>面板与日志显示的可选标题。</para></param>
    public MemoryStreamArtifact(ReadOnlyMemory<byte> payload, string? displayName = null)
    {
        _payload = payload;
        DisplayName = displayName;
    }

    /// <inheritdoc />
    public override long? Length => _payload.Length;

    /// <inheritdoc />
    public override string? DisplayName { get; }

    /// <inheritdoc />
    public override Stream OpenRead() => new MemoryStream(_payload.ToArray(), writable: false);

    /// <inheritdoc />
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}