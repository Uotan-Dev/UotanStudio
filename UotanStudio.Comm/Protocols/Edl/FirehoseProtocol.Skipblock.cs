using System.Security.Cryptography;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Digest-driven skip-before-write programming modeled on qdl's skipblock fast path
/// (<c>src/firehose.c</c> firehose_program_skipblock): the region is walked in bounded chunks, each
/// chunk's source bytes are hashed locally with qdl's trailing zero-pad semantics, and only chunks
/// whose local digest differs from the flash digest reported by <c>&lt;getsha256digest&gt;</c> are
/// programmed. NAND is excluded because spare/OOB bytes make target digests programmer-specific,
/// mirroring the reference restrictions.
/// <para>基于摘要的“先比对后跳过”编程路径，参照 qdl 的 skipblock 快速路径（<c>src/firehose.c</c>
/// 的 firehose_program_skipblock）：区域按有界分块遍历，每块源字节按 qdl 的尾部零填充语义在本地
/// 计算哈希，只有与 <c>&lt;getsha256digest&gt;</c> 上报的闪存摘要不一致的块才会被编程。NAND 的
/// spare/OOB 字节使目标摘要因 programmer 而异，与参考实现的限制一致，故被排除。</para>
/// </summary>
public sealed partial class FirehoseProtocol
{
    /// <summary>
    /// Maximum region handed to one <c>&lt;getsha256digest&gt;</c> round trip (qdl
    /// <c>SKIPBLOCK_CHUNK_BYTES</c>). Hashing a multi-gigabyte partition in a single query can
    /// exceed the target's digest timeout, so the region is compared chunk by chunk.
    /// <para>单次 <c>&lt;getsha256digest&gt;</c> 往返的最大区域（qdl 的 SKIPBLOCK_CHUNK_BYTES）。
    /// 对多 GB 分区做单次摘要查询可能超过目标的摘要计算超时，因此按块逐一比对。</para>
    /// </summary>
    private const long SkipblockChunkBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Programs the declared region on an already-owned operation lease, skipping chunks whose
    /// source already matches the on-target digest. The first chunk acts as the capability probe:
    /// when the target reports no digest there, the attempt returns <see langword="false"/> before
    /// any decision so the caller can program the region normally. A digest that disappears after
    /// later chunks only degrades that chunk back to a normal program. Transport failures propagate
    /// because the session is unusable afterwards.
    /// <para>在已持有的操作租约上编程声明的区域，跳过源数据已与目标摘要一致的块。首块充当能力
    /// 探测：目标在该块未上报摘要时，本方法在任何决策之前返回 <see langword="false"/>，由调用方
    /// 按常规编程整个区域。后续块摘要消失只把该块降级回常规编程。传输失败会向外传播，因为会话
    /// 随后不可再用。</para>
    /// </summary>
    /// <param name="dataStream">
    /// The seekable source covering the region from its start; bytes past its end are treated as
    /// zeros for digest comparison and programming, mirroring qdl's trailing zero-pad.
    /// <para>从区域起点覆盖该区域的可寻址源；超出其末尾的字节在摘要比对与编程时均按零处理，
    /// 与 qdl 的尾部零填充一致。</para>
    /// </param>
    /// <param name="startSector">The absolute starting sector. <para>绝对起始扇区。</para></param>
    /// <param name="numSectors">The number of declared sectors. <para>声明的扇区数。</para></param>
    /// <param name="filename">The filename metadata carried by chunk program commands.
    /// <para>分块 program 命令携带的文件名元数据。</para></param>
    /// <param name="physicalPartition">The physical storage region or LUN.
    /// <para>物理存储区域或 LUN。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>
    /// <see langword="true"/> when every chunk was either skipped on a digest match or programmed;
    /// <see langword="false"/> when the target gave no digest for the first chunk and the caller
    /// must program the region normally.
    /// <para>每块都因摘要匹配被跳过或已被编程时为 <see langword="true"/>；目标未对首块上报摘要、
    /// 需由调用方按常规编程整个区域时为 <see langword="false"/>。</para>
    /// </returns>
    internal async Task<bool> TryProgramSkipblockOwnedAsync(Stream dataStream, long startSector, long numSectors,
        string filename, int physicalPartition, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (Configuration.StorageType is FirehoseStorageType.Nand || dataStream is not { CanSeek: true })
        {
            return false;
        }

        var sectorSize = Configuration.SectorSize;
        if (sectorSize is <= 0 or > 65536)
        {
            return false;
        }

        var chunkMaxSectors = SkipblockChunkBytes / sectorSize;
        long chunkIndex = 0;
        long skipped = 0;
        long flashed = 0;
        for (var offset = 0L; offset < numSectors; offset += chunkMaxSectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            chunkIndex++;
            var chunkSectors = Math.Min(chunkMaxSectors, numSectors - offset);
            var chunkBytes = checked(chunkSectors * sectorSize);
            var chunkStart = checked(startSector + offset);
            var streamOffset = checked(offset * sectorSize);
            Log($"Skipblock hashing chunk {chunkIndex} locally ({chunkBytes >> 10} KiB)...");
            var localDigest = await ComputeSkipblockLocalDigestAsync(dataStream, streamOffset, chunkBytes,
                cancellationToken).ConfigureAwait(false);
            var targetDigest = await GetSha256DigestCoreAsync(chunkStart, chunkSectors, physicalPartition,
                cancellationToken, EffectiveSlot).ConfigureAwait(false);
            if (targetDigest is null)
            {
                if (chunkIndex == 1)
                {
                    Log("The target did not report a digest for the first skipblock chunk; programming the region normally.");
                    return false;
                }

                Log("The target stopped reporting digests; programming this chunk to stay safe.");
            }

            if (targetDigest is not null && localDigest.AsSpan().SequenceEqual(targetDigest))
            {
                skipped++;
                Log($"Skipblock chunk {chunkIndex} matches the flash digest; skipped.");
                continue;
            }

            Log($"Skipblock chunk {chunkIndex} differs from the flash digest; flashing.");
            using var padded = new ZeroPaddedSourceStream(dataStream, streamOffset, chunkBytes);
            await ProgramCoreAsync(padded, new FirehoseSectorAddress(chunkStart, false), chunkSectors,
                filename, physicalPartition, cancellationToken).ConfigureAwait(false);
            flashed++;
        }

        Log($"Skipblock finished for {filename}: {flashed} chunk(s) flashed, {skipped} skipped.");
        return true;
    }

