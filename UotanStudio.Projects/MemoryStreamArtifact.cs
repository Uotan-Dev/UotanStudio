using System.Runtime.InteropServices;

namespace UotanStudio.Projects;

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
    /// <remarks>
    /// The returned stream wraps the payload instead of copying it, and it is created with
    /// <c>publiclyVisible: false</c> so the underlying array is never handed to the caller; readers can only observe
    /// the same immutable snapshot the artifact was constructed with. A payload that does not expose a whole array
    /// (for example one produced by slicing a larger buffer through a custom memory manager) falls back to a copy.
    /// <para>返回的流包裹载荷而不复制，并以 <c>publiclyVisible: false</c> 创建，底层数组因此从不交给调用方；读取者只能
    /// 观察到构造时所依据的同一份不可变快照。若载荷不暴露为整块数组（例如经自定义内存管理器从更大缓冲切出），则回退为复制。</para>
    /// </remarks>
    public override Stream OpenRead()
    {
        if (MemoryMarshal.TryGetArray(_payload, out var segment) && segment.Array is { } array)
        {
            return new MemoryStream(array, segment.Offset, segment.Count, writable: false, publiclyVisible: false);
        }

        return new MemoryStream(_payload.ToArray(), writable: false);
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}