using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UnifiedDataLayout = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsDataLayout;
using UnifiedInode = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsInode;

namespace UotanStudio.Firmware.FileSystem.Erofs.Parser;

/// <summary>
/// EROFS parser part that copies inode data out of the image: it checks every offset and length against the image size, dispatches on the inline, flat, chunk-based and compressed layouts, and reports pcluster coverage deficits against the pre-5.13 decoding rules.
/// <para>EROFS 解析器的组成部分，负责把 inode 数据从镜像中取出：先用镜像大小校验每个偏移与长度，
/// 再按 inline、flat、chunk-based 与压缩布局分派，并按 pre-5.13 解码规则报告 pcluster 的覆盖缺口。</para>
/// </summary>
public sealed partial class ErofsParser
{
    private void CheckRange(long offset, long length, string what)
    {
        if (offset < 0 || length < 0 || offset > _imageSize || length > _imageSize - offset)
        {
            throw new FileSystemCorruptedException($"{what} exceeds the image bounds.");
        }
    }

    private void WriteFileData(ulong nid, UnifiedInode inode, Stream output)
    {
        _readCancellation.ThrowIfCancellationRequested();
        if (_readCancellation.CanBeCanceled)
        {
            output = new CancellationWriteStream(output, _readCancellation);
        }
        if (IsSpecialFile(inode))
        {

            throw new FileSystemCorruptedException(
                $"Inode nid {nid} is a special file (mode 0x{inode.Mode:X4}) with no data content.");
        }

        switch (inode.DataLayout)
        {
            case UnifiedDataLayout.Inline:

                WriteInlineData(nid, inode, output);
                break;
            case UnifiedDataLayout.Flat:
                WriteFlatData(inode, output);
                break;
            case UnifiedDataLayout.ChunkBased:
                WriteChunkData(nid, inode, output);
                break;
            case UnifiedDataLayout.Compressed:
            case UnifiedDataLayout.CompactIndexes:
                WriteCompressedData(nid, inode, output);
                break;
            default:

                throw new FileSystemUnsupportedFeatureException($"Unsupported EROFS data layout {inode.DataLayout} for inode nid {nid}.");
        }
    }

    private void WriteInlineData(ulong nid, UnifiedInode inode, Stream output)
    {
        var length = checked((long)inode.Size);
        if (length == 0)
        {
            return;
        }

        var flatLength = (long)(inode.Size / (ulong)_blockSize * (ulong)_blockSize);
        if (flatLength > 0)
        {
            var flatStart = checked(inode.AddressField() * _blockSize);
            CheckRange(flatStart, flatLength, "Tailpacked flat file data");
            CopyFromSource(output, flatStart, flatLength);
        }

        var tailLength = length - flatLength;
        if (tailLength > 0)
        {
            if (_hasMetabox && (nid & NidMetaboxFlag) != 0)
            {
                var tailStart = checked(MetaboxOffset(nid) + inode.InlineDataOffset);
                output.Write(ReadMetaboxSpan(tailStart, checked((int)tailLength), "Tailpacked inline data"));
            }
            else
            {
                var tailStart = checked(_superblock.GetInodeOffset(nid) + inode.InlineDataOffset);
                CheckRange(tailStart, tailLength, "Tailpacked inline data");
                CopyFromSource(output, tailStart, tailLength);
            }
        }
    }

    private void WriteFlatData(UnifiedInode inode, Stream output)
    {
        var length = (long)inode.Size;
        if (length == 0)
        {
            return;
        }

        var start = checked(inode.AddressField() * _blockSize);
        CheckRange(start, length, "File data");
        CopyFromSource(output, start, length);
    }