    /// <summary>
    /// Hashes <paramref name="length"/> bytes of the source starting at <paramref name="offset"/>
    /// with trailing zero-pad past the source's end (qdl firehose_region_local_digest), so a chunk
    /// reaching beyond the image compares as if the missing tail were zeros. Streaming keeps the
    /// buffer bounded regardless of the region size.
    /// <para>对源中自 <paramref name="offset"/> 起的 <paramref name="length"/> 字节计算哈希，源结束
    /// 之后按零填充（qdl 的 firehose_region_local_digest），使越过镜像末尾的分块如同缺失尾部为零一样
    /// 参与比对。流式处理使缓冲与区域大小无关、保持有界。</para>
    /// </summary>
    /// <param name="dataStream">The seekable source stream. <para>可寻址的源流。</para></param>
    /// <param name="offset">The byte offset of the chunk within the source.
    /// <para>分块在源内的字节偏移。</para></param>
    /// <param name="length">The declared chunk size in bytes. <para>声明的分块字节数。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The 32-byte SHA-256 digest. <para>32 字节的 SHA-256 摘要。</para></returns>
    private static async Task<byte[]> ComputeSkipblockLocalDigestAsync(Stream dataStream, long offset, long length,
        CancellationToken cancellationToken)
    {
        dataStream.Seek(offset, SeekOrigin.Begin);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long hashed = 0;
        while (hashed < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var want = (int)Math.Min(buffer.Length, length - hashed);
            var read = await dataStream.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            hasher.AppendData(buffer, 0, read);
            hashed += read;
        }

        if (hashed < length)
        {
            Array.Clear(buffer);
            while (hashed < length)
            {
                var pad = (int)Math.Min(buffer.Length, length - hashed);
                hasher.AppendData(buffer, 0, pad);
                hashed += pad;
            }
        }

        return hasher.GetHashAndReset();
    }

