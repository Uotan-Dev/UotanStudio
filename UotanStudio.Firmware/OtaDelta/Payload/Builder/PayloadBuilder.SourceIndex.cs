using System.Buffers.Binary;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.OtaDelta.Payload.Builder;

public sealed partial class PayloadBuilder
{
    /// <summary>
    /// Content index over the source partition: it maps a block's SHA-256 digest to the source blocks holding that
    /// content, so incremental matching answers "which source block equals this target block" without re-reading the
    /// source for every probe.
    /// <para>源分区的块内容索引：把块的 SHA-256 摘要映射到持有该内容的源块，使增量匹配无需为每次探测重读源文件
    /// 即可回答"哪个源块与本目标块相等"。</para>
    /// </summary>
    /// <remarks>
    /// Two properties of the original probe are load-bearing and are preserved here.
    /// <para>原探测有两个性质是承载语义的，此处予以保留。</para>
    /// <para>First, bucket layout is observable: buckets are keyed by the low two digest bytes and every
    /// <see cref="Add"/> prepends, so a bucket is a newest-first chain and the digest walk returns the most recently
    /// indexed matching block. When the source holds duplicate blocks that index differs from a caller-supplied
    /// preferred block, and the returned index is copied into the emitted extents.</para>
    /// <para>其一，桶的布局是可观测的：桶以摘要最低两字节为键，每次 <see cref="Add"/> 前插，因此桶是"最新在前"的链，
    /// 摘要遍历返回最近被索引的匹配块。当源中存在重复块时，该索引与调用方给出的 preferred 块不同，而返回的索引会被
    /// 写入输出的 extent。</para>
    /// <para>Second, a probe consults the caller's preferred block before the digest walk and accepts it on a byte
    /// comparison. That shortcut is what keeps matching cheap on real images, where most target blocks repeat the
    /// content at their own source index.</para>
    /// <para>其二，探测先查调用方给出的 preferred 块，再走摘要遍历，并通过逐字节比较决定是否接受。正是这条快捷路径
    /// 让真实镜像上的匹配保持廉价：其中大多数目标块在自己的源下标处重复了相同内容。</para>
    /// </remarks>
    private sealed class IncrementalSourceIndex : IDisposable
    {
        /// <summary>
        /// Upper bound on resident records, chosen so the index stays bounded in memory while remaining far above
        /// the block count of realistic partitions.
        /// <para>常驻记录数上限，取值使索引在内存中有界，同时远高于实际分区的块数量。</para>
        /// </summary>
        private const int MaximumResidentRecords = 8 * 1024 * 1024;

        /// <summary>
        /// Bytes of one spilled record: a next-record offset, the source block index, and the digest.
        /// <para>单条溢写记录的字节数：下一条记录偏移、源块下标与摘要。</para>
        /// </summary>
        private const int SpilledRecordBytes = 48;

        /// <summary>
        /// Source blocks cached as raw bytes, so chain verification and the preferred-block shortcut need no source
        /// I/O. The window is bounded and re-anchored by the sequential reads matching already performs, which keeps
        /// it effective without unbounding memory.
        /// <para>以原始字节缓存的源块数量，使链校验与 preferred 块快捷路径都不再需要源 I/O。窗口有界，并由匹配过程
        /// 本身已执行的顺序读取重新锚定，从而在有界内存下保持有效。</para>
        /// </summary>
        private const int CachedBlocks = 4096;

        private readonly Stream _source;
        private readonly long _sourceOrigin;
        private readonly long[] _heads = new long[65536];
        private readonly int _blockSize;
        private readonly long[] _residentNext;
        private readonly long[] _residentBlock;
        private readonly byte[] _residentDigest;
        private readonly long[] _cacheBlock;
        private readonly byte[] _cacheBytes;
        private FileStream? _spilledRecords;
        private long _indexedBlocks;
        private long _recordCount;