    /// <summary>
    /// Reads a chunk-based file (EROFS_INODE_CHUNK_BASED): assembles data chunk by chunk from the chunk index table.
    /// <para>读取 chunk-based 文件（EROFS_INODE_CHUNK_BASED）：按 chunk 索引表逐块拼接数据。</para>
    /// </summary>
    private void WriteChunkData(ulong nid, UnifiedInode inode, Stream output)
    {
        var length = checked((long)inode.Size);
        if (length == 0)
        {
            return;
        }

        var chunkBlkBits = inode.ChunkFormatBlkBits;
        var chunkSize = (long)_blockSize << chunkBlkBits;
        if ((inode.ChunkFormat & ~UnifiedInode.ChunkFormatAll) != 0 || (inode.Chunk48Bit && !inode.UsesChunkIndexes))
        {
            throw new FileSystemCorruptedException($"Invalid chunk size (blkbits {chunkBlkBits}) for nid {nid}.");
        }

        var chunkCount = (length - 1) / chunkSize + 1;
        var usesIndexes = inode.UsesChunkIndexes;
        var indexSize = usesIndexes ? 8 : 4;
        var indexTableOffset = checked((InodeOffset(nid) + inode.InlineDataOffset + indexSize - 1) & ~(long)(indexSize - 1));
        var indexBytes = usesIndexes
            ? checked(chunkCount * 8L)
            : checked(chunkCount * 4L);
        if (indexBytes > MaxMetadataComponentSize)
        {
            throw new FileSystemCorruptedException(
                $"Chunk index table size {indexBytes} exceeds the limit of {MaxMetadataComponentSize} bytes.");
        }

        var index = ReadInodeMetadata(nid, indexTableOffset, checked((int)indexBytes), "Chunk index table");
        var remaining = length;
        for (var i = 0; i < chunkCount && remaining > 0; i++)
        {
            var chunkLen = Math.Min(chunkSize, remaining);
            ulong startBlk;
            var deviceId = 0;
            if (usesIndexes)
            {
                var slot = index.AsSpan(i * 8, 8);
                startBlk = BinaryPrimitives.ReadUInt32LittleEndian(slot.Slice(4));
                if (inode.Chunk48Bit)
                    startBlk |= (ulong)BinaryPrimitives.ReadUInt16LittleEndian(slot) << 32;
                var deviceMask = System.Numerics.BitOperations.RoundUpToPowerOf2((uint)_superblock.ExtraDevices + 1) - 1;
                deviceId = (int)(BinaryPrimitives.ReadUInt16LittleEndian(slot.Slice(2)) & deviceMask);
            }
            else
            {

                startBlk = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(i * 4, 4));
            }

            if (startBlk == (inode.Chunk48Bit ? 0xFFFFFFFFFFFFUL : uint.MaxValue))
            {

                var zeroed = 0L;
                while (zeroed < chunkLen)
                {
                    var n = (int)Math.Min(SharedZeroBuffer.Length, chunkLen - zeroed);
                    output.Write(SharedZeroBuffer, 0, n);
                    zeroed += n;
                }

                remaining -= chunkLen;
                continue;
            }

            if (deviceId == 0 && !_flatDevice && _deviceSlots is not null)
            {
                for (var mappedId = 1; mappedId < _deviceSlots.Length; mappedId++)
                {
                    var mapped = _deviceSlots[mappedId];
                    if (mapped.UniAddr != 0 && startBlk >= mapped.UniAddr && startBlk - mapped.UniAddr < mapped.Blocks)
                    {
                        deviceId = mappedId;
                        startBlk -= mapped.UniAddr;
                        break;
                    }
                }
            }

            if (deviceId > 0)
            {
                var devOffset = checked((long)startBlk * _blockSize);
                CopyFromDevice(output, deviceId, devOffset, chunkLen, "Multi-device chunk data");
            }
            else
            {
                var dataStart = checked((long)startBlk * _blockSize);
                CheckRange(dataStart, chunkLen, "Chunk data");
                CopyFromSource(output, dataStart, chunkLen);
            }
            remaining -= chunkLen;
        }
    }

    private const byte LclusterTypePlain = 0;
    private const byte LclusterTypeHead1 = 1;
    private const byte LclusterTypeNonHead = 2;
    private const byte LclusterTypeHead2 = 3;

    private const int FullIndexStartPad = 8;

    /// <summary>
    /// Byte size of one compact-index slot in the 4-byte (non-amortized) form.
    /// <para>紧凑索引 4 字节（非摊薄）形式的槽位字节大小。</para>
    /// </summary>
    private const int CompactIndexSlotSize = 4;

    /// <summary>
    /// Extra bytes the compact-index read window reserves beyond the index entries themselves, covering the aligned index packs (up to 32 bytes) read while decoding compact indexes.
    /// <para>紧凑索引读取窗口在索引条目本身之外额外预留的字节数，用于覆盖解码紧凑索引时按对齐索引包（最多 32 字节）的读取。</para>
    /// </summary>
    private const int CompactIndexWindowPadding = 64;

    /// <summary>
    /// Worst-case bytes one parallel pcluster worker holds — the encoded staging buffer capped at
    /// <see cref="ErofsCompression.MaxPclusterSize"/> plus the decoded buffer capped at
    /// <see cref="ErofsCompression.MaxPclusterDecompressedSize"/> — used to cap the worker count against the
    /// shared worker memory budget.
    /// <para>单个并行 pcluster 工作者最坏情况下占用的字节数：编码暂存缓冲上限
    /// <see cref="ErofsCompression.MaxPclusterSize"/> 加上解码缓冲上限
    /// <see cref="ErofsCompression.MaxPclusterDecompressedSize"/>，用于按共享工作者内存预算限制工作者数量。</para>
    /// </summary>
    private const long CompressedPclusterWorkerBytes =
        (long)ErofsCompression.MaxPclusterSize + ErofsCompression.MaxPclusterDecompressedSize;

    /// <summary>
    /// Reads compressed file data through compact or full EROFS cluster indexes, including LZ4SC and
    /// ZLIB4C semi-compressed pclusters: a PLAIN head lcluster followed by NONHEAD lclusters decodes as
    /// one raw logical cluster (the head, stored verbatim at the pcluster start) plus the remaining
    /// logical clusters produced by the HEAD1 codec (LZ4 for LZ4SC, raw DEFLATE for ZLIB4C). Semi
    /// detection requires big-pcluster indexing (the CBLKCNT marker on the first NONHEAD lcluster), a
    /// zero head clusterofs, and a non-interlaced, non-tailpacked pcluster; PLAIN heads that encode a
    /// LZMA or ZSTD stream stay fully raw (SHIFTED). When no observer probes the clusters, encoded pclusters
    /// are decoded by a bounded per-inode pipeline and written in logical order, so the produced bytes and the
    /// first logical-order failure stay identical to the sequential walk.
    /// <para>通过紧凑或完整的 EROFS 簇索引读取压缩文件数据，含 LZ4SC 与 ZLIB4C 半压缩 pcluster：
    /// 当 PLAIN head lcluster 后跟随 NONHEAD lcluster 时，首逻辑簇按 pcluster 起点的原始字节直接输出，
    /// 其余逻辑簇由 HEAD1 编解码器解压产出（LZ4SC 为 LZ4，ZLIB4C 为原始 DEFLATE）。半压缩识别要求
    /// big-pcluster 索引（首个 NONHEAD 携带 CBLKCNT 标记）、head 的 clusterofs 为 0，且非 interlaced、
    /// 非尾部打包；编码 LZMA 或 ZSTD 流的 PLAIN head 仍整体视为明文（SHIFTED）。</para>
    /// </summary>
    private void WriteCompressedData(ulong nid, UnifiedInode inode, Stream output,
        CompressionObserver? observer = null, CancellationToken cancellationToken = default,
        ExtentWriteScratch? extentScratch = null)
    {
        if (TryWriteNonLclusterLayout(nid, inode, output, observer, cancellationToken,
            out var header, out var headerOffset, extentScratch))
        {
            return;
        }

        var advise = header.Advise;
        var algorithmHead1 = header.Algorithm;
        var algorithmHead2 = (ErofsAlgorithm)((header.AlgorithmType >> 4) & ZErofsMapHeader.AlgorithmTypeMask);
        var lclusterBits = _superblock.BlockSizeBits + (header.ClusterBits & ZErofsMapHeader.LclusterBitsMask);
        if (lclusterBits > CompactIndexMaxLclusterBits)
        {
            throw new FileSystemCorruptedException($"Logical cluster size shift {lclusterBits} exceeds the limit of {CompactIndexMaxLclusterBits}.");
        }

        var lclusterSize = 1 << lclusterBits;
        var totalIdx = ErofsCompression.PclusterCount(inode.Size, lclusterSize);
        if (totalIdx == 0)
        {
            return;
        }

        var bigPcluster1 = (advise & ZErofsMapHeader.AdviseBigPcluster1) != 0;
        var bigPcluster2 = (advise & ZErofsMapHeader.AdviseBigPcluster2) != 0;
        var tailPacking = (advise & ZErofsMapHeader.AdviseInlinePcluster) != 0;
        var fragmentPcluster = (advise & ZErofsMapHeader.AdviseFragmentPcluster) != 0;
        var interlaced = (advise & ZErofsMapHeader.AdviseInterlacedPcluster) != 0;

        var indexBase = headerOffset + ZErofsMapHeader.Size;
        var indexLength = inode.DataLayout == UnifiedDataLayout.CompactIndexes
            ? checked((long)totalIdx * CompactIndexSlotSize + CompactIndexWindowPadding)
            : checked((long)totalIdx * ZErofsLclusterIndex.Size + FullIndexStartPad);
        var cachedIndex = new IndexReadWindow(this, nid, indexBase,
            Math.Min(indexLength, InodeMetadataLength(nid) - indexBase));

        var context = new CompressedWriteContext
        {
            Nid = nid,
            Inode = inode,
            Header = header,
            HeaderOffset = headerOffset,
            Advise = advise,
            AlgorithmHead1 = algorithmHead1,
            AlgorithmHead2 = algorithmHead2,
            LclusterBits = lclusterBits,
            LclusterSize = lclusterSize,
            TotalIdx = totalIdx,
            CachedIndex = cachedIndex,
            Interlaced = interlaced,
            BigPcluster1 = bigPcluster1,
            BigPcluster2 = bigPcluster2,
            TailPacking = tailPacking,
            FragmentPcluster = fragmentPcluster,
            Output = output,
            Observer = observer,
            Compressed = [],
            Decoded = [],
        };
        try
        {
            if (context.Observer is null
                && TryWriteCompressedClustersInParallel(context, inode, totalIdx, cancellationToken))
            {
                return;
            }

            context.Compressed = ArrayPool<byte>.Shared.Rent(_blockSize * 2);
            context.Decoded = ArrayPool<byte>.Shared.Rent(Math.Max(lclusterSize, _blockSize));
            long lcn = 0;
            while (lcn < context.TotalIdx)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lc = LoadLcluster(context, lcn);
                if (lc.Type == LclusterTypeNonHead)
                {
                    lcn++;
                    continue;
                }

                if (!WriteCompressedClusterRange(context, lc, lcn, out var nextLcn))
                {
                    return;
                }

                lcn = nextLcn;
            }
        }
        finally
        {
            if (context.Compressed.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(context.Compressed);
            }

            if (context.Decoded.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(context.Decoded);
            }
        }
    }

    /// <summary>
    /// Decodes the compressed map header and serves the two layouts that bypass the lcluster loop:
    /// fragment inodes read from the packed inode, and extent-bearing compressed inodes handled by
    /// the extent walker. Returns true when one of these layouts consumed the request.
    /// <para>解码压缩映射头并处理两类绕过 lcluster 循环的布局：fragment inode 从 packed inode 读取，
    /// 携带 extent 的压缩 inode 交给 extent 遍历处理。返回 true 表示其中一种布局已消费本次请求。</para>
    /// </summary>
    private bool TryWriteNonLclusterLayout(ulong nid, UnifiedInode inode, Stream output,
        CompressionObserver? observer, CancellationToken cancellationToken,
        out ZErofsMapHeader header, out long headerOffset, ExtentWriteScratch? extentScratch = null)
    {
        var inodeLoc = InodeOffset(nid);

        headerOffset = AlignUp(inodeLoc + inode.OnDiskSize + inode.XattrISize, 8);
        var headerBytes = ReadInodeMetadata(nid, headerOffset, ZErofsMapHeader.Size, "compressed map header");
        header = ZErofsMapHeader.FromBytes(headerBytes);

        if (header.IsFragmentInode)
        {
            if (observer is not null)
            {
                return true;
            }

            var fragmentOff = BinaryPrimitives.ReadUInt64LittleEndian(headerBytes) & ~(1UL << 63);
            WritePackedData(fragmentOff, checked((long)inode.Size), output);
            return true;
        }

        if (inode.DataLayout == UnifiedDataLayout.Compressed && header.HasExtents)
        {
            if (observer is not null)
            {
                return true;
            }

            var scratch = extentScratch ?? new ExtentWriteScratch();
            var ownsScratch = extentScratch is null;
            try
            {
                WriteExtentsData(nid, inode, header, headerBytes, headerOffset, output, scratch, cancellationToken);
            }
            finally
            {
                if (ownsScratch)
                {
                    scratch.Dispose();
                }
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// Writes one run of logical clusters headed by the lcluster at <paramref name="lcn"/>: it classifies
    /// the run through <see cref="PrepareCompressedClusterRange"/> and dispatches to the streaming plain,
    /// fragment or encoded writer, reporting the cursor for the next loop iteration.
    /// <para>写出以 <paramref name="lcn"/> 处 lcluster 为首的一段逻辑簇：经
    /// <see cref="PrepareCompressedClusterRange"/> 判定该段类型后分派给流式 plain、fragment 或编码写出方法，
    /// 并给出下一次循环迭代的游标。</para>
    /// </summary>
    /// <returns>True when the loop should continue from <paramref name="nextLcn"/>; false when the
    /// observer stopped the whole write and no further cluster should be processed.
    /// <para>返回 true 表示循环应从 <paramref name="nextLcn"/> 继续；返回 false 表示 observer 已终止
    /// 整个写出过程，不再处理后续逻辑簇。</para>
    /// </returns>
    private bool WriteCompressedClusterRange(CompressedWriteContext context, LclusterEntry lc, long lcn, out long nextLcn)
    {
        PrepareCompressedClusterRange(context, lc, lcn, out nextLcn, out var iteration, out var kind);
        switch (kind)
        {
            case CompressedRunKind.Skip:
                return true;
            case CompressedRunKind.Fragment:
                WriteFragmentPcluster(context, iteration!);
                return true;
            case CompressedRunKind.Plain:
                WritePlainLcluster(context, iteration!);
                return true;
            default:
                return WriteEncodedPcluster(context, iteration!);
        }
    }

    /// <summary>
    /// Classifies one logical cluster run without writing anything: it maps the run's logical window,
    /// resolves the next head lcluster, applies the size and decompression-budget checks, and reports
    /// whether the run is skipped (observer probe or out-of-range), streamed by the plain/fragment writers,
    /// or decoded as an encoded pcluster.
    /// <para>判定一段逻辑簇区段而不写出任何内容：映射该段的逻辑窗口、解析下一个 head lcluster、执行大小与
    /// 解压预算校验，并给出该段是跳过（observer 探测或超出范围）、交给 plain/fragment 写出方法，还是作为
    /// 编码 pcluster 解码。</para>
    /// </summary>
    private void PrepareCompressedClusterRange(CompressedWriteContext context, LclusterEntry lc, long lcn,
        out long nextLcn, out ClusterIteration? iteration, out CompressedRunKind kind)
    {
        iteration = null;
        kind = CompressedRunKind.Skip;
        var inode = context.Inode;
        var mappedLogicalOffset = lcn * context.LclusterSize + lc.ClusterOfs;
        if (mappedLogicalOffset >= (long)inode.Size)
        {
            nextLcn = lcn + 1;
            return;
        }

        nextLcn = FindNextHeadLcluster(context, lcn, out var nextMappedLogicalOffset);
        var kernelExpectedOutputSize = checked((int)(Math.Max((long)nextLcn << context.LclusterBits,
            Math.Min(nextMappedLogicalOffset, (long)inode.Size)) - mappedLogicalOffset));

        var llen = Math.Min(nextMappedLogicalOffset, (long)inode.Size) - mappedLogicalOffset;
        if (llen <= 0)
        {
            return;
        }

        if (llen > ErofsCompression.MaxPclusterDecompressedSize)
        {
            throw new FileSystemCorruptedException(
                $"Pcluster decompressed size {llen} exceeds the limit of {ErofsCompression.MaxPclusterDecompressedSize} bytes.");
        }

        var isLastExtent = nextMappedLogicalOffset >= (long)inode.Size;
        var isTailPacked = context.TailPacking && isLastExtent;

        var semiRawLength = 0;
        var isSemiPcluster = lc.Type == LclusterTypePlain && !context.Interlaced && !isTailPacked
            && lc.ClusterOfs == 0 && nextLcn > lcn + 1 && llen > context.LclusterSize
            && context.AlgorithmHead1 is ErofsAlgorithm.Lz4 or ErofsAlgorithm.Deflate;
        if (isSemiPcluster)
        {
            semiRawLength = context.LclusterSize;
        }
        if (context.Observer is not null && (llen > 1 << 20 || lc.Type == LclusterTypePlain || context.FragmentPcluster && isLastExtent))
        {
            return;
        }

        iteration = new ClusterIteration
        {
            Head = lc,
            Lcn = lcn,
            NextLcn = nextLcn,
            MappedLogicalOffset = mappedLogicalOffset,
            Llen = llen,
            KernelExpectedOutputSize = kernelExpectedOutputSize,
            IsLastExtent = isLastExtent,
            IsTailPacked = isTailPacked,
            IsSemiPcluster = isSemiPcluster,
            SemiRawLength = semiRawLength,
        };
        if (context.FragmentPcluster && isLastExtent)
        {
            kind = CompressedRunKind.Fragment;
            return;
        }

        kind = lc.Type == LclusterTypePlain && !isSemiPcluster ? CompressedRunKind.Plain : CompressedRunKind.Encoded;
    }

    /// <summary>
    /// Writes the final extent of a fragment inode by copying its logical bytes out of the packed inode.
    /// <para>从 packed inode 复制 fragment inode 最后一个 extent 的逻辑字节。</para>
    /// </summary>
    private void WriteFragmentPcluster(CompressedWriteContext context, ClusterIteration iteration)
        => WritePackedData(FragmentOffsetOf(context, iteration), iteration.Llen, context.Output);

    /// <summary>
    /// Scans forward from <paramref name="lcn"/> to the next head lcluster, skipping NONHEAD entries,
    /// and reports the logical offset where that head starts, clamped to the file size at the end of
    /// the index.
    /// <para>从 <paramref name="lcn"/> 向前扫描到下一个 head lcluster（跳过 NONHEAD 条目），并给出该
    /// head 的逻辑起始偏移；索引末尾处按文件大小截断。</para>
    /// </summary>
    private long FindNextHeadLcluster(CompressedWriteContext context, long lcn, out long nextMappedLogicalOffset)
    {
        var nextLcn = lcn + 1;
        while (nextLcn < context.TotalIdx)
        {
            var candidate = LoadLcluster(context, nextLcn);
            if (candidate.Type != LclusterTypeNonHead)
            {
                break;
            }

            nextLcn++;
        }
        if (nextLcn < 0)
        {
            nextMappedLogicalOffset = (long)context.Inode.Size;
        }
        else if (nextLcn >= context.TotalIdx)
        {
            nextMappedLogicalOffset = (long)context.Inode.Size;
            nextLcn = context.TotalIdx;
        }
        else
        {
            var nextEntry = LoadLcluster(context, nextLcn);
            nextMappedLogicalOffset = nextLcn * context.LclusterSize + nextEntry.ClusterOfs;
        }

        return nextLcn;
    }

    /// <summary>
    /// Loads a logical cluster entry for the inode described by the compressed write context.
    /// <para>按压缩写出上下文所描述的 inode 加载一个逻辑簇条目。</para>
    /// </summary>
    private LclusterEntry LoadLcluster(CompressedWriteContext context, long lcn)
        => LoadLcluster(context.Nid, context.Inode, context.HeaderOffset, context.Advise, context.LclusterBits,
            context.TotalIdx, lcn, out _, out _, out _, context.CachedIndex);

    /// <summary>
    /// Writes a run whose head lcluster is PLAIN and not semi-compressed, dispatching between the
    /// sequential (non-interlaced) copy and the byte-granular interlaced copy.
    /// <para>写出 head lcluster 为 PLAIN 且非半压缩的区段，在按顺序（非 interlaced）复制与按字节粒度的
    /// interlaced 复制之间分派。</para>
    /// </summary>
    private void WritePlainLcluster(CompressedWriteContext context, ClusterIteration iteration)
    {
        var dataStart = iteration.IsTailPacked
            ? CompressedIndexEnd(context.Nid, context.Inode, context.HeaderOffset, context.TotalIdx, context.LclusterBits)
            : checked(iteration.Head.Pblk * _blockSize);
        if (!context.Interlaced)
        {
            WriteSequentialPlainLcluster(context, iteration, dataStart);
            return;
        }

        WriteInterlacedPlainLcluster(context, iteration, dataStart);
    }

    /// <summary>
    /// Copies a non-interlaced plain run verbatim, either from the metabox or from the image range
    /// checked against the image bounds.
    /// <para>按原样复制非 interlaced 的 plain 区段：metabox inode 从 metabox 读取，其余经镜像边界校验
    /// 后从镜像复制。</para>
    /// </summary>
    private void WriteSequentialPlainLcluster(CompressedWriteContext context, ClusterIteration iteration, long dataStart)
    {
        if (iteration.IsTailPacked && IsMetaboxInode(context.Nid))
        {
            context.Output.Write(ReadMetaboxSpan(dataStart, checked((int)iteration.Llen), "Plain lcluster data"));
        }
        else
        {
            CheckRange(dataStart, iteration.Llen, "Plain lcluster data");
            CopyFromSource(context.Output, dataStart, iteration.Llen);
        }
    }

    /// <summary>
    /// Writes an interlaced plain run: the stored block spans one physical block and the logical
    /// window starts at <c>mappedLogicalOffset</c> modulo the block size, wrapping around the block
    /// end.
    /// <para>写出 interlaced 的 plain 区段：存储块占用一个物理块，逻辑窗口从 <c>mappedLogicalOffset</c>
    /// 对块大小取模的位置开始，并在块尾回绕。</para>
    /// </summary>
    private void WriteInterlacedPlainLcluster(CompressedWriteContext context, ClusterIteration iteration, long dataStart)
    {
        var llen = iteration.Llen;
        if (llen > _blockSize || llen < 0)
        {
            throw new FileSystemCorruptedException(
                $"Interlaced plain pcluster {iteration.Lcn} of nid {context.Nid} has length {llen} > block size {_blockSize}.");
        }

        var skip = (int)(iteration.MappedLogicalOffset & (_blockSize - 1));
        var rawBlock = iteration.IsTailPacked && IsMetaboxInode(context.Nid)
            ? ReadMetaboxBytes(dataStart, _blockSize, "Interlaced plain lcluster block")
            : ReadBytes(dataStart, _blockSize, "Interlaced plain lcluster block");
        var rightpart = Math.Min(_blockSize - skip, (int)llen);
        context.Output.Write(rawBlock, skip, rightpart);
        if (llen > rightpart)
        {
            context.Output.Write(rawBlock, 0, (int)llen - rightpart);
        }
    }

    /// <summary>
    /// Decompresses and writes one encoded pcluster run: it stages the frame into the context buffers,
    /// decompresses it with the head lcluster's algorithm, and either feeds the observer or writes the
    /// semi raw head cluster plus the decoded tail to the output.
    /// <para>解压并写出一段编码 pcluster：把帧暂存到上下文缓冲、按 head lcluster 的算法解压，并将结果交给
    /// observer，或把半压缩的原始头簇与解码尾部写入输出。</para>
    /// </summary>
    /// <returns>True when the caller should continue with the next run; false when the observer
    /// stopped the whole write.
    /// <para>返回 true 表示调用方应继续处理下一段；返回 false 表示 observer 已终止整个写出。</para>
    /// </returns>
    private bool WriteEncodedPcluster(CompressedWriteContext context, ClusterIteration iteration)
    {
        var compressed = context.Compressed;
        var decoded = context.Decoded;
        EncodedPcluster staged;
        try
        {
            staged = PrepareEncodedPcluster(context, iteration, ref compressed, ref decoded);
        }
        finally
        {
            context.Compressed = compressed;
            context.Decoded = decoded;
        }

        DecodeEncodedPcluster(context.Nid, staged);
        if (context.Observer is not null)
        {
            return context.Observer((ErofsCompressionAlgorithm)staged.Algorithm,
                staged.Compressed.AsSpan(staged.InputMargin, staged.CompressedByteLen - staged.InputMargin),
                staged.Decoded.AsSpan(0, staged.DecodedTarget), staged.ClusterOfs, staged.KernelExpectedOutputSize);
        }

        WriteDecodedPcluster(context.Output, staged);
        return true;
    }

    /// <summary>
    /// Validates one encoded pcluster run and stages its encoded bytes into the supplied buffers without
    /// decoding them: it reads the CBLKCNT big-pcluster marker, resolves the physical range, grows the two
    /// buffers only when they are too small, reads the encoded frame, and skips the zero padding ahead of the
    /// codec input.
    /// <para>校验一段编码 pcluster 并把其编码字节暂存到给定缓冲而不解码：读取 CBLKCNT big-pcluster 标记、
    /// 解析物理区间、仅在缓冲不足时扩容、读取编码帧并跳过编解码输入前的零填充。</para>
    /// </summary>
    /// <returns>The staged frame together with the lengths the ordered writer needs.
    /// <para>暂存帧，连同顺序写出所需的长度。</para></returns>
    private EncodedPcluster PrepareEncodedPcluster(CompressedWriteContext context, ClusterIteration iteration,
        ref byte[] compressed, ref byte[] decoded)
    {
        var lc = iteration.Head;
        var lcn = iteration.Lcn;
        var llen = iteration.Llen;
        var compressedBlocks = 1;
        var headType = lc.Type;
        var head1NotBig = headType == LclusterTypeHead1 && !context.BigPcluster1;
        var plainOrHead2NotBig = (headType == LclusterTypePlain || headType == LclusterTypeHead2) && !context.BigPcluster2;
        var atEof = (lcn << context.LclusterBits) >= (long)context.Inode.Size;
        if (!iteration.IsTailPacked && !head1NotBig && !plainOrHead2NotBig && !atEof && lcn + 1 < context.TotalIdx)
        {
            var next = LoadLcluster(context, lcn + 1);
            if (next.Type != LclusterTypeNonHead)
            {
                compressedBlocks = 1;
            }
            else
            {
                if (next.Delta0 != 1 || next.CompressedBlocks == 0)
                {
                    throw new FileSystemCorruptedException(
                        $"Bogus CBLKCNT @ lcn {lcn + 1} of nid {context.Nid}.");
                }

                compressedBlocks = next.CompressedBlocks;
            }
        }

        var compressedByteLen = iteration.IsTailPacked
            ? context.Header.IdataSize
            : head1NotBig || plainOrHead2NotBig
                ? checked(1 << context.LclusterBits)
                : checked(compressedBlocks * _blockSize);
        if (compressedByteLen <= 0 || !iteration.IsTailPacked && lc.Pblk < 0 || compressedByteLen > ErofsCompression.MaxPclusterSize)
        {
            throw new FileSystemCorruptedException($"Invalid compressed pcluster for nid {context.Nid}.");
        }

        if (iteration.IsSemiPcluster && compressedByteLen <= iteration.SemiRawLength)
        {
            throw new FileSystemCorruptedException(
                $"Semi-compressed pcluster {lcn} of nid {context.Nid} has no encoded tail after its raw head cluster ({compressedByteLen} disk bytes, {iteration.SemiRawLength} raw bytes).");
        }

        if (headType == LclusterTypePlain && llen > compressedByteLen)
        {
            throw new FileSystemCorruptedException(
                $"Invalid PLAIN head lcluster {lcn} of nid {context.Nid}: logical length {llen} exceeds physical length {compressedByteLen}.");
        }

        var pblkStart = iteration.IsTailPacked
            ? CompressedIndexEnd(context.Nid, context.Inode, context.HeaderOffset, context.TotalIdx, context.LclusterBits)
            : checked(lc.Pblk * _blockSize);
        EnsureEncodedBuffer(ref compressed, compressedByteLen);
        EnsureEncodedBuffer(ref decoded, checked((int)llen));

        if (iteration.IsTailPacked && IsMetaboxInode(context.Nid))
        {
            ReadMetaboxSpan(pblkStart, compressedByteLen, "Compressed pcluster data")
                .CopyTo(compressed.AsSpan(0, compressedByteLen));
        }
        else
        {
            CheckRange(pblkStart, compressedByteLen, "Compressed pcluster data");
            ReadPositioned(_stream, checked(_baseOffset + pblkStart), compressed, compressedByteLen);
        }

        var inputMargin = iteration.SemiRawLength;
        while (!iteration.IsTailPacked && inputMargin < compressedByteLen && compressed[inputMargin] == 0)
        {
            inputMargin++;
        }

        if (inputMargin >= compressedByteLen)
        {
            throw new FileSystemCorruptedException(
                $"Pcluster {lcn} of nid {context.Nid} is entirely zero-padded (no compressed data).");
        }

        return new EncodedPcluster(
            lcn,
            headType == LclusterTypeHead2 ? context.AlgorithmHead2 : context.AlgorithmHead1,
            compressed,
            decoded,
            compressedByteLen,
            inputMargin,
            (int)llen - iteration.SemiRawLength,
            iteration.SemiRawLength,
            iteration.KernelExpectedOutputSize,
            lc.ClusterOfs);
    }

    /// <summary>
    /// Decodes one staged encoded pcluster into its decoded buffer and verifies the exact decoded length;
    /// a failure names the pcluster's logical position so a parallel batch can surface the earliest one.
    /// <para>把一个已暂存的编码 pcluster 解码到其解码缓冲并校验精确长度；失败信息给出该 pcluster 的逻辑位置，
    /// 使并行批次能上报逻辑顺序最靠前的一个。</para>
    /// </summary>
    private void DecodeEncodedPcluster(ulong nid, in EncodedPcluster staged)
    {
        int decodedLen;
        try
        {
            decodedLen = DecompressPcluster(staged.Algorithm, staged.Compressed, staged.InputMargin,
                staged.CompressedByteLen - staged.InputMargin, staged.Decoded, staged.DecodedTarget);
        }
        catch (Exception ex) when (ex is InvalidDataException or FileSystemException or InvalidOperationException or ArgumentException)
        {
            throw new FileSystemCorruptedException(
                $"Pcluster {staged.Lcn} of nid {nid} failed to decompress ({staged.Algorithm}): {ex.Message}");
        }

        if (decodedLen != staged.DecodedTarget)
        {
            throw new FileSystemCorruptedException(
                $"Pcluster {staged.Lcn} of nid {nid} decoded to {decodedLen} bytes, expected {staged.DecodedTarget}.");
        }
    }

    /// <summary>
    /// Writes one decoded pcluster in logical order: the raw head cluster of a semi-compressed pcluster
    /// comes first, then the decoded tail.
    /// <para>按逻辑顺序写出一个已解码 pcluster：半压缩 pcluster 先写原始头簇，再写解码尾部。</para>
    /// </summary>
    private static void WriteDecodedPcluster(Stream output, in EncodedPcluster staged)
    {
        if (staged.SemiRawLength > 0)
        {
            output.Write(staged.Compressed, 0, staged.SemiRawLength);
        }
        output.Write(staged.Decoded, 0, staged.DecodedTarget);
    }

    /// <summary>
    /// Grows one pooled staging buffer to at least <paramref name="required"/> bytes, returning the
    /// previous array to the pool first; a buffer that is already large enough is kept untouched.
    /// <para>把一个池化暂存缓冲扩大到至少 <paramref name="required"/> 字节，先归还旧数组；已经足够大的
    /// 缓冲保持不变。</para>
    /// </summary>
    private static void EnsureEncodedBuffer(ref byte[] buffer, int required)
    {
        if (required <= buffer.Length)
        {
            return;
        }

        if (buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        buffer = ArrayPool<byte>.Shared.Rent(required);
    }

    /// <summary>
    /// Runs the bounded decode pipeline for one inode when the shared lane budget affords decode workers beyond
    /// the calling thread, claiming those lanes for the pipeline's lifetime. Returns false when the sequential
    /// walk must write the pclusters instead, which is the case for small inodes and when the concurrent
    /// extractions already hold the whole budget.
    /// <para>当共享通道预算能提供调用线程之外的解码工作者时，为单个 inode 运行有界解码流水线，并在流水线存活期间
    /// 占用这些通道。必须由顺序遍历写出 pcluster 时返回 false：inode 过小，或并发解包已占满整个预算。</para>
    /// </summary>
    /// <param name="context">Per-call compressed write state. <para>每次调用的压缩写出状态。</para></param>
    /// <param name="inode">Inode whose data is written. <para>正在写出其数据的 inode。</para></param>
    /// <param name="frameCount">Number of pclusters the inode index yields.
    /// <para>inode 索引给出的 pcluster 数量。</para></param>
    /// <param name="cancellationToken">Token checked while the pipeline prefetches frames.
    /// <para>流水线预取帧期间检查的取消令牌。</para></param>
    /// <returns>True when the pipeline wrote every run; false when the caller must run the sequential walk.
    /// <para>流水线写出全部区段时为 true；调用方必须运行顺序遍历时为 false。</para></returns>
    private bool TryWriteCompressedClustersInParallel(CompressedWriteContext context, UnifiedInode inode,
        int frameCount, CancellationToken cancellationToken)
    {
        if (frameCount <= 1 || (long)inode.Size <= PipelinedDecodeMinInodeBytes)
        {
            return false;
        }

        var requested = Core.FirmwareParallelism.BudgetedWorkers(
            bytesPerWorker: CompressedPclusterWorkerBytes,
            budgetBytes: Core.FirmwareParallelism.WorkerMemoryBudgetBytes,
            requestedWorkers: Core.FirmwareParallelism.Workers);
        var workers = DecodeLaneBudget.ClaimWorkers(requested, frameCount);
        if (workers <= 1)
        {
            DecodeLaneBudget.ReleaseWorkers(workers);
            return false;
        }

        try
        {
            WriteCompressedClustersInParallel(context, workers, cancellationToken);
            return true;
        }
        finally
        {
            DecodeLaneBudget.ReleaseWorkers(workers);
        }
    }

    /// <summary>
    /// Decodes the compressed clusters of one inode through a bounded pipeline: the calling thread walks the
    /// lcluster index and stages every encoded frame, so source-stream positioning stays single-threaded; one
    /// long-lived worker per ring slot decodes the frame staged there while the following frames are read, and
    /// the decoded frames are written in logical order. Plain and fragment runs are streamed on the calling
    /// thread once every earlier frame is written, and a decode failure is surfaced by the logically earliest
    /// slot, which keeps the sequential walk's error semantics.
    /// <para>通过有界流水线解码单个 inode 的压缩簇：调用线程遍历 lcluster 索引并暂存每个编码帧，因此数据源定位
    /// 仍为单线程；每个环形槽位各有一个长期工作者，在后续帧被读取时解码已暂存的帧，随后按逻辑顺序写出已解码帧。
    /// plain 与 fragment 区段在先前的帧全部写出后于调用线程上流式写出；解码失败按逻辑顺序最靠前的槽位上报，
    /// 从而保持串行遍历的错误语义。</para>
    /// </summary>
    private void WriteCompressedClustersInParallel(CompressedWriteContext context, int workers,
        CancellationToken cancellationToken)
    {
        using var pipeline = new CompressedDecodePipeline(this, context.Nid, workers);
        var lcn = 0L;
        var produced = 0;
        var written = 0;
        ClusterIteration? direct = null;
        var directKind = CompressedRunKind.Skip;
        var directNextLcn = 0L;
        var producerDone = false;
        ExceptionDispatchInfo? producerFailure = null;

        try
        {
            while (true)
            {
                if (direct is not null)
                {
                    if (written < produced)
                    {
                        WritePipelineFrame(context, pipeline, written, cancellationToken);
                        written++;
                        continue;
                    }

                    if (directKind == CompressedRunKind.Fragment)
                    {
                        WriteFragmentPcluster(context, direct);
                    }
                    else
                    {
                        WritePlainLcluster(context, direct);
                    }

                    lcn = directNextLcn;
                    direct = null;
                    continue;
                }

                while (!producerDone && produced - written < workers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (lcn >= context.TotalIdx)
                        {
                            producerDone = true;
                            break;
                        }

                        var lc = LoadLcluster(context, lcn);
                        if (lc.Type == LclusterTypeNonHead)
                        {
                            lcn++;
                            continue;
                        }

                        PrepareCompressedClusterRange(context, lc, lcn, out var nextLcn, out var iteration,
                            out var kind);
                        if (kind == CompressedRunKind.Skip)
                        {
                            lcn = nextLcn;
                            continue;
                        }

                        if (kind != CompressedRunKind.Encoded)
                        {
                            direct = iteration;
                            directKind = kind;
                            directNextLcn = nextLcn;
                            break;
                        }

                        var slot = pipeline.SlotAt(produced);
                        slot.Frame = PrepareEncodedPcluster(context, iteration!, ref slot.Compressed,
                            ref slot.Decoded);
                        slot.Failure = null;
                        slot.Prepare();
                        produced++;
                        pipeline.Publish(produced);
                        lcn = nextLcn;
                    }
                    catch (Exception ex)
                    {
                        producerFailure = ExceptionDispatchInfo.Capture(ex);
                        producerDone = true;
                        break;
                    }
                }

                if (written < produced)
                {
                    WritePipelineFrame(context, pipeline, written, cancellationToken);
                    written++;
                    continue;
                }

                if (producerDone)
                {
                    break;
                }
            }
        }
        finally
        {
            pipeline.Stop();
        }

        producerFailure?.Throw();
    }

    /// <summary>
    /// Writes the pipeline frame at <paramref name="index"/> in logical order: it waits for that slot's decode,
    /// surfaces the failure recorded there, and forwards the decoded bytes to the output.
    /// <para>按逻辑顺序写出第 <paramref name="index"/> 个流水线帧：等待该槽位解码完成，上报其上记录的失败，并把
    /// 解码字节转发给输出。</para>
    /// </summary>
    private static void WritePipelineFrame(CompressedWriteContext context, CompressedDecodePipeline pipeline,
        int index, CancellationToken cancellationToken)
    {
        var slot = pipeline.SlotAt(index);
        slot.Ready.Wait(cancellationToken);
        if (slot.Failure is { } failure)
        {
            failure.Throw();
        }

        WriteDecodedPcluster(context.Output, slot.Frame);
    }

    /// <summary>How one logical cluster run is written: skipped, streamed as plain or fragment data, or decoded as an encoded pcluster.
    /// <para>一个逻辑簇区段的写出方式：跳过、作为 plain 或 fragment 流式写出，或作为编码 pcluster 解码。</para></summary>
    private enum CompressedRunKind
    {
        Skip,
        Plain,
        Fragment,
        Encoded,
    }

    /// <summary>One encoded pcluster whose frame is staged and waiting for a worker to decode it.
    /// <para>编码帧已暂存、等待工作者解码的单个编码 pcluster。</para></summary>
    private readonly record struct EncodedPcluster(
        long Lcn,
        ErofsAlgorithm Algorithm,
        byte[] Compressed,
        byte[] Decoded,
        int CompressedByteLen,
        int InputMargin,
        int DecodedTarget,
        int SemiRawLength,
        int KernelExpectedOutputSize,
        int ClusterOfs);

    /// <summary>
    /// Per-call state shared by the compressed write loop and its branch writers: inode identity,
    /// map header fields, index geometry, advise flags, the output sink and the pooled scratch
    /// buffers.
    /// <para>压缩写出循环及其分支写出方法共享的每次调用状态：inode 标识、映射头字段、索引几何参数、
    /// advise 标志、输出流与池化临时缓冲。</para>
    /// </summary>
    private sealed class CompressedWriteContext
    {
        public required ulong Nid { get; init; }
        public required UnifiedInode Inode { get; init; }
        public required ZErofsMapHeader Header { get; init; }
        public required long HeaderOffset { get; init; }
        public required ushort Advise { get; init; }
        public required ErofsAlgorithm AlgorithmHead1 { get; init; }
        public required ErofsAlgorithm AlgorithmHead2 { get; init; }
        public required int LclusterBits { get; init; }
        public required int LclusterSize { get; init; }
        public required int TotalIdx { get; init; }
        public required IndexReadWindow CachedIndex { get; init; }
        public required bool Interlaced { get; init; }
        public required bool BigPcluster1 { get; init; }
        public required bool BigPcluster2 { get; init; }
        public required bool TailPacking { get; init; }
        public required bool FragmentPcluster { get; init; }
        public required Stream Output { get; init; }
        public required CompressionObserver? Observer { get; init; }

        /// <summary>Pooled encoded-input buffer of the sequential walk, rented only when that walk runs; the pipeline keeps one staging pair per ring slot instead.
        /// <para>串行遍历使用的池化压缩输入缓冲，仅在该遍历运行时租借；流水线改为每个环形槽位一对暂存缓冲。</para></summary>
        public required byte[] Compressed { get; set; }

        /// <summary>Pooled decoded-output buffer of the sequential walk, rented only when that walk runs; the pipeline keeps one staging pair per ring slot instead.
        /// <para>串行遍历使用的池化解码输出缓冲，仅在该遍历运行时租借；流水线改为每个环形槽位一对暂存缓冲。</para></summary>
        public required byte[] Decoded { get; set; }
    }

    /// <summary>
    /// Per-call pooled extent buffers shared by every extent of one compressed-inode walk: the
    /// encoded-input and decoded-output arrays grow in place to the largest extent seen so far, so a
    /// file with many extents rents from the pool a small number of times instead of once per extent.
    /// Instances are call-scoped because parser instances are shared across threads.
    /// <para>单次调用内、被一个压缩 inode 的全部 extent 共享的池化缓冲：压缩输入与解码输出数组按
    /// 已遇到的最大 extent 就地扩容，因此含大量 extent 的文件只从池中租借少量次数而非每个 extent
    /// 一次。实例必须按调用作用域持有，因为解析器实例是跨线程共享的。</para>
    /// </summary>
    internal sealed class ExtentWriteScratch : IDisposable
    {
        /// <summary>Pooled encoded-extent buffer, at least the last requested encoded length.
        /// <para>池化的压缩 extent 缓冲，不小于最近一次请求的压缩长度。</para></summary>
        internal byte[] Compressed = [];

        /// <summary>Pooled decoded-extent buffer, at least the last requested logical length.
        /// <para>池化的解码 extent 缓冲，不小于最近一次请求的逻辑长度。</para></summary>
        internal byte[] Decoded = [];

        /// <summary>Number of pool rentals this scratch has performed.
        /// <para>本缓冲组已执行的池租借次数。</para></summary>
        internal int RentCount { get; private set; }

        /// <summary>
        /// Grows the two buffers in place only when an extent needs more bytes than they currently
        /// hold; a buffer that is already large enough is kept untouched. Growth doubles the previous
        /// capacity so alternating extent sizes do not re-rent on every step.
        /// <para>仅在某个 extent 需要的字节数超过当前容量时就地扩容两个缓冲；已经足够大的缓冲保持不动。
        /// 扩容按上一容量倍增，避免大小交替的 extent 每一步都重新租借。</para>
        /// </summary>
        /// <param name="compressedLength">Encoded extent bytes to read; must be non-negative. <para>待读取的压缩 extent 字节数，不得为负。</para></param>
        /// <param name="decodedLength">Logical extent bytes to decode; must be non-negative. <para>待解压的逻辑 extent 字节数，不得为负。</para></param>
        /// <exception cref="ArgumentOutOfRangeException">Either length is negative. <para>任一长度为负数。</para></exception>
        internal void Ensure(int compressedLength, int decodedLength)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(compressedLength);
            ArgumentOutOfRangeException.ThrowIfNegative(decodedLength);
            if (Compressed.Length < compressedLength)
            {
                Compressed = Grow(Compressed, compressedLength);
            }
            if (Decoded.Length < decodedLength)
            {
                Decoded = Grow(Decoded, decodedLength);
            }
        }

        /// <summary>Releases both buffers back to the pool, leaving empty arrays behind.
        /// <para>把两个缓冲归还池中，并留下空数组。</para></summary>
        internal void Release()
        {
            if (Compressed.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(Compressed);
            }
            if (Decoded.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(Decoded);
            }
            Compressed = [];
            Decoded = [];
        }

        /// <summary>Releases the pooled buffers owned by this scratch. <para>释放本缓冲组持有的池化缓冲。</para></summary>
        public void Dispose() => Release();

        private byte[] Grow(byte[] current, int required)
        {
            if (current.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(current);
            }
            var capacity = current.Length == 0 || current.Length > int.MaxValue / 2
                ? required
                : Math.Max(required, current.Length * 2);
            RentCount++;
            return ArrayPool<byte>.Shared.Rent(capacity);
        }
    }

    /// <summary>
    /// Per-iteration state of one logical cluster run inside the compressed write loop, shared by
    /// the plain, interlaced and encoded pcluster writers.
    /// <para>压缩写出循环中单个逻辑簇区段的每次迭代状态，供 plain、interlaced 与压缩 pcluster 写出
    /// 方法共享。</para>
    /// </summary>
    private sealed class ClusterIteration
    {
        public required LclusterEntry Head { get; init; }
        public required long Lcn { get; init; }
        public required long NextLcn { get; init; }
        public required long MappedLogicalOffset { get; init; }
        public required long Llen { get; init; }
        public required int KernelExpectedOutputSize { get; init; }
        public required bool IsLastExtent { get; init; }
        public required bool IsTailPacked { get; init; }
        public required bool IsSemiPcluster { get; init; }
        public required int SemiRawLength { get; init; }
    }

    /// <summary>
    /// Returns the largest logical <c>clusterofs</c> among all compressed head lclusters, i.e. the
    /// greatest nonzero SourceOffset modulo the logical cluster size. Standards-conforming images
    /// place every head lcluster at its cluster start (clusterofs == 0); only byte-granular packing
    /// (the dense planner) produces a non-zero clusterofs, which the legacy (pre-5.13) decoders
    /// cannot address.
    /// <para>返回所有压缩 head lcluster 中最大的逻辑 clusterofs，即 SourceOffset 对逻辑簇大小取模的
    /// 最大值。符合规范的镜像把每个 head lcluster 放在其逻辑簇起点（clusterofs == 0）；只有按
    /// 字节粒度打包（dense 规划器）才会产生非零 clusterofs，这是 legacy（pre-5.13）解码器
    /// 无法寻址的布局。</para>
    /// </summary>
    internal int MaxCompressedClusterOfs()
    {
        var largest = 0;
        foreach (var entry in EnumerateEntries())
        {
            if (entry.IsDirectory || entry.IsSymlink || entry.IsSpecialFile) continue;
            var inode = ReadInode(entry.Nid);
            if (!inode.IsCompressed) continue;
            WriteCompressedData(entry.Nid, inode, Stream.Null, (_, _, _, clusterOfs, _) =>
            {
                largest = Math.Max(largest, clusterOfs);
                return true;
            });
        }

        return largest;
    }

    /// <summary>
    /// Returns the largest compressed stream (in bytes) among all pclusters. A value above the block
    /// size proves the image stores a genuine multi-block big pcluster, whose index head carries the
    /// CBLKCNT marker so one extent spans several physical blocks.
    /// <para>返回所有 pcluster 中最大的压缩流字节数。大于块大小即证明镜像存储了真正的多块 big
    /// pcluster：其索引 head 携带 CBLKCNT 标记，使单个 extent 跨越多个物理块。</para>
    /// </summary>
    internal int MaxCompressedPclusterBytes()
    {
        var largest = 0;
        foreach (var entry in EnumerateEntries())
        {
            if (entry.IsDirectory || entry.IsSymlink || entry.IsSpecialFile) continue;
            var inode = ReadInode(entry.Nid);
            if (!inode.IsCompressed) continue;
            WriteCompressedData(entry.Nid, inode, Stream.Null, (_, encoded, _, _, _) =>
            {
                largest = Math.Max(largest, encoded.Length);
                return true;
            });
        }

        return largest;
    }

    /// <summary>
    /// Returns the largest shortfall, over all compressed pclusters, between the output a
    /// legacy (pre-big-pcluster) decoder demands (covered whole logical clusters, unclamped by i_size) and
    /// the bytes the stored stream actually decodes to. Zero is required for images that must
    /// mount on legacy (pre-5.13) implementations; a positive value reproduces the device-side
    /// "failed to decompress %d in[...] out[...]" corruption. Big-pcluster-aware decoders clamp
    /// the final extent to i_size, so images using that layout can legitimately report a deficit.
    /// <para>返回所有压缩 pcluster 中，“legacy（pre-big-pcluster）解码器要求的输出（覆盖的整逻辑簇数，不按
    /// i_size 裁剪）”与“实际解压字节数”之间的最大缺口。需要在 legacy（pre-5.13）实现上挂载的镜像必须
    /// 为 0；大于 0 即复现设备端 "failed to decompress %d in[...] out[...]" 故障。支持
    /// big-pcluster 的解码器会把最后一个 extent 裁剪到 i_size，因此采用该布局的镜像出现缺口属正常。</para>
    /// </summary>
    internal int MaxPclusterKernelCoverageDeficit()
    {
        var largest = 0;
        foreach (var entry in EnumerateEntries())
        {
            if (entry.IsDirectory || entry.IsSymlink || entry.IsSpecialFile) continue;
            var inode = ReadInode(entry.Nid);
            if (!inode.IsCompressed) continue;
            WriteCompressedData(entry.Nid, inode, Stream.Null, (_, _, decoded, _, kernelExpected) =>
            {
                largest = Math.Max(largest, kernelExpected - decoded.Length);
                return true;
            });
        }

        return largest;
    }

    /// <summary>Diagnostic companion to <see cref="MaxPclusterKernelCoverageDeficit"/> listing every
    /// compressed extent whose legacy-expected output exceeds its decoded bytes.
    /// <para><see cref="MaxPclusterKernelCoverageDeficit"/> 的诊断伴侣，列出所有 pre-5.13 期望输出大于实际
    /// 解压字节数的压缩 extent。</para></summary>
    internal List<(string Path, int Expected, int Decoded)> PclusterKernelCoverageDeficits()
    {
        var report = new List<(string, int, int)>();
        foreach (var entry in EnumerateEntries())
        {
            if (entry.IsDirectory || entry.IsSymlink || entry.IsSpecialFile) continue;
            var inode = ReadInode(entry.Nid);
            if (!inode.IsCompressed) continue;
            var path = entry.Path;
            WriteCompressedData(entry.Nid, inode, Stream.Null, (_, _, decoded, _, kernelExpected) =>
            {
                if (kernelExpected > decoded.Length)
                {
                    report.Add((path, kernelExpected, decoded.Length));
                }
                return true;
            });
        }

        return report;
    }

    /// <summary>Diagnostic companion listing one file's logical cluster index entries exactly as a
    /// conformant decoder reads them, to compare a rebuild's index against the verified source image.
    /// <para>诊断伴侣：按合规解码器的读取方式列出指定文件的每个逻辑簇索引项，用于把重建镜像的索引与已校验
    /// 的源镜像逐项对照。</para></summary>
    internal List<(long Lcn, byte Type, int ClusterOfs, long Pblk, uint Delta0, uint Delta1)> DumpIndexClusters(string path)
    {
        var wanted = path.TrimStart('/');
        var match = EnumerateEntries().First(entry => string.Equals(entry.Path.TrimStart('/'), wanted, StringComparison.Ordinal));
        return IndexRows(match.Nid, ReadInode(match.Nid));
    }

    /// <summary>Diagnostic companion listing the last index entries of every regular compressed
    /// file, which is where a rebuild and its source image tend to diverge.
    /// <para>诊断伴侣：列出每个常规压缩文件的最后若干索引项，重建与源镜像的差异通常就出现在尾部。</para></summary>
    internal List<(string Path, long Lcn, byte Type, int ClusterOfs, long Pblk)> TailIndexClusters(int count)
    {
        var report = new List<(string, long, byte, int, long)>();
        foreach (var entry in EnumerateEntries())
        {
            if (entry.IsDirectory || entry.IsSymlink || entry.IsSpecialFile) continue;
            var inode = ReadInode(entry.Nid);
            if (!inode.IsCompressed) continue;
            var rows = IndexRows(entry.Nid, inode);
            foreach (var row in rows.Skip(Math.Max(0, rows.Count - count)))
                report.Add((entry.Path, row.Lcn, row.Type, row.ClusterOfs, row.Pblk));
        }

        return report;
    }

    private List<(long Lcn, byte Type, int ClusterOfs, long Pblk, uint Delta0, uint Delta1)> IndexRows(ulong nid, UnifiedInode inode)
    {
        var headerOffset = AlignUp(InodeOffset(nid) + inode.OnDiskSize + inode.XattrISize, 8);
        var header = ZErofsMapHeader.FromBytes(ReadInodeMetadata(nid, headerOffset, ZErofsMapHeader.Size, "compressed map header"));
        var lclusterBits = _superblock.BlockSizeBits + (header.ClusterBits & ZErofsMapHeader.LclusterBitsMask);
        var totalIdx = ErofsCompression.PclusterCount(inode.Size, 1 << lclusterBits);
        var rows = new List<(long, byte, int, long, uint, uint)>(totalIdx);
        for (var lcn = 0L; lcn < totalIdx; lcn++)
        {
            var lc = LoadLcluster(nid, inode, headerOffset, header.Advise, lclusterBits, totalIdx, lcn,
                out _, out _, out _);
            rows.Add((lcn, lc.Type, lc.ClusterOfs, lc.Pblk, lc.Delta0, lc.Delta1));
        }

        return rows;
    }

    /// <summary>
    /// Writes one file's data with the extent scratch buffers supplied by the caller, so a repeated
    /// read path can reuse one pooled pair instead of renting per walk.
    /// <para>使用调用方给出的 extent 缓冲组写出单个文件的数据，使重复读取路径复用同一对池化缓冲，
    /// 而不是每次遍历都重新租借。</para>
    /// </summary>
    /// <param name="path">File path as reported by <see cref="EnumerateFiles"/>. <para>按 <see cref="EnumerateFiles"/> 报告的文件路径。</para></param>
    /// <param name="output">Sink receiving the decoded bytes. <para>接收解码字节的写出流。</para></param>
    /// <param name="scratch">Caller-owned extent buffers. <para>调用方持有的 extent 缓冲组。</para></param>
    /// <param name="cancellationToken">Token checked between extents. <para>在 extent 之间检查的取消令牌。</para></param>
    /// <exception cref="FileNotFoundException">No entry carries <paramref name="path"/>. <para>没有任何条目携带 <paramref name="path"/>。</para></exception>
    internal void WriteCompressedExtentData(string path, Stream output, ExtentWriteScratch scratch,
        CancellationToken cancellationToken = default)
    {
        var nid = FindFile(path);
        WriteCompressedData(nid, ReadInode(nid), output, null, cancellationToken, scratch);
    }

    /// <summary>Diagnostic companion reporting where a compressed inode's map header lives in the image,
    /// which is also where the extent index records that follow it begin.
    /// <para>诊断伴侣：报告压缩 inode 映射头在镜像中的位置，该位置之后即其 extent 索引记录的起点。</para></summary>
    /// <param name="path">File path as reported by <see cref="EnumerateFiles"/>. <para>按 <see cref="EnumerateFiles"/> 报告的文件路径。</para></param>
    /// <returns>The byte offset of the map header from the stream base. <para>映射头相对流起点的字节偏移。</para></returns>
    /// <exception cref="FileNotFoundException">No entry carries <paramref name="path"/>. <para>没有任何条目携带 <paramref name="path"/>。</para></exception>
    internal long CompressedMapHeaderOffset(string path)
    {
        var nid = FindFile(path);
        var inode = ReadInode(nid);
        return AlignUp(InodeOffset(nid) + inode.OnDiskSize + inode.XattrISize, 8);
    }

    /// <summary>
    /// Reads a COMRESSED_FULL inode whose map header carries extent metadata: the index area is
    /// a sorted array of variable-size z_erofs_extent records.
    /// The encoded and decoded extent buffers come from <paramref name="scratch"/>, which the caller
    /// owns for the whole walk so consecutive extents reuse them.
    /// <para>读取携带 extent 元数据的 COMRESSED_FULL inode：索引区为按逻辑偏移排序的可变长度
    /// z_erofs_extent 记录数组。压缩与解码缓冲来自
    /// <paramref name="scratch"/>，由调用方在整个遍历期间持有，使相邻 extent 复用同一对缓冲。</para>
    /// </summary>
    private void WriteExtentsData(ulong nid, UnifiedInode inode, ZErofsMapHeader header, byte[] headerBytes,
        long headerOffset, Stream output, ExtentWriteScratch scratch, CancellationToken cancellationToken,
        LogicalWindow? window = null)
    {
        var effectiveWindow = window ?? LogicalWindow.Whole;
        var extentCount = (ulong)(BinaryPrimitives.ReadUInt32LittleEndian(headerBytes)
            | ((ulong)header.ExtentsHi << 32));
        var recSize = ZErofsMapHeader.ExtentRecordSize(header.Advise);
        var extentsBase = ZErofsMapHeader.ExtentAreaBase(headerOffset, recSize);
        if (recSize is 4 or 8)
        {
            WriteShortExtentsData(nid, inode, header, extentsBase, recSize, output, scratch, cancellationToken,
                window);
            return;
        }
        if (extentCount == 0)
        {
            if (inode.Size == 0)
            {
                return;
            }
            throw new FileSystemCorruptedException($"EROFS extent count is zero for nonempty inode {nid}.");
        }
        if (recSize < 8)
        {
            throw new FileSystemCorruptedException($"EROFS extent record size {recSize} is too small.");
        }

        const int maxExtents = 1 << 24;
        if (extentCount > (ulong)maxExtents)
        {
            throw new FileSystemCorruptedException($"EROFS extent count {extentCount} exceeds the limit of {maxExtents}.");
        }

        var logicalOffset = 0L;
        for (var i = 0; i < (long)extentCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recordOffset = checked(extentsBase + i * recSize);
            var record = ReadInodeMetadata(nid, recordOffset, recSize, $"extent record {i}");
            var plenRaw = BinaryPrimitives.ReadUInt32LittleEndian(record);
            var logicalStart = checked((long)(BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(12))
                | (recSize > 16 ? (ulong)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(16)) << 32 : 0)));

            if (logicalStart != logicalOffset)
            {
                throw new FileSystemCorruptedException($"EROFS extent {i} of nid {nid} is not sorted.");
            }

            var logicalEnd = i + 1 < (long)extentCount
                ? ReadExtentLogicalStart(nid, checked(recordOffset + recSize), recSize)
                : checked((long)inode.Size);
            if (logicalEnd <= logicalStart || logicalEnd > (long)inode.Size)
            {
                throw new FileSystemCorruptedException($"EROFS extent {i} of nid {nid} has an invalid logical range.");
            }
            var logicalLength = logicalEnd - logicalStart;
            if (logicalEnd <= effectiveWindow.Start)
            {
                logicalOffset = logicalEnd;
                continue;
            }
            if (logicalStart >= effectiveWindow.End)
            {
                break;
            }
            effectiveWindow.GetRange(logicalStart, logicalLength, out var writeFrom, out var writeLength);
            if (writeLength <= 0)
            {
                logicalOffset = logicalEnd;
                continue;
            }
            var physicalLength = plenRaw & ZErofsMapHeader.ExtentPlenMask;
            var fmt = plenRaw >> ZErofsMapHeader.ExtentPlenFmtBit;
            var physicalOffset = checked((long)(BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(4))
                | (ulong)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(8)) << 32));
            if (fmt > (uint)ErofsAlgorithm.Zstd + 1)
            {
                throw new FileSystemCorruptedException($"EROFS extent {i} of nid {nid} uses unknown algorithm {fmt - 1}.");
            }

            if (i == (long)extentCount - 1
                && (header.Advise & ZErofsMapHeader.AdviseFragmentPcluster) != 0)
            {
                WritePackedData((((ulong)physicalOffset << 32) | plenRaw) + (ulong)writeFrom, writeLength, output);
            }
            else if (physicalLength == 0)
            {
                WriteZeroes(output, writeLength);
            }
            else if (fmt == 0)
            {
                WritePlainExtent(nid, inode, record, recSize, logicalStart, logicalLength, physicalOffset, output,
                    cancellationToken, writeFrom, writeLength);
            }
            else
            {
                WriteCompressedExtent(nid, inode, record, recSize, logicalStart, logicalLength,
                    (ErofsAlgorithm)(fmt - 1), output, scratch, cancellationToken, physicalOffset, writeFrom,
                    writeLength);
            }

            logicalOffset = logicalEnd;
        }

        if (window is null && logicalOffset != (long)inode.Size)
        {
            throw new FileSystemCorruptedException($"EROFS extents of nid {nid} do not cover the file.");
        }
    }

    private long ReadExtentLogicalStart(ulong nid, long offset, int recordSize)
    {
        var record = ReadInodeMetadata(nid, offset, recordSize, "extent logical start");
        return checked((long)(BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(12))
            | (recordSize > 16 ? (ulong)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(16)) << 32 : 0)));
    }

    private void WriteShortExtentsData(ulong nid, UnifiedInode inode, ZErofsMapHeader header,
        long indexStart, int recordSize, Stream output, ExtentWriteScratch scratch, CancellationToken cancellationToken,
        LogicalWindow? window = null)
    {
        var effectiveWindow = window ?? LogicalWindow.Whole;
        if (inode.Size == 0)
        {
            return;
        }
        var clusterBits = _superblock.BlockSizeBits + (header.ClusterBits & ZErofsMapHeader.LclusterBitsMask);
        if (clusterBits > 23)
        {
            throw new FileSystemCorruptedException($"EROFS extent cluster size shift {clusterBits} exceeds the supported limit.");
        }
        var clusterSize = 1L << clusterBits;
        if (clusterSize > ErofsCompression.MaxPclusterDecompressedSize)
        {
            throw new FileSystemCorruptedException("EROFS short extent exceeds the decompression budget.");
        }
        var count = checked(((long)inode.Size - 1) / clusterSize + 1);
        if (count > 1 << 24)
        {
            throw new FileSystemCorruptedException("EROFS short extent count exceeds the supported limit.");
        }
        var physical = recordSize == 4
            ? checked((long)BinaryPrimitives.ReadUInt64LittleEndian(
                ReadInodeMetadata(nid, indexStart, 8, "short extent initial physical address")))
            : 0L;
        var indexBase = checked(indexStart + (recordSize == 4 ? 8 : 0));
        for (var i = 0L; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var logicalLength = Math.Min(clusterSize, (long)inode.Size - i * clusterSize);
            var record = ReadInodeMetadata(nid, checked(indexBase + i * recordSize), recordSize, "short extent record");
            if (recordSize == 8)
            {
                physical = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(4));
            }
            var plenRaw = BinaryPrimitives.ReadUInt32LittleEndian(record);
            var physicalLength = plenRaw & ZErofsMapHeader.ExtentPlenMask;
            var format = plenRaw >> ZErofsMapHeader.ExtentPlenFmtBit;
            var runStart = i * clusterSize;
            if (runStart + logicalLength <= effectiveWindow.Start)
            {
                if (recordSize == 4)
                {
                    physical = checked(physical + physicalLength);
                }

                continue;
            }

            if (runStart >= effectiveWindow.End)
            {
                break;
            }

            effectiveWindow.GetRange(runStart, logicalLength, out var writeFrom, out var writeLength);
            if (writeLength <= 0)
            {
                if (recordSize == 4)
                {
                    physical = checked(physical + physicalLength);
                }

                continue;
            }

            if (i == count - 1 && (header.Advise & ZErofsMapHeader.AdviseFragmentPcluster) != 0)
            {
                var fragmentOffset = checked((ulong)physical << 32) | plenRaw;
                WritePackedData(fragmentOffset + (ulong)writeFrom, writeLength, output);
            }
            else if (physicalLength == 0)
            {
                WriteZeroes(output, writeLength);
            }
            else if (format == 0)
            {
                if (physicalLength < logicalLength)
                {
                    throw new FileSystemCorruptedException("EROFS plain short extent is smaller than its logical cluster.");
                }
                CheckRange(checked(physical + writeFrom), writeLength, "Plain short extent");
                CopyFromSource(output, checked(physical + writeFrom), writeLength);
            }
            else
            {
                if (format > (uint)ErofsAlgorithm.Zstd + 1)
                {
                    throw new FileSystemCorruptedException($"Unknown EROFS short extent compression algorithm {format - 1}.");
                }
                WriteCompressedExtent(nid, inode, record, recordSize, runStart, logicalLength,
                    (ErofsAlgorithm)(format - 1), output, scratch, cancellationToken, physical, writeFrom,
                    writeLength);
            }
            if (recordSize == 4)
            {
                physical = checked(physical + physicalLength);
            }
        }
    }

    private void WritePlainExtent(ulong nid, UnifiedInode inode, byte[] record, int recSize,
        long logicalOffset, long logicalLength, long physicalOffset, Stream output, CancellationToken cancellationToken,
        long windowFrom = 0, long windowLength = -1)
    {
        if (logicalLength <= 0)
        {
            return;
        }
        if (windowLength < 0)
        {
            windowLength = logicalLength;
        }
        if (windowFrom < 0 || windowLength <= 0 || windowFrom > logicalLength
            || windowLength > logicalLength - windowFrom)
        {
            throw new FileSystemCorruptedException("EROFS plain extent window is outside the logical range.");
        }
        if ((BinaryPrimitives.ReadUInt32LittleEndian(record) & ZErofsMapHeader.ExtentPlenMask) < logicalLength)
        {
            throw new FileSystemCorruptedException("EROFS plain extent is smaller than its logical range.");
        }
        long source;
        if (recSize > 8)
        {
            source = checked(physicalOffset + windowFrom);
        }
        else
        {
            source = checked(physicalOffset + logicalOffset + windowFrom);
        }

        CheckRange(source, windowLength, "Plain extent data");
        CopyFromSource(output, source, windowLength);
    }

    /// <summary>
    /// Decompresses and writes one extent's logical range. The encoded and decoded buffers are the
    /// caller-scoped <paramref name="scratch"/> pair, grown in place only when an extent needs more
    /// bytes than it currently holds, so a run of extents performs no further pool round-trips.
    /// <para>解压并写出单个 extent 的逻辑区间。压缩与解码缓冲为调用方作用域的
    /// <paramref name="scratch"/> 配对缓冲，仅在某个 extent 需要更多字节时就地扩容，因此连续
    /// extent 不再产生额外的池往返。</para>
    /// </summary>
    private void WriteCompressedExtent(ulong nid, UnifiedInode inode, byte[] record, int recSize,
        long logicalOffset, long logicalLength, ErofsAlgorithm algorithm, Stream output,
        ExtentWriteScratch scratch, CancellationToken cancellationToken,
        long? physicalOverride = null, long windowFrom = 0, long windowLength = -1)
    {
        if (logicalLength <= 0)
        {
            return;
        }
        if (windowLength < 0)
        {
            windowLength = logicalLength;
        }
        if (windowFrom < 0 || windowLength <= 0 || windowFrom > logicalLength
            || windowLength > logicalLength - windowFrom)
        {
            throw new FileSystemCorruptedException("EROFS compressed extent window is outside the logical range.");
        }
        if (logicalLength > ErofsCompression.MaxPclusterDecompressedSize)
        {
            throw new FileSystemCorruptedException("EROFS compressed extent exceeds the decompression budget.");
        }

        long pstart;
        if (physicalOverride is { } address)
        {
            pstart = address;
        }
        else if (recSize > 8)
        {
            var pstartLo = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(4));
            var pstartHi = recSize > 12 ? BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(8)) : 0;
            pstart = checked((long)(pstartLo | (ulong)pstartHi << 32));
        }
        else
        {
            throw new FileSystemCorruptedException($"EROFS compressed extent of nid {nid} lacks a physical start.");
        }

        var plenRaw = BinaryPrimitives.ReadUInt32LittleEndian(record);
        var compressedLength = (int)(plenRaw & ZErofsMapHeader.ExtentPlenMask);
        if (compressedLength <= 0 || compressedLength > ErofsCompression.MaxPclusterSize)
        {
            throw new FileSystemCorruptedException($"EROFS extent of nid {nid} has invalid encoded length {compressedLength}.");
        }

        CheckRange(pstart, compressedLength, "Compressed extent data");
        scratch.Ensure(compressedLength, (int)logicalLength);
        var compressed = scratch.Compressed;
        var decoded = scratch.Decoded;
        ReadPositioned(_stream, checked(_baseOffset + pstart), compressed, compressedLength);
        int decodedLength;
        try
        {
            var inputMargin = 0;
            if (algorithm == ErofsAlgorithm.Lz4
                && (_superblock.FeatureIncompat & ErofsSuperblock.IncompatLz4_0Padding) != 0)
            {
                while (inputMargin < compressedLength && compressed[inputMargin] == 0)
                {
                    inputMargin++;
                }
            }
            if (inputMargin == compressedLength)
            {
                throw new FileSystemCorruptedException("EROFS compressed extent is entirely zero-padded.");
            }
            decodedLength = DecompressPcluster(
                algorithm, compressed, inputMargin, compressedLength - inputMargin, decoded, (int)logicalLength);
        }
        catch (Exception ex) when (ex is InvalidDataException or FileSystemException or InvalidOperationException or ArgumentException)
        {
            throw new FileSystemCorruptedException(
                $"Extent of nid {nid} failed to decompress ({algorithm}): {ex.Message}");
        }

        if (decodedLength != (int)logicalLength)
        {
            throw new FileSystemCorruptedException(
                $"Extent of nid {nid} decoded to {decodedLength} bytes, expected {logicalLength}; " +
                $"algorithm={algorithm}, physical={pstart}, encoded={compressedLength}, prefix={Convert.ToHexString(compressed.AsSpan(0, Math.Min(16, compressedLength)))}.");
        }

        output.Write(decoded, checked((int)windowFrom), checked((int)windowLength));
    }

    /// <summary>
    /// Shared zero buffer for zero-fill writes, avoiding a fresh allocation per call.
    /// <para>补零写入的共享零缓冲，避免每次调用新分配。</para>
    /// </summary>
    private static readonly byte[] SharedZeroBuffer = new byte[1 << 16];

    private static void WriteZeroes(Stream output, long count)
    {
        var zeroes = SharedZeroBuffer;
        var written = 0L;
        while (written < count)
        {
            var n = (int)Math.Min(zeroes.Length, count - written);
            output.Write(zeroes, 0, n);
            written += n;
        }
    }

    /// <summary>
    /// Half-open logical byte window a mapped read requests from an inode. The whole-inode window is
    /// <see cref="Whole"/>; a packed-fragment read passes only the range it needs, so the extent walk can skip
    /// extents outside it and each writer emits just the intersecting bytes.
    /// <para>映射读取向 inode 请求的半开逻辑字节窗口。整 inode 窗口为 <see cref="Whole"/>；packed 片段读取只传入
    /// 所需区间，使 extent 遍历跳过窗口之外的 extent，各写出方法只输出相交的字节。</para>
    /// </summary>
    private readonly struct LogicalWindow
    {
        private LogicalWindow(long start, long end)
        {
            Start = start;
            End = end;
        }

        /// <summary>The window covering the whole inode. <para>覆盖整个 inode 的窗口。</para></summary>
        internal static LogicalWindow Whole => new(0, long.MaxValue);

        /// <summary>Inclusive logical start of the window. <para>窗口的逻辑起点（含）。</para></summary>
        internal long Start { get; }

        /// <summary>Exclusive logical end of the window. <para>窗口的逻辑终点（不含）。</para></summary>
        internal long End { get; }

        /// <summary>Creates a window from two logical offsets.
        /// <para>由两个逻辑偏移创建窗口。</para></summary>
        /// <param name="start">Inclusive logical start. <para>逻辑起点（含）。</para></param>
        /// <param name="end">Exclusive logical end. <para>逻辑终点（不含）。</para></param>
        /// <returns>The window; the caller guarantees <paramref name="start"/> is not above
        /// <paramref name="end"/>. <para>该窗口；调用方保证 <paramref name="start"/> 不大于
        /// <paramref name="end"/>。</para></returns>
        internal static LogicalWindow FromRange(long start, long end) => new(start, end);

        /// <summary>Reports the sub-range of one run that falls inside the window.
        /// <para>给出单个区段落在窗口内的子区间。</para></summary>
        /// <param name="runStart">Logical start of the run. <para>区段的逻辑起点。</para></param>
        /// <param name="runLength">Logical length of the run. <para>区段的逻辑长度。</para></param>
        /// <param name="from">Offset of the sub-range from <paramref name="runStart"/>; zero when empty.
        /// <para>子区间相对 <paramref name="runStart"/> 的偏移；为空时为零。</para></param>
        /// <param name="length">Length of the sub-range; zero when the run misses the window.
        /// <para>子区间的长度；区段与窗口不相交时为零。</para></param>
        internal void GetRange(long runStart, long runLength, out long from, out long length)
        {
            from = Math.Max(0, Start - runStart);
            var to = Math.Min(runLength, End - runStart);
            length = Math.Max(0, to - from);
        }
    }

    /// <summary>
    /// Copies fragment data at the specified logical offset out of the packed inode. A compressed packed inode
    /// is read through its own index on demand: only the pclusters covering the requested range are decoded,
    /// and their bytes stay in a bounded LRU cache, so a fragment read never stages the whole packed inode.
    /// <para>从 packed inode 的指定逻辑偏移复制片段数据。压缩的 packed inode 按其自身索引按需读取：只解码覆盖
    /// 请求区间的 pcluster，其字节留在有界 LRU 缓存中，因此片段读取绝不转存整个 packed inode。</para>
    /// </summary>
    /// <param name="fragmentOff">Logical offset inside the packed inode.
    /// <para>packed inode 内的逻辑偏移。</para></param>
    /// <param name="length">Bytes requested; zero writes nothing.
    /// <para>请求的字节数；为零时不写出。</para></param>
    /// <param name="output">Sink receiving the bytes. <para>接收字节的写出流。</para></param>
    /// <exception cref="FileSystemCorruptedException">The packed inode is absent, references itself, declares a
    /// size above the image size, or the requested range exceeds the image/output bound.
    /// <para>packed inode 缺失、自引用、声明的大小超出镜像大小，或请求区间超出镜像/输出上限。</para></exception>
    private void WritePackedData(ulong fragmentOff, long length, Stream output)
    {
        var packedNid = _superblock.PackedNid;
        if (packedNid == 0)
        {
            throw new FileSystemCorruptedException("Fragment inode without a packed inode (s_packed_nid == 0).");
        }

        UnifiedInode packed;
        lock (_streamLock)
        {
            _packedInode ??= ReadInode(packedNid);
            packed = _packedInode;
        }
        if (!packed.IsRegularFile && !packed.IsDirectory)
        {
            throw new FileSystemCorruptedException($"Packed inode nid {packedNid} is not a regular file.");
        }

        if (length == 0)
        {
            return;
        }

        if (length < 0)
        {
            throw new FileSystemCorruptedException($"Packed fragment length {length} is negative.");
        }

        var maximumBytes = MaximumOutputBytes;
        if (length > maximumBytes)
        {
            throw new FileSystemCorruptedException(
                $"Packed fragment read of {length} bytes exceeds the {maximumBytes}-byte output budget.");
        }

        var packedSize = (long)packed.Size;
        if (fragmentOff >= (ulong)packedSize || length > packedSize - (long)fragmentOff)
        {
            throw new FileSystemCorruptedException(
                $"Packed fragment offset {fragmentOff} and length {length} exceed the packed inode size {packedSize}.");
        }

        if (packed.IsCompressed)
        {
            CopyCompressedPackedRange(packedNid, packed, (long)fragmentOff, length, output);
            return;
        }

        var inlineAll = packed.DataLayout == UnifiedDataLayout.Inline &&
                        packed.AddressField() == uint.MaxValue;
        if (packed.DataLayout == UnifiedDataLayout.Inline && !inlineAll)
        {

            var flatLength = packedSize / _blockSize * _blockSize;
            WritePackedRange((long)fragmentOff, length, flatLength, packed, packedNid, inlineStartIsFlat: false, output);
            return;
        }

        if (packed.DataLayout == UnifiedDataLayout.Inline)
        {

            var start = checked(InodeOffset(packedNid) + packed.InlineDataOffset + (long)fragmentOff);
            if (IsMetaboxInode(packedNid))
            {
                output.Write(ReadMetaboxSpan(start, checked((int)length), "Packed fragment data"));
            }
            else
            {
                CheckRange(start, length, "Packed fragment data");
                CopyFromSource(output, start, length);
            }
            return;
        }

        if (packed.DataLayout != UnifiedDataLayout.Flat)
        {
            throw new FileSystemUnsupportedFeatureException($"Unsupported packed inode layout {packed.DataLayout} for nid {packedNid}.");
        }

        WritePackedRange((long)fragmentOff, length, packedSize, packed, packedNid, inlineStartIsFlat: true, output);
    }

    private long CompressedIndexEnd(ulong nid, UnifiedInode inode, long headerOffset, int count, int clusterBits)
    {
        if (inode.DataLayout == UnifiedDataLayout.Compressed)
            return checked(headerOffset + ZErofsMapHeader.Size + FullIndexStartPad + (long)count * 8);
        var header = ZErofsMapHeader.FromBytes(ReadInodeMetadata(nid, headerOffset, ZErofsMapHeader.Size, "inline index header"));
        var start = headerOffset + ZErofsMapHeader.Size;
        var initial = (int)(((32 - start % 32) / 4) & 7);
        var compact = header.HasCompacted2B && initial < count ? (count - initial) / 16 * 16 : 0;
        var last = count - 1;
        var width = 4;
        if (last >= initial)
        {
            start += initial * 4;
            last -= initial;
            if (last < compact) width = 2;
            else { start += compact * 2; last -= compact; }
        }
        var position = checked(start + (long)last * width);
        var packSize = width == 2 ? 32 : 8;
        return AlignUp(position + 1, packSize);
    }

    /// <summary>Bytes of decoded packed-inode pclusters the cache keeps alive at once.
    /// <para>缓存同时保留的已解码 packed inode pcluster 字节数。</para>
    /// </summary>
    private const long PackedRangeCacheBudgetBytes = 8L * 1024 * 1024;

    private bool _loadingPackedData;
    private PackedRangeCache? _packedRangeCache;

    private PackedRangeCache PackedRanges => _packedRangeCache ??= new PackedRangeCache(PackedRangeCacheBudgetBytes);

    /// <summary>
    /// Serves one compressed packed-inode range under the parser lock. The lock both keeps the read guard
    /// single-threaded and makes a packed inode that names itself fail as corruption instead of recursing.
    /// <para>在解析器锁下提供一次压缩 packed inode 区间读取。该锁既让读取守卫保持单线程，也使自引用的
    /// packed inode 作为损坏失败而不是递归。</para>
    /// </summary>
    private void CopyCompressedPackedRange(ulong nid, UnifiedInode inode, long offset, long length, Stream output)
    {
        lock (_streamLock)
        {
            if (_loadingPackedData)
            {
                throw new FileSystemCorruptedException("Recursive EROFS packed inode reference.");
            }

            _loadingPackedData = true;
            try
            {
                WriteCompressedPackedRange(nid, inode, offset, checked(offset + length), output);
            }
            finally
            {
                _loadingPackedData = false;
            }
        }
    }

    /// <summary>
    /// Decodes only the runs of the compressed packed inode that intersect [<paramref name="offset"/>,
    /// <paramref name="end"/>): the lcluster index maps the window to its covering pclusters, each covering
    /// run is decoded once and cached, and non-covering runs are skipped without touching their data. An
    /// extent-form packed inode takes the same windowed path through the extent walker.
    /// <para>只解码与 [<paramref name="offset"/>, <paramref name="end"/>) 相交的压缩 packed inode 区段：逻辑簇
    /// 索引把窗口映射到覆盖它的 pcluster，每个覆盖区段只解码一次并缓存，未覆盖的区段不触碰其数据直接跳过。
    /// extent 形态的 packed inode 经 extent 遍历走同一条窗口化路径。</para>
    /// </summary>
    private void WriteCompressedPackedRange(ulong nid, UnifiedInode inode, long offset, long end, Stream output)
    {
        var headerOffset = AlignUp(InodeOffset(nid) + inode.OnDiskSize + inode.XattrISize, 8);
        var headerBytes = ReadInodeMetadata(nid, headerOffset, ZErofsMapHeader.Size, "packed compressed map header");
        var header = ZErofsMapHeader.FromBytes(headerBytes);
        if (header.IsFragmentInode)
        {
            throw new FileSystemCorruptedException("Recursive EROFS packed inode reference.");
        }

        if (inode.DataLayout == UnifiedDataLayout.Compressed && header.HasExtents)
        {
            using var extentScratch = new ExtentWriteScratch();
            WriteExtentsData(nid, inode, header, headerBytes, headerOffset, output, extentScratch,
                _readCancellation, LogicalWindow.FromRange(offset, end));
            return;
        }

        var lclusterBits = _superblock.BlockSizeBits + (header.ClusterBits & ZErofsMapHeader.LclusterBitsMask);
        if (lclusterBits > CompactIndexMaxLclusterBits)
        {
            throw new FileSystemCorruptedException(
                $"Logical cluster size shift {lclusterBits} exceeds the limit of {CompactIndexMaxLclusterBits}.");
        }

        var lclusterSize = 1 << lclusterBits;
        var totalIdx = ErofsCompression.PclusterCount(inode.Size, lclusterSize);
        if (totalIdx == 0)
        {
            return;
        }

        var advise = header.Advise;
        var indexBase = headerOffset + ZErofsMapHeader.Size;
        var indexLength = inode.DataLayout == UnifiedDataLayout.CompactIndexes
            ? checked((long)totalIdx * CompactIndexSlotSize + CompactIndexWindowPadding)
            : checked((long)totalIdx * ZErofsLclusterIndex.Size + FullIndexStartPad);
        var cachedIndex = new IndexReadWindow(this, nid, indexBase,
            Math.Min(indexLength, InodeMetadataLength(nid) - indexBase));
        var context = new CompressedWriteContext
        {
            Nid = nid,
            Inode = inode,
            Header = header,
            HeaderOffset = headerOffset,
            Advise = advise,
            AlgorithmHead1 = header.Algorithm,
            AlgorithmHead2 = (ErofsAlgorithm)((header.AlgorithmType >> 4) & ZErofsMapHeader.AlgorithmTypeMask),
            LclusterBits = lclusterBits,
            LclusterSize = lclusterSize,
            TotalIdx = totalIdx,
            CachedIndex = cachedIndex,
            Interlaced = (advise & ZErofsMapHeader.AdviseInterlacedPcluster) != 0,
            BigPcluster1 = (advise & ZErofsMapHeader.AdviseBigPcluster1) != 0,
            BigPcluster2 = (advise & ZErofsMapHeader.AdviseBigPcluster2) != 0,
            TailPacking = (advise & ZErofsMapHeader.AdviseInlinePcluster) != 0,
            FragmentPcluster = (advise & ZErofsMapHeader.AdviseFragmentPcluster) != 0,
            Output = output,
            Observer = null,
            Compressed = [],
            Decoded = [],
        };

        var lcn = FindPackedRunStart(context, offset);
        while (lcn < context.TotalIdx)
        {
            _readCancellation.ThrowIfCancellationRequested();
            var lc = LoadLcluster(context, lcn);
            if (lc.Type == LclusterTypeNonHead)
            {
                lcn++;
                continue;
            }

            PrepareCompressedClusterRange(context, lc, lcn, out var nextLcn, out var iteration, out var kind);
            if (iteration is null || kind == CompressedRunKind.Skip)
            {
                if (nextLcn <= lcn)
                {
                    break;
                }

                lcn = nextLcn;
                continue;
            }

            var runStart = iteration.MappedLogicalOffset;
            if (runStart >= end)
            {
                break;
            }
            if (runStart + iteration.Llen <= offset)
            {
                lcn = nextLcn;
                continue;
            }

            switch (kind)
            {
                case CompressedRunKind.Fragment:
                    WritePackedFragmentRun(context, iteration, offset, end, output);
                    break;
                case CompressedRunKind.Plain:
                    WritePackedPlainRun(context, iteration, offset, end, output);
                    break;
                default:
                    WritePackedEncodedRun(context, iteration, offset, end, output);
                    break;
            }

            if (nextLcn <= lcn)
            {
                break;
            }

            lcn = nextLcn;
        }
    }

    /// <summary>
    /// Finds the head lcluster whose run covers <paramref name="offset"/>: it starts at the containing
    /// logical cluster, walks back over NONHEAD rows, and steps back once more when that head's
    /// <c>clusterofs</c> places its run after the requested offset.
    /// <para>找到其区段覆盖 <paramref name="offset"/> 的 head lcluster：从包含该偏移的逻辑簇开始，往回越过
    /// NONHEAD 行；若该 head 的 <c>clusterofs</c> 使其区段起点晚于请求偏移，则再回退一次。</para>
    /// </summary>
    private long FindPackedRunStart(CompressedWriteContext context, long offset)
    {
        var lcn = Math.Min(offset / context.LclusterSize, (long)context.TotalIdx - 1);
        while (lcn > 0 && LoadLcluster(context, lcn).Type == LclusterTypeNonHead)
        {
            lcn--;
        }

        while (lcn > 0)
        {
            var head = LoadLcluster(context, lcn);
            if (head.Type != LclusterTypeNonHead && lcn * context.LclusterSize + head.ClusterOfs <= offset)
            {
                break;
            }

            lcn--;
            while (lcn > 0 && LoadLcluster(context, lcn).Type == LclusterTypeNonHead)
            {
                lcn--;
            }
        }

        return lcn;
    }

    /// <summary>
    /// Writes the part of one fragment run that intersects the window. A packed inode never legitimately
    /// contains a further fragment reference, so the nested read reports recursion through
    /// <see cref="CopyCompressedPackedRange"/>.
    /// <para>写出单个 fragment 区段与窗口相交的部分。packed inode 合法时不会再包含更深的 fragment 引用，因此
    /// 嵌套读取会经 <see cref="CopyCompressedPackedRange"/> 报告递归。</para>
    /// </summary>
    private void WritePackedFragmentRun(CompressedWriteContext context, ClusterIteration iteration,
        long windowStart, long windowEnd, Stream output)
    {
        var fragmentOffset = FragmentOffsetOf(context, iteration);
        var from = Math.Max(0, windowStart - iteration.MappedLogicalOffset);
        var to = Math.Min(iteration.Llen, windowEnd - iteration.MappedLogicalOffset);
        if (to <= from)
        {
            return;
        }

        WritePackedData(fragmentOffset + (ulong)from, to - from, output);
    }

    /// <summary>Resolves the packed-inode offset a fragment run names, which for the compressed-full layout
    /// combines the 32-bit header offset with the head lcluster's physical block.
    /// <para>解析 fragment 区段指名的 packed inode 偏移；compressed-full 布局下它由 32 位头部偏移与 head
    /// lcluster 的物理块组合而成。</para>
    /// </summary>
    private static ulong FragmentOffsetOf(CompressedWriteContext context, ClusterIteration iteration)
    {
        var fragmentOffset = (ulong)context.Header.FragmentOff;
        if (context.Inode.DataLayout == UnifiedDataLayout.Compressed)
        {
            fragmentOffset |= (ulong)iteration.Head.Pblk << 32;
        }

        return fragmentOffset;
    }

    /// <summary>
    /// Copies the part of one plain packed-inode run that intersects the window, handling the byte-rotated
    /// interlaced layout and the tail-packed metadata location.
    /// <para>复制单个 plain packed inode 区段与窗口相交的部分，处理按字节旋转的 interlaced 布局与 tailpacked
    /// 的元数据存放位置。</para>
    /// </summary>
    private void WritePackedPlainRun(CompressedWriteContext context, ClusterIteration iteration,
        long windowStart, long windowEnd, Stream output)
    {
        var runStart = iteration.MappedLogicalOffset;
        var from = Math.Max(0, windowStart - runStart);
        var to = Math.Min(iteration.Llen, windowEnd - runStart);
        if (to <= from)
        {
            return;
        }

        var dataStart = iteration.IsTailPacked
            ? CompressedIndexEnd(context.Nid, context.Inode, context.HeaderOffset, context.TotalIdx,
                context.LclusterBits)
            : checked(iteration.Head.Pblk * _blockSize);
        if (!context.Interlaced)
        {
            if (iteration.IsTailPacked && IsMetaboxInode(context.Nid))
            {
                output.Write(ReadMetaboxSpan(dataStart + from, checked((int)(to - from)), "Plain lcluster data"));
                return;
            }

            CheckRange(dataStart + from, to - from, "Plain lcluster data");
            CopyFromSource(output, dataStart + from, to - from);
            return;
        }

        if (iteration.Llen > _blockSize || iteration.Llen < 0)
        {
            throw new FileSystemCorruptedException(
                $"Interlaced plain pcluster {iteration.Lcn} of nid {context.Nid} has length {iteration.Llen} > block size {_blockSize}.");
        }

        var rawBlock = iteration.IsTailPacked && IsMetaboxInode(context.Nid)
            ? ReadMetaboxBytes(dataStart, _blockSize, "Interlaced plain lcluster block")
            : ReadBytes(dataStart, _blockSize, "Interlaced plain lcluster block");
        var skip = (int)(runStart & (_blockSize - 1));
        var position = (int)((skip + from) & (_blockSize - 1));
        var remaining = (int)(to - from);
        var first = Math.Min(remaining, _blockSize - position);
        output.Write(rawBlock, position, first);
        if (remaining > first)
        {
            output.Write(rawBlock, 0, remaining - first);
        }
    }

    /// <summary>
    /// Decodes one encoded packed-inode run and writes its intersection with the window. The decoded frame is
    /// cached under its logical start when it fits the cache budget, so repeated fragment reads that share a
    /// pcluster decode it once; a frame larger than the budget is windowed straight from the pooled buffers.
    /// <para>解码单个编码 packed inode 区段并写出其与窗口的交集。解码帧能放入缓存预算时按其逻辑起点缓存，
    /// 使共享同一 pcluster 的重复片段读取只解码一次；大于预算的帧直接从池化缓冲按窗口写出。</para>
    /// </summary>
    private void WritePackedEncodedRun(CompressedWriteContext context, ClusterIteration iteration,
        long windowStart, long windowEnd, Stream output)
    {
        var runStart = iteration.MappedLogicalOffset;
        if (PackedRanges.TryGet(runStart, out var cached) && cached.Length == iteration.Llen)
        {
            WriteWindow(output, cached, runStart, windowStart, windowEnd);
            return;
        }

        byte[] compressed = [];
        byte[] decoded = [];
        try
        {
            var staged = PrepareEncodedPcluster(context, iteration, ref compressed, ref decoded);
            DecodeEncodedPcluster(context.Nid, staged);
            if (iteration.Llen <= PackedRangeCacheBudgetBytes)
            {
                var combined = new byte[checked((int)iteration.Llen)];
                var position = 0;
                if (staged.SemiRawLength > 0)
                {
                    staged.Compressed.AsSpan(0, staged.SemiRawLength).CopyTo(combined);
                    position = staged.SemiRawLength;
                }

                staged.Decoded.AsSpan(0, staged.DecodedTarget).CopyTo(combined.AsSpan(position));
                PackedRanges.Add(runStart, combined);
                WriteWindow(output, combined, runStart, windowStart, windowEnd);
                return;
            }

            WriteWindow(output, staged.Compressed.AsSpan(0, staged.SemiRawLength), runStart, windowStart, windowEnd);
            WriteWindow(output, staged.Decoded.AsSpan(0, staged.DecodedTarget),
                runStart + staged.SemiRawLength, windowStart, windowEnd);
        }
        finally
        {
            if (compressed.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(compressed);
            }

            if (decoded.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(decoded);
            }
        }
    }

    /// <summary>Writes the sub-span of <paramref name="data"/> that falls inside [<paramref name="windowStart"/>,
    /// <paramref name="windowEnd"/>), where <paramref name="data"/> starts at <paramref name="dataStart"/>.
    /// <para>写出 <paramref name="data"/> 中落在 [<paramref name="windowStart"/>, <paramref name="windowEnd"/>)
    /// 内的子跨度，其中 <paramref name="data"/> 自 <paramref name="dataStart"/> 开始。</para>
    /// </summary>
    private static void WriteWindow(Stream output, ReadOnlySpan<byte> data, long dataStart, long windowStart,
        long windowEnd)
    {
        var from = (int)Math.Max(0, windowStart - dataStart);
        var to = (int)Math.Min(data.Length, windowEnd - dataStart);
        if (to > from)
        {
            output.Write(data[from..to]);
        }
    }

    /// <summary>
    /// Bounded least-recently-used cache of decoded packed-inode pcluster bytes, keyed by the pcluster's
    /// logical start. Instances are parser-scoped and touched only under the parser stream lock.
    /// <para>按 pcluster 逻辑起点索引的已解码 packed inode 字节的有界 LRU 缓存。实例按解析器作用域持有，且只在
    /// 解析器流锁内访问。</para>
    /// </summary>
    private sealed class PackedRangeCache(long capacity)
    {
        private sealed class Entry(long start, byte[] data)
        {
            internal long Start { get; } = start;
            internal byte[] Data { get; } = data;
        }

        private readonly Dictionary<long, LinkedListNode<Entry>> _nodes = [];
        private readonly LinkedList<Entry> _order = [];
        private long _bytes;

        /// <summary>Returns the cached frame for a logical start and marks it most recently used.
        /// <para>返回该逻辑起点的缓存帧，并标记为最近使用。</para>
        /// </summary>
        internal bool TryGet(long start, out byte[] data)
        {
            if (_nodes.TryGetValue(start, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                data = node.Value.Data;
                return true;
            }

            data = [];
            return false;
        }

        /// <summary>Stores one decoded frame, evicting the least recently used frames until the byte budget
        /// fits; a frame larger than the whole budget is not cached.
        /// <para>保存一个解码帧，逐出最近最少使用的帧直到字节预算可容纳；大于整个预算的帧不缓存。</para>
        /// </summary>
        internal void Add(long start, byte[] data)
        {
            if (data.Length == 0 || data.Length > capacity)
            {
                return;
            }

            if (_nodes.Remove(start, out var existing))
            {
                _order.Remove(existing);
                _bytes -= existing.Value.Data.Length;
            }

            while (_bytes > capacity - data.Length && _order.Last is { } last)
            {
                _order.RemoveLast();
                _nodes.Remove(last.Value.Start);
                _bytes -= last.Value.Data.Length;
            }

            _nodes[start] = _order.AddFirst(new Entry(start, data));
            _bytes += data.Length;
        }

        /// <summary>Drops every cached frame. <para>丢弃全部缓存帧。</para></summary>
        internal void Clear()
        {
            _nodes.Clear();
            _order.Clear();
            _bytes = 0;
        }
    }

    /// <summary>
    /// Maps and copies the range [offset, offset + length) of the packed inode across its flat and inline sections.
    /// <para>按 flat/inline 分段映射 packed inode 的 [offset, offset + length) 区间并复制到输出。</para>
    /// </summary>
    private void WritePackedRange(
        long offset,
        long length,
        long flatLength,
        UnifiedInode packed,
        ulong packedNid,
        bool inlineStartIsFlat,
        Stream output)
    {
        var remaining = length;
        var pos = offset;
        if (pos < flatLength)
        {
            var flatBytes = Math.Min(remaining, flatLength - pos);
            var flatStart = checked(packed.AddressField() * _blockSize + pos);
            CheckRange(flatStart, flatBytes, "Packed fragment data");
            CopyFromSource(output, flatStart, flatBytes);
            remaining -= flatBytes;
            pos += flatBytes;
        }

        if (remaining <= 0)
        {
            return;
        }

        var inlineOffset = packed.InlineDataOffset;
        var inlineStart = InodeOffset(packedNid) + inlineOffset;
        if (!inlineStartIsFlat)
        {

            if (pos < flatLength)
            {
                throw new FileSystemCorruptedException("Packed fragment straddles the flat/inline boundary unexpectedly.");
            }

            inlineStart += pos - flatLength;
        }
        else
        {

            if (pos >= flatLength)
            {
                throw new FileSystemCorruptedException("Packed fragment extends beyond a pure-flat packed inode.");
            }
        }

        if (IsMetaboxInode(packedNid))
        {
            output.Write(ReadMetaboxSpan(inlineStart, checked((int)remaining), "Packed fragment data"));
        }
        else
        {
            CheckRange(inlineStart, remaining, "Packed fragment data");
            CopyFromSource(output, inlineStart, remaining);
        }
    }
}