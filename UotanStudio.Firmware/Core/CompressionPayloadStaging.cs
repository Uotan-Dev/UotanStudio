using System.Buffers;

namespace UotanStudio.Firmware.Core;

/// <summary>
/// Shared staging area for compressed payloads accepted during a parallel planning pass. Accepted frames
/// stay in a pooled, chunked memory arena while their total stays inside one aggregate memory budget and
/// spill to a temporary file once that budget is exceeded, so a small image never creates one and a
/// large one keeps the bounded file layout. Compression stays parallel while appends serialize under the
/// lock; the sequential write pass copies staged bytes instead of re-compressing every cluster, so no
/// payload is ever compressed twice. All appends complete before the first read (planning finishes before
/// layout allocation), so reads need no lock. Both forms address the same bytes at the same offsets, which
/// is what makes the spill invisible to callers and the emitted image independent of it.
/// <para>并行规划阶段接受的压缩负载共享暂存区：接受帧先在按块池化的内存区中保留，总量不超过单一聚合内存预算，
/// 超出后才落到临时文件，因此小镜像完全不碰磁盘，大镜像仍保持有界的文件布局。压缩保持并行，追加在锁内串行；
/// 顺序写出阶段复制暂存字节而不是对每个负载重新压缩，因此任何负载都不会被压缩两次。全部追加发生在首次读取之前
/// （规划先于布局分配完成），因此读取无需加锁。两种形态在相同偏移上覆盖相同的字节，因此落盘对调用方不可见，
/// 已发出的镜像也不取决于它。</para>
/// </summary>
internal sealed class CompressionPayloadStaging : IDisposable
{
    private const int ChunkBytes = 1 << 20;

    private readonly long _memoryBudgetBytes;
    private readonly Lock _appendLock = new();
    private readonly List<byte[]> _chunks = [];
    private FileStream? _file;
    private long _length;
    private int _tailUsed;

    /// <summary>
    /// Creates the staging area with the memory budget it may keep resident.
    /// <para>按允许常驻的内存预算创建暂存区。</para>
    /// </summary>
    /// <param name="memoryBudgetBytes">Total staged bytes kept in memory before the content spills to the
    /// temporary file; negative values behave like zero and spill on the first non-empty append.
    /// <para>落盘前允许保留在内存中的暂存字节总量；负值与零等价，会在首次非空追加时落盘。</para></param>
    internal CompressionPayloadStaging(long memoryBudgetBytes = FirmwareParallelism.WorkerMemoryBudgetBytes)
    {
        _memoryBudgetBytes = Math.Max(0L, memoryBudgetBytes);
    }

    /// <summary>
    /// Appends one payload and returns the offset it was written at; the caller records the payload
    /// length separately.
    /// <para>追加一个负载并返回其写入偏移；负载长度由调用方另行记录。</para>
    /// </summary>
    /// <param name="payload">Compressed payload bytes appended at the current end of the staging area;
    /// an empty span writes nothing and still yields the end offset held before the call.
    /// <para>追加到暂存区末尾的压缩负载字节；为空时不写入任何内容，仍返回调用前的末尾偏移。</para></param>
    /// <returns>Absolute offset of <paramref name="payload"/> within the staging area.
    /// <para>负载在暂存区中的绝对起始偏移。</para></returns>
    public long Append(ReadOnlySpan<byte> payload)
    {
        lock (_appendLock)
        {
            var offset = _length;
            if (_file is null && offset + payload.Length <= _memoryBudgetBytes)
            {
                AppendToMemory(payload);
                return offset;
            }

            var file = _file ?? Spill();
            file.Write(payload);
            _length = offset + payload.Length;
            return offset;
        }
    }

    /// <summary>
    /// Reads a staged payload back into <paramref name="target"/>.
    /// <para>把一个暂存负载读回 <paramref name="target"/>。</para>
    /// </summary>
    /// <param name="offset">Absolute offset returned by <see cref="Append"/> for the staged payload;
    /// an offset past the end leaves too few bytes to read.
    /// <para><see cref="Append"/> 为该暂存负载返回的绝对偏移；偏移超出末尾时剩余字节不足。</para></param>
    /// <param name="target">Destination span whose length selects how many staged bytes are read.
    /// <para>目标区间，其长度决定读取的暂存字节数。</para></param>
    public void Read(long offset, Span<byte> target)
    {
        if (_file is { } file)
        {
            file.Seek(offset, SeekOrigin.Begin);
            file.ReadExactly(target);
            return;
        }

        ReadFromMemory(offset, target);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _file?.Dispose();
        _file = null;
        ReleaseChunks();
        _length = 0;
        _tailUsed = 0;
    }