        /// <summary>
        /// Creates an index over <paramref name="source"/>, answering probes from memory up to
        /// <see cref="MaximumResidentRecords"/> records and spilling to a temporary file beyond that.
        /// <para>在记录数不超过 <see cref="MaximumResidentRecords"/> 时以内存回答探测、超出后溢写到临时文件的方式，
        /// 创建基于 <paramref name="source"/> 的索引。</para>
        /// </summary>
        /// <param name="source">Seekable source partition stream. <para>可定位的源分区流。</para></param>
        /// <param name="blockSize">Block size in bytes used by every probe. <para>每次探测使用的块大小（字节）。</para></param>
        public IncrementalSourceIndex(Stream source, int blockSize)
        {
            _source = source;
            _sourceOrigin = source.Position;
            _blockSize = blockSize;
            _residentNext = new long[MaximumResidentRecords];
            _residentBlock = new long[MaximumResidentRecords];
            _residentDigest = new byte[(long)MaximumResidentRecords * 32];
            _cacheBlock = new long[CachedBlocks];
            _cacheBytes = new byte[CachedBlocks * blockSize];
            Array.Fill(_heads, -1);
            Array.Fill(_cacheBlock, -1);
        }

        /// <summary>
        /// Number of source blocks the index can resolve; preferred blocks outside it must be rejected.
        /// <para>索引可解析的源块数量；超出该范围的 preferred 块必须被拒绝。</para>
        /// </summary>
        public long IndexedBlocks => _indexedBlocks;

        /// <summary>
        /// Adds one source block to its digest bucket, prepending to that bucket's chain.
        /// <para>把一个源块加入其摘要桶，前插到该桶的链首。</para>
        /// </summary>
        /// <param name="bytes">Block contents, exactly one block long. <para>块内容，恰为一个块长。</para></param>
        /// <param name="block">Index the block occupies in the source partition. <para>该块在源分区中的下标。</para></param>
        [System.Runtime.CompilerServices.SkipLocalsInit]
        public void Add(ReadOnlySpan<byte> bytes, long block)
        {
            Span<byte> digest = stackalloc byte[32];
            Sha256.ComputeHash(bytes, digest);
            var bucket = BinaryPrimitives.ReadUInt16LittleEndian(digest);
            StoreRecord(handle: _recordCount, previous: _heads[bucket], block: block, digest: digest);
            _heads[bucket] = _recordCount++;
            if (block >= _indexedBlocks)
            {
                _indexedBlocks = block + 1;
            }
        }

        /// <summary>Resolves every block of one target chunk against the index, writing one source block index per target block, or <c>-1</c> when the content is absent.
        /// <para>把一个目标分块的每个块与索引比对，按目标块写入一个源块下标；内容不存在时为 <c>-1</c>。</para></summary>
        /// <remarks>This loop is deliberately serial. Each block's preferred hint is derived from the previous block's resolved match, and a hint failure is resolved by the digest chains, so block <c>i</c> cannot be settled before block <c>i - 1</c>. Evaluating blocks out of order or concurrently therefore changes which source block duplicate content resolves to, and that index is copied into the emitted extents, so the parallelism budget for incremental matching is spent inside the index instead of in this loop.
        /// <para>本循环刻意保持串行。每个块的 preferred 提示源自前一个块已解析的匹配，而提示失败时由摘要链解析， 因此块 <c>i</c> 无法在块 <c>i - 1</c> 之前定论。乱序或并发求值会改变重复内容解析到的源块，而该索引会被 写入输出的 extent，因此增量匹配的并发预算花在索引内部而不是本循环上。</para></remarks>
        /// <param name="chunk">Target chunk buffer, a whole number of blocks long.
        /// <para>目标分块缓冲区，长度是块的整数倍。</para></param>
        /// <param name="chunkLength">Valid bytes in <paramref name="chunk"/>.
        /// <para><paramref name="chunk"/> 中的有效字节数。</para></param>
        /// <param name="blockSize">Block size in bytes.<para>块大小（字节）。</para></param>
        /// <param name="chunkStartBlock">Index of the chunk's first block in the target partition.
        /// <para>本分块首块在目标分区中的下标。</para></param>
        /// <param name="matches">Receives one source block index per target block, or <c>-1</c>; must hold at least <c>chunkLength / blockSize</c> entries.
        /// <para>按目标块接收一个源块下标或 <c>-1</c>；容量至少为 <c>chunkLength / blockSize</c>。</para></param>
        /// <param name="token">Cancellation token.<para>取消令牌。</para></param>
        public void MatchChunk(byte[] chunk, int chunkLength, int blockSize, ulong chunkStartBlock, long[] matches,
            CancellationToken token)
        {
            var blocks = chunkLength / blockSize;
            for (var i = 0; i < blocks; i++)
            {
                var preferred = i > 0 && matches[i - 1] >= 0
                    ? matches[i - 1] + 1
                    : checked((long)chunkStartBlock + i);
                if (Confirm(preferred, chunk.AsSpan(i * blockSize, blockSize), token))
                {
                    matches[i] = preferred;
                    continue;
                }

                matches[i] = FindHashed(chunk.AsSpan(i * blockSize, blockSize), token);
            }
        }

