namespace UotanStudio.Firmware.Core;

/// <summary>
/// Shared staging file for compressed payloads accepted during a parallel planning pass. Compression
/// stays parallel while appends serialize under the lock; the sequential write pass copies staged
/// bytes instead of re-compressing every cluster, so no payload is ever compressed twice. All appends
/// complete before the first read (planning finishes before layout allocation), so reads need no lock.
/// DeleteOnClose removes the scratch file even when the build fails between planning and writing.
/// <para>并行规划阶段接受的压缩负载共享暂存文件：压缩保持并行，追加在锁内串行；顺序写出阶段复制暂存
/// 字节而不是对每个负载重新压缩，因此任何负载都不会被压缩两次。全部追加发生在首次读取之前
/// （规划先于布局分配完成），因此读取无需加锁。DeleteOnClose 保证构建在规划与写出之间失败时也能清除暂存文件。</para>
/// </summary>
internal sealed class CompressionPayloadStaging : IDisposable
{
    private readonly FileStream _file = new(
        Path.Combine(Path.GetTempPath(), $"uotan-stage-{Guid.NewGuid():N}.tmp"),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
        FileOptions.DeleteOnClose | FileOptions.SequentialScan);
    private readonly Lock _appendLock = new();

    /// <summary>
    /// Appends one payload and returns the offset it was written at; the caller records the payload
    /// length separately.
    /// <para>追加一个负载并返回其写入偏移；负载长度由调用方另行记录。</para>
    /// </summary>
    public long Append(ReadOnlySpan<byte> payload)
    {
        lock (_appendLock)
        {
            var offset = _file.Length;
            _file.Write(payload);
            return offset;
        }
    }

    /// <summary>
    /// Reads a staged payload back into <paramref name="target"/>.
    /// <para>把一个暂存负载读回 <paramref name="target"/>。</para>
    /// </summary>
    public void Read(long offset, Span<byte> target)
    {
        _file.Seek(offset, SeekOrigin.Begin);
        _file.ReadExactly(target);
    }

    public void Dispose() => _file.Dispose();
}