    /// <summary>
    /// Copies one payload into the resident arena, renting a further chunk when the tail is full.
    /// <para>把一个负载复制到常驻内存区，尾块已满时再借一块。</para>
    /// </summary>
    /// <param name="payload">Bytes appended at the current end of the arena.
    /// <para>追加到内存区末尾的字节。</para></param>
    private void AppendToMemory(ReadOnlySpan<byte> payload)
    {
        while (!payload.IsEmpty)
        {
            if (_chunks.Count == 0 || _tailUsed == ChunkBytes)
            {
                _chunks.Add(ArrayPool<byte>.Shared.Rent(ChunkBytes));
                _tailUsed = 0;
            }

            var take = Math.Min(ChunkBytes - _tailUsed, payload.Length);
            payload[..take].CopyTo(_chunks[^1].AsSpan(_tailUsed));
            _tailUsed += take;
            _length += take;
            payload = payload[take..];
        }
    }

    /// <summary>
    /// Reads one staged payload out of the resident arena.
    /// <para>从常驻内存区读回一个已暂存负载。</para>
    /// </summary>
    /// <param name="offset">Absolute offset of the payload inside the arena.
    /// <para>负载在内存区中的绝对偏移。</para></param>
    /// <param name="target">Destination span whose length selects how many staged bytes are read.
    /// <para>目标区间，其长度决定读取的暂存字节数。</para></param>
    /// <exception cref="EndOfStreamException">The requested range reaches past the staged bytes, which is what
    /// the file-backed form reports for the same offset.
    /// <para>请求的区间越过已暂存字节，与文件形态对同一偏移报告的结果一致。</para></exception>
    private void ReadFromMemory(long offset, Span<byte> target)
    {
        if (offset < 0 || target.Length > _length - offset)
        {
            throw new EndOfStreamException("The staged payload range extends past the staged bytes.");
        }

        var index = (int)(offset / ChunkBytes);
        var inChunk = (int)(offset % ChunkBytes);
        var written = 0;
        while (written < target.Length)
        {
            var available = index == _chunks.Count - 1 ? _tailUsed : ChunkBytes;
            var take = Math.Min(Math.Min(available - inChunk, target.Length - written), ChunkBytes - inChunk);
            if (take <= 0)
            {
                throw new EndOfStreamException("The staged payload range extends past the staged bytes.");
            }

            _chunks[index].AsSpan(inChunk, take).CopyTo(target[written..]);
            written += take;
            inChunk = 0;
            index++;
        }
    }

    /// <summary>
    /// Creates the temporary file and moves the resident arena into it, so the complete staged range keeps
    /// its offsets and every later append continues at the end of the same file. The arena is returned to
    /// the pool afterwards, which keeps peak memory bounded by the configured budget.
    /// <para>创建临时文件并把常驻内存区迁入其中，使整段暂存范围保持原有偏移，后续追加继续写入同一文件的末尾。
    /// 迁移后内存区归还池，使峰值内存受配置的预算约束。</para>
    /// </summary>
    /// <returns>The file every later append and read addresses. <para>后续追加与读取所寻址的文件。</para></returns>
    private FileStream Spill()
    {
        var file = new FileStream(
            Path.Combine(Path.GetTempPath(), $"uotan-stage-{Guid.NewGuid():N}.tmp"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
            FileOptions.DeleteOnClose | FileOptions.SequentialScan);
        try
        {
            for (var index = 0; index < _chunks.Count; index++)
            {
                var count = index == _chunks.Count - 1 ? _tailUsed : ChunkBytes;
                file.Write(_chunks[index], 0, count);
            }

            _file = file;
            ReleaseChunks();
            _tailUsed = 0;
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Returns every resident chunk to the shared pool and empties the arena.
    /// <para>把全部常驻块归还共享池并清空内存区。</para>
    /// </summary>
    private void ReleaseChunks()
    {
        foreach (var chunk in _chunks)
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        _chunks.Clear();
    }
}