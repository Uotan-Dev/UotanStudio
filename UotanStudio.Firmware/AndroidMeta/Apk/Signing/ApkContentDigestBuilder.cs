using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace UotanStudio.Firmware.AndroidMeta.Apk.Signing;

/// <summary>
/// Streaming builder for the APK v2/v3 two-level content digest: bytes are appended section by
/// section, every 1 MiB chunk is hashed independently (optionally on the thread pool) and the
/// top-level digest is folded from the per-chunk digests in submission order.
/// <para>APK v2/v3 两级内容摘要的流式构建器：按区段追加字节，每个 1 MiB 分块独立哈希
/// （可选在线程池上执行），顶层摘要按提交顺序由各分块摘要折叠而成。</para>
/// </summary>
internal sealed class ApkContentDigestBuilder : IDisposable
{
    private const int ChunkSize = (int)ApkSignatureConstants.ContentDigestChunkSize;
    private const int MaximumParallelChunkHashes = 8;

    private readonly string _hashName;
    private readonly int _maximumInFlight;
    private readonly List<byte[]> _chunkDigests = [];
    private readonly List<Task<byte[]>> _pending = [];
    private byte[] _current;
    private int _currentLength;
    private bool _completed;
    private bool _disposed;

    /// <summary>
    /// Creates a builder fixed to one hash algorithm.
    /// <para>创建固定为单一哈希算法的构建器。</para>
    /// </summary>
    /// <param name="hashName">Hash algorithm name ("SHA256"/"SHA512").
    /// <para>哈希算法名称（"SHA256"/"SHA512"）。</para></param>
    /// <exception cref="CryptographicException">The name is outside the known hash algorithm set.
    /// <para>该名称不在已知哈希算法集合内。</para></exception>
    public ApkContentDigestBuilder(string hashName)
    {
        using var probe = ApkContentDigest.CreateAccumulator(hashName);
        _hashName = hashName;
        var degree = Math.Min(Environment.ProcessorCount, MaximumParallelChunkHashes);
        _maximumInFlight = degree > 1 ? degree * 2 : 1;
        _current = ArrayPool<byte>.Shared.Rent(ChunkSize);
    }

    /// <summary>
    /// Appends bytes that continue the current section. Empty spans change nothing.
    /// <para>追加延续当前区段的字节；空区间不产生变化。</para>
    /// </summary>
    /// <param name="data">Bytes in file order. <para>按文件顺序排列的字节。</para></param>
    /// <exception cref="CryptographicException">A drained chunk hash failed in the hash provider.
    /// <para>被回收的分块哈希在哈希提供程序中失败。</para></exception>
    public void Append(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            if (_currentLength == ChunkSize)
            {
                SubmitCurrentChunk();
            }

            var take = Math.Min(ChunkSize - _currentLength, data.Length);
            data[..take].CopyTo(_current.AsSpan(_currentLength));
            _currentLength += take;
            data = data[take..];
        }
    }

    /// <summary>
    /// Closes the current digest section. A section boundary always starts a new chunk, and an empty
    /// section contributes no chunk, matching the v2/v3 digest rules.
    /// <para>结束当前摘要区段。区段边界总是开始新分块，空区段不产生分块，与 v2/v3 摘要规则一致。</para>
    /// </summary>
    public void BeginSection()
    {
        if (_currentLength > 0)
        {
            SubmitCurrentChunk();
        }
    }

    /// <summary>
    /// Completes the digest over every appended section and returns the top-level digest.
    /// <para>对全部已追加区段完成摘要并返回顶层摘要。</para>
    /// </summary>
    /// <returns>The top-level digest whose length equals the hash output width.
    /// <para>顶层摘要，长度等于哈希输出宽度。</para></returns>
    /// <exception cref="InvalidOperationException">The builder was already completed.
    /// <para>构建器已完成。</para></exception>
    public byte[] Complete()
    {
        if (_completed)
        {
            throw new InvalidOperationException("APK content digest builder was already completed. APK 内容摘要构建器已完成。");
        }

        BeginSection();
        DrainPending();
        _completed = true;

        using var topLevel = ApkContentDigest.CreateAccumulator(_hashName);
        Span<byte> prefix = stackalloc byte[5];
        prefix[0] = 0x5a;
        BinaryPrimitives.WriteUInt32LittleEndian(prefix[1..], (uint)_chunkDigests.Count);
        topLevel.Append(prefix);
        foreach (var digest in _chunkDigests)
        {
            topLevel.Append(digest);
        }

        return topLevel.GetHashAndReset();
    }

    /// <summary>
    /// Releases the pooled chunk buffer and waits for queued chunk hashes so no buffer is returned
    /// to the pool while a worker still reads it.
    /// <para>释放池化分块缓冲区，并等待排队的分块哈希，避免工作线程仍在读取时把缓冲区归还池。</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            DrainPending();
        }
        catch (Exception)
        {
        }

        ArrayPool<byte>.Shared.Return(_current);
        _current = [];
    }

    private void SubmitCurrentChunk()
    {
        var buffer = _current;
        var length = _currentLength;
        _current = ArrayPool<byte>.Shared.Rent(ChunkSize);
        _currentLength = 0;

        if (_maximumInFlight == 1)
        {
            _chunkDigests.Add(HashChunk(buffer, length, returnBuffer: true));
            return;
        }

        _pending.Add(Task.Run(() => HashChunk(buffer, length, returnBuffer: true)));
        if (_pending.Count >= _maximumInFlight)
        {
            DrainFirstPending();
        }
    }

    private void DrainPending()
    {
        while (_pending.Count > 0)
        {
            DrainFirstPending();
        }
    }

    private void DrainFirstPending()
    {
        var task = _pending[0];
        _pending.RemoveAt(0);
        _chunkDigests.Add(task.GetAwaiter().GetResult());
    }

    private byte[] HashChunk(byte[] buffer, int length, bool returnBuffer)
    {
        try
        {
            using var hash = ApkContentDigest.CreateAccumulator(_hashName);
            Span<byte> prefix = stackalloc byte[5];
            prefix[0] = 0xa5;
            BinaryPrimitives.WriteUInt32LittleEndian(prefix[1..], (uint)length);
            hash.Append(prefix);
            hash.Append(buffer.AsSpan(0, length));
            return hash.GetHashAndReset();
        }
        finally
        {
            if (returnBuffer)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}