        /// <summary>
        /// Answers one probe from the digest chains alone, ignoring any preferred block.
        /// <para>仅依据摘要链回答一次探测，忽略任何 preferred 块。</para>
        /// </summary>
        /// <param name="bytes">Probe block contents, exactly one block long. <para>探测块内容，恰为一个块长。</para></param>
        /// <param name="token">Cancellation token checked between chain nodes. <para>在链节点之间检查的取消令牌。</para></param>
        /// <returns>
        /// The most recently indexed matching source block, or <c>-1</c> when the content is absent.
        /// <para>最近被索引的匹配源块；内容不存在时为 <c>-1</c>。</para>
        /// </returns>
        [System.Runtime.CompilerServices.SkipLocalsInit]
        private long FindHashed(ReadOnlySpan<byte> bytes, CancellationToken token)
        {
            Span<byte> digest = stackalloc byte[32];
            Sha256.ComputeHash(bytes, digest);
            var handle = _heads[BinaryPrimitives.ReadUInt16LittleEndian(digest)];
            while (handle >= 0)
            {
                token.ThrowIfCancellationRequested();
                if (digest.SequenceEqual(GetDigest(handle)))
                {
                    var block = GetBlock(handle);
                    if (BlockEquals(block, bytes, token))
                    {
                        return block;
                    }
                }

                handle = GetPrevious(handle);
            }

            return -1;
        }

        /// <summary>
        /// Checks whether <paramref name="block"/> still holds <paramref name="bytes"/>, which is the cheap
        /// preferred-block fast path of the original probe.
        /// <para>检查 <paramref name="block"/> 是否仍持有 <paramref name="bytes"/>，即原探测中开销较低的
        /// preferred 块快捷路径。</para>
        /// </summary>
        /// <param name="block">Candidate source block index. <para>候选源块下标。</para></param>
        /// <param name="bytes">Probe block contents, exactly one block long. <para>探测块内容，恰为一个块长。</para></param>
        /// <param name="token">Cancellation token forwarded to any source read. <para>转发给任何源读取的取消令牌。</para></param>
        /// <returns><see langword="true"/> when the preferred block matches the probe. <para>preferred 块与探测内容一致时为 <see langword="true"/>。</para></returns>
        public bool Confirm(long block, ReadOnlySpan<byte> bytes, CancellationToken token)
            => block >= 0 && block < _indexedBlocks && BlockEquals(block, bytes, token);

        /// <summary>
        /// Reads one source block into <paramref name="bytes"/>.
        /// <para>把一个源块读入 <paramref name="bytes"/>。</para>
        /// </summary>
        /// <param name="block">Indexed source block to read. <para>要读取的已索引源块。</para></param>
        /// <param name="bytes">Destination, exactly one block long. <para>目标缓冲区，恰为一个块长。</para></param>
        /// <param name="token">Cancellation token forwarded to the source read. <para>转发给源读取的取消令牌。</para></param>
        /// <exception cref="PayloadGenerationException"><paramref name="block"/> was never indexed.
        /// <para><paramref name="block"/> 从未被索引。</para></exception>
        public void Read(long block, Span<byte> bytes, CancellationToken token)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(block);
            if (block >= _indexedBlocks)
            {
                throw new PayloadGenerationException("Incremental source index cannot read an unindexed block.");
            }

            if (TryCopyCached(block, bytes))
            {
                return;
            }

            lock (_source)
            {
                _source.Position = _sourceOrigin + checked(block * _blockSize);
                ReadIncrementalChunk(_source, bytes, token);
            }
        }

        /// <summary>
        /// Releases the temporary spill file, if the index outgrew its resident capacity.
        /// <para>索引超出常驻容量时释放临时溢写文件。</para>
        /// </summary>
        public void Dispose()
        {
            _spilledRecords?.Dispose();
            _spilledRecords = null;
        }