    /// <summary>
    /// A bounded read-only view of one chunk that substitutes zero bytes past the source's end,
    /// mirroring the program path's zero-pad semantics so a chunk reaching beyond the image is
    /// still programmed as its exact declared range.
    /// <para>单个分块的有界只读视图，在源数据结束后以零字节代替，镜像编程路径的零填充语义，
    /// 使越过镜像末尾的分块仍能按声明的完整范围编程。</para>
    /// </summary>
    /// <param name="source">The underlying source stream. <para>底层源流。</para></param>
    /// <param name="sourceOffset">The chunk's byte offset within the source.
    /// <para>分块在源内的字节偏移。</para></param>
    /// <param name="length">The declared chunk length in bytes. <para>声明的分块字节数。</para></param>
    private sealed class ZeroPaddedSourceStream(Stream source, long sourceOffset, long length) : Stream
    {
        private readonly long _sourceAvailable = Math.Max(0, source.Length - sourceOffset);
        private long _position;

        /// <summary>Gets whether the stream can be read. 流是否可读。</summary>
        public override bool CanRead => true;

        /// <summary>Gets whether the stream can seek. 流是否可寻址。</summary>
        public override bool CanSeek => true;

        /// <summary>Gets whether the stream can be written. 流是否可写。</summary>
        public override bool CanWrite => false;

        /// <summary>Gets the declared chunk length. 声明的分块长度。</summary>
        public override long Length => length;

        /// <summary>Gets or sets the position within the declared chunk.
        /// 分块内的位置。</summary>
        public override long Position { get => _position; set => _position = value; }

        /// <summary>Flush (no-op). 刷新（空操作）。</summary>
        public override void Flush() { }

        /// <summary>Reads source bytes followed by zero padding.
        /// 读取源字节，其后以零填充。</summary>
        public override int Read(byte[] buffer, int offset, int count)
        {
            var fromSource = (int)Math.Min(count, Math.Max(0, _sourceAvailable - _position));
            var read = 0;
            if (fromSource > 0)
            {
                source.Seek(sourceOffset + _position, SeekOrigin.Begin);
                read = source.Read(buffer, offset, fromSource);
            }

            if (read < count)
            {
                buffer.AsSpan(offset + read, count - read).Clear();
            }

            _position += count;
            return count;
        }

        /// <summary>Reads source bytes followed by zero padding.
        /// 读取源字节，其后以零填充。</summary>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fromSource = (int)Math.Min(buffer.Length, Math.Max(0, _sourceAvailable - _position));
            var read = 0;
            if (fromSource > 0)
            {
                source.Seek(sourceOffset + _position, SeekOrigin.Begin);
                read = await source.ReadAsync(buffer[..fromSource], cancellationToken).ConfigureAwait(false);
            }

            if (read < buffer.Length)
            {
                buffer[read..].Span.Clear();
            }

            _position += buffer.Length;
            return buffer.Length;
        }

        /// <summary>Seeks within the declared chunk length. 在声明的分块长度内寻址。</summary>
        public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset
        };

        /// <summary>Set length (not supported). 设置长度（不支持）。</summary>
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>Write (not supported). 写入（不支持）。</summary>
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        /// <summary>Disposes the view without closing the underlying source.
        /// 释放视图但不关闭底层源流。</summary>
        /// <param name="disposing">Whether managed resources are being released.
        /// 是否正在释放托管资源。</param>
        protected override void Dispose(bool disposing)
        {
        }
    }
}