        /// <summary>
        /// Compares a source block against probe bytes, preferring cached content so the common path performs no
        /// source I/O at all.
        /// <para>把源块与探测字节比较，优先使用缓存内容，使常见路径完全不发生源 I/O。</para>
        /// </summary>
        /// <param name="block">Source block to compare. <para>要比较的源块。</para></param>
        /// <param name="bytes">Probe bytes to compare against. <para>用于比较的探测字节。</para></param>
        /// <param name="token">Cancellation token forwarded to any source read. <para>转发给任何源读取的取消令牌。</para></param>
        /// <returns><see langword="true"/> when the block and the probe hold identical content. <para>块与探测内容完全相同时为 <see langword="true"/>。</para></returns>
        [System.Runtime.CompilerServices.SkipLocalsInit]
        private bool BlockEquals(long block, ReadOnlySpan<byte> bytes, CancellationToken token)
        {
            var slot = (int)(block % CachedBlocks);
            if (_cacheBlock[slot] == block)
            {
                return _cacheBytes.AsSpan(slot * _blockSize, _blockSize).SequenceEqual(bytes);
            }

            Span<byte> comparison = stackalloc byte[_blockSize];
            Read(block, comparison, token);
            if (!comparison.SequenceEqual(bytes))
            {
                return false;
            }

            comparison.CopyTo(_cacheBytes.AsSpan(slot * _blockSize));
            _cacheBlock[slot] = block;
            return true;
        }

        private bool TryCopyCached(long block, Span<byte> bytes)
        {
            var slot = (int)(block % CachedBlocks);
            if (_cacheBlock[slot] != block)
            {
                return false;
            }

            _cacheBytes.AsSpan(slot * _blockSize, _blockSize).CopyTo(bytes);
            return true;
        }

        private void StoreRecord(long handle, long previous, long block, ReadOnlySpan<byte> digest)
        {
            if (handle < MaximumResidentRecords)
            {
                var slot = (int)handle;
                _residentNext[slot] = previous;
                _residentBlock[slot] = block;
                digest.CopyTo(_residentDigest.AsSpan(slot * 32));
                return;
            }

            _spilledRecords ??= Diff.BinaryDeltaPatch.CreateTemporaryStream();
            Span<byte> record = stackalloc byte[SpilledRecordBytes];
            BinaryPrimitives.WriteInt64LittleEndian(record, previous);
            BinaryPrimitives.WriteInt64LittleEndian(record[8..], block);
            digest.CopyTo(record[16..]);
            _spilledRecords.Position = _spilledRecords.Length;
            _spilledRecords.Write(record);
        }

        private long GetPrevious(long handle) => ReadRecord(handle).Previous;

        private long GetBlock(long handle) => ReadRecord(handle).Block;

        private ReadOnlySpan<byte> GetDigest(long handle)
            => handle < MaximumResidentRecords
                ? _residentDigest.AsSpan((int)handle * 32, 32)
                : ReadSpilledDigest(handle);

        private (long Previous, long Block, byte[] Digest) ReadRecord(long handle)
            => handle < MaximumResidentRecords
                ? (_residentNext[(int)handle], _residentBlock[(int)handle], _residentDigest.AsSpan((int)handle * 32, 32).ToArray())
                : ReadSpilledRecord(handle);

        private (long Previous, long Block, byte[] Digest) ReadSpilledRecord(long handle)
        {
            var stream = _spilledRecords
                ?? throw new PayloadGenerationException("Incremental source index spilled record is unavailable.");
            Span<byte> record = stackalloc byte[SpilledRecordBytes];
            stream.Position = (handle - MaximumResidentRecords) * SpilledRecordBytes;
            stream.ReadExactly(record);
            return (BinaryPrimitives.ReadInt64LittleEndian(record),
                BinaryPrimitives.ReadInt64LittleEndian(record[8..]),
                record[16..].ToArray());
        }

        private ReadOnlySpan<byte> ReadSpilledDigest(long handle)
        {
            var stream = _spilledRecords
                ?? throw new PayloadGenerationException("Incremental source index spilled record is unavailable.");
            var digest = new byte[32];
            stream.Position = (handle - MaximumResidentRecords) * SpilledRecordBytes + 16;
            stream.ReadExactly(digest);
            return digest;
        }
    }
}