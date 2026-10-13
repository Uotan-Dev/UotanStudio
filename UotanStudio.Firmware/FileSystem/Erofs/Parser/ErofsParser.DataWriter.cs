using System.Buffers;
using System.Buffers.Binary;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UnifiedDataLayout = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsDataLayout;
using UnifiedInode = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsInode;

namespace UotanStudio.Firmware.FileSystem.Erofs.Parser;

/// <summary>
/// EROFS parser part that copies inode data out of the image: it checks every offset and length against the image size, dispatches on the inline, flat, chunk-based and compressed layouts, and reports pcluster coverage deficits against the legacy decoding rules.
/// <para>EROFS 解析器的组成部分，负责把 inode 数据从镜像中取出：先用镜像大小校验每个偏移与长度，
/// 再按 inline、flat、chunk-based 与压缩布局分派，并按旧版解码规则报告 pcluster 的覆盖缺口。</para>
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
        if ((inode.ChunkFormat & ~0x7F) != 0 || (inode.Chunk48Bit && !inode.UsesChunkIndexes))
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
    /// Reads compressed file data through compact or full EROFS cluster indexes, including LZ4SC and
    /// ZLIB4C semi-compressed pclusters: a PLAIN head lcluster followed by NONHEAD lclusters decodes as
    /// one raw logical cluster (the head, stored verbatim at the pcluster start) plus the remaining
    /// logical clusters produced by the HEAD1 codec (LZ4 for LZ4SC, raw DEFLATE for ZLIB4C). Semi
    /// detection requires big-pcluster indexing (the CBLKCNT marker on the first NONHEAD lcluster), a
    /// zero head clusterofs, and a non-interlaced, non-tailpacked pcluster; PLAIN heads that encode a
    /// LZMA or ZSTD stream stay fully raw (SHIFTED).
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
            Compressed = ArrayPool<byte>.Shared.Rent(_blockSize * 2),
            Decoded = ArrayPool<byte>.Shared.Rent(Math.Max(lclusterSize, _blockSize)),
        };
        try
        {
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
            ArrayPool<byte>.Shared.Return(context.Compressed);
            ArrayPool<byte>.Shared.Return(context.Decoded);
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
    /// Writes one run of logical clusters headed by the lcluster at <paramref name="lcn"/>: it maps
    /// the run's logical offset window, classifies it (observer probe, fragment pcluster, plain,
    /// interlaced plain or encoded pcluster), dispatches to the matching writer, and reports the
    /// cursor for the next loop iteration.
    /// <para>写出以 <paramref name="lcn"/> 处 lcluster 为首的一段逻辑簇：映射该段的逻辑偏移窗口并判定
    /// 类型（observer 探测、fragment pcluster、plain、interlaced plain 或压缩 pcluster），分派到对应的
    /// 写出方法，并给出下一次循环迭代的游标。</para>
    /// </summary>
    /// <returns>True when the loop should continue from <paramref name="nextLcn"/>; false when the
    /// observer stopped the whole write and no further cluster should be processed.
    /// <para>返回 true 表示循环应从 <paramref name="nextLcn"/> 继续；返回 false 表示 observer 已终止
    /// 整个写出过程，不再处理后续逻辑簇。</para>
    /// </returns>
    private bool WriteCompressedClusterRange(CompressedWriteContext context, LclusterEntry lc, long lcn, out long nextLcn)
    {
        var inode = context.Inode;
        var mappedLogicalOffset = lcn * context.LclusterSize + lc.ClusterOfs;
        if (mappedLogicalOffset >= (long)inode.Size)
        {
            nextLcn = lcn + 1;
            return true;
        }

        nextLcn = FindNextHeadLcluster(context, lcn, out var nextMappedLogicalOffset);
        var kernelExpectedOutputSize = checked((int)(Math.Max((long)nextLcn << context.LclusterBits,
            Math.Min(nextMappedLogicalOffset, (long)inode.Size)) - mappedLogicalOffset));

        var llen = Math.Min(nextMappedLogicalOffset, (long)inode.Size) - mappedLogicalOffset;
        if (llen <= 0)
        {
            return true;
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
            return true;
        }
        if (context.FragmentPcluster && isLastExtent)
        {
            var fragmentOffset = (ulong)context.Header.FragmentOff;
            if (inode.DataLayout == UnifiedDataLayout.Compressed)
            {
                fragmentOffset |= (ulong)lc.Pblk << 32;
            }

            WritePackedData(fragmentOffset, llen, context.Output);
            return true;
        }

        var iteration = new ClusterIteration
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

        if (lc.Type == LclusterTypePlain && !isSemiPcluster)
        {
            WritePlainLcluster(context, iteration);
            return true;
        }

        return WriteEncodedPcluster(context, iteration);
    }

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
    /// Decompresses and writes one encoded pcluster run: it reads the CBLKCNT big-pcluster marker,
    /// validates the encoded length, decompresses with the head lcluster's algorithm, and either
    /// feeds the observer or writes the semi raw head cluster plus the decoded tail to the output.
    /// <para>解压并写出一段编码 pcluster：读取 CBLKCNT big-pcluster 标记，校验编码长度，按 head
    /// lcluster 的算法解压，并将结果交给 observer，或把半压缩的原始头簇与解码尾部写入输出。</para>
    /// </summary>
    /// <returns>True when the caller should continue with the next run; false when the observer
    /// stopped the whole write.
    /// <para>返回 true 表示调用方应继续处理下一段；返回 false 表示 observer 已终止整个写出。</para>
    /// </returns>
    private bool WriteEncodedPcluster(CompressedWriteContext context, ClusterIteration iteration)
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
        if (compressedByteLen > context.Compressed.Length)
        {
            ArrayPool<byte>.Shared.Return(context.Compressed);
            context.Compressed = ArrayPool<byte>.Shared.Rent(compressedByteLen);
        }

        if (llen > context.Decoded.Length)
        {
            ArrayPool<byte>.Shared.Return(context.Decoded);
            context.Decoded = ArrayPool<byte>.Shared.Rent((int)llen);
        }

        if (iteration.IsTailPacked && IsMetaboxInode(context.Nid))
        {
            ReadMetaboxSpan(pblkStart, compressedByteLen, "Compressed pcluster data")
                .CopyTo(context.Compressed.AsSpan(0, compressedByteLen));
        }
        else
        {
            CheckRange(pblkStart, compressedByteLen, "Compressed pcluster data");
            ReadPositioned(_stream, checked(_baseOffset + pblkStart), context.Compressed, compressedByteLen);
        }

        var inputMargin = iteration.SemiRawLength;
        while (!iteration.IsTailPacked && inputMargin < compressedByteLen && context.Compressed[inputMargin] == 0)
        {
            inputMargin++;
        }

        if (inputMargin >= compressedByteLen)
        {
            throw new FileSystemCorruptedException(
                $"Pcluster {lcn} of nid {context.Nid} is entirely zero-padded (no compressed data).");
        }

        var algorithm = headType == LclusterTypeHead2 ? context.AlgorithmHead2 : context.AlgorithmHead1;
        var decodedTarget = (int)llen - iteration.SemiRawLength;
        int decodedLen;
        try
        {
            decodedLen = ErofsCompression.AlgorithmDecompress(
                algorithm, context.Compressed, inputMargin, compressedByteLen - inputMargin, context.Decoded, 0, decodedTarget);
        }
        catch (Exception ex) when (ex is InvalidDataException or FileSystemException or InvalidOperationException or ArgumentException)
        {
            throw new FileSystemCorruptedException(
                $"Pcluster {lcn} of nid {context.Nid} failed to decompress ({algorithm}): {ex.Message}");
        }

        if (decodedLen != decodedTarget)
        {
            throw new FileSystemCorruptedException(
                $"Pcluster {lcn} of nid {context.Nid} decoded to {decodedLen} bytes, expected {decodedTarget}.");
        }

        if (context.Observer is not null)
        {
            return context.Observer((ErofsCompressionAlgorithm)algorithm,
                context.Compressed.AsSpan(inputMargin, compressedByteLen - inputMargin), context.Decoded.AsSpan(0, decodedTarget), lc.ClusterOfs, iteration.KernelExpectedOutputSize);
        }

        if (iteration.SemiRawLength > 0)
        {
            context.Output.Write(context.Compressed, 0, iteration.SemiRawLength);
        }
        context.Output.Write(context.Decoded, 0, decodedTarget);
        return true;
    }

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

        /// <summary>Pooled encoded-input buffer, grown in place by the encoded pcluster writer.
        /// <para>池化的压缩输入缓冲，由压缩 pcluster 写出方法按需就地扩容。</para></summary>
        public required byte[] Compressed { get; set; }

        /// <summary>Pooled decoded-output buffer, grown in place by the encoded pcluster writer.
        /// <para>池化的解码输出缓冲，由压缩 pcluster 写出方法按需就地扩容。</para></summary>
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
    /// <para><see cref="MaxPclusterKernelCoverageDeficit"/> 的诊断伴侣，列出所有旧版期望输出大于实际
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
        long headerOffset, Stream output, ExtentWriteScratch scratch, CancellationToken cancellationToken)
    {
        var extentCount = (ulong)(BinaryPrimitives.ReadUInt32LittleEndian(headerBytes)
            | ((ulong)header.ExtentsHi << 32));
        var recSize = ZErofsMapHeader.ExtentRecordSize(header.Advise);
        var extentsBase = ZErofsMapHeader.ExtentAreaBase(headerOffset, recSize);
        if (recSize is 4 or 8)
        {
            WriteShortExtentsData(nid, inode, header, extentsBase, recSize, output, scratch, cancellationToken);
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
                WritePackedData(((ulong)physicalOffset << 32) | plenRaw, logicalLength, output);
            }
            else if (physicalLength == 0)
            {
                WriteZeroes(output, logicalLength);
            }
            else if (fmt == 0)
            {
                WritePlainExtent(nid, inode, record, recSize, logicalStart, logicalLength, physicalOffset, output, cancellationToken);
            }
            else
            {
                WriteCompressedExtent(nid, inode, record, recSize, logicalStart, logicalLength,
                    (ErofsAlgorithm)(fmt - 1), output, scratch, cancellationToken, physicalOffset);
            }

            logicalOffset = logicalEnd;
        }

        if (logicalOffset != (long)inode.Size)
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
        long indexStart, int recordSize, Stream output, ExtentWriteScratch scratch, CancellationToken cancellationToken)
    {
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
            if (i == count - 1 && (header.Advise & ZErofsMapHeader.AdviseFragmentPcluster) != 0)
            {
                var fragmentOffset = checked((ulong)physical << 32) | plenRaw;
                WritePackedData(fragmentOffset, logicalLength, output);
            }
            else if (physicalLength == 0)
            {
                WriteZeroes(output, logicalLength);
            }
            else if (format == 0)
            {
                if (physicalLength < logicalLength)
                {
                    throw new FileSystemCorruptedException("EROFS plain short extent is smaller than its logical cluster.");
                }
                CheckRange(physical, logicalLength, "Plain short extent");
                CopyFromSource(output, physical, logicalLength);
            }
            else
            {
                if (format > (uint)ErofsAlgorithm.Zstd + 1)
                {
                    throw new FileSystemCorruptedException($"Unknown EROFS short extent compression algorithm {format - 1}.");
                }
                WriteCompressedExtent(nid, inode, record, recordSize, i * clusterSize, logicalLength,
                    (ErofsAlgorithm)(format - 1), output, scratch, cancellationToken, physical);
            }
            if (recordSize == 4)
            {
                physical = checked(physical + physicalLength);
            }
        }
    }

    private void WritePlainExtent(ulong nid, UnifiedInode inode, byte[] record, int recSize,
        long logicalOffset, long logicalLength, long physicalOffset, Stream output, CancellationToken cancellationToken)
    {
        if (logicalLength <= 0)
        {
            return;
        }
        if ((BinaryPrimitives.ReadUInt32LittleEndian(record) & ZErofsMapHeader.ExtentPlenMask) < logicalLength)
        {
            throw new FileSystemCorruptedException("EROFS plain extent is smaller than its logical range.");
        }
        long source;
        if (recSize > 8)
        {
            source = physicalOffset;
        }
        else
        {
            source = checked(physicalOffset + logicalOffset);
        }

        CheckRange(source, logicalLength, "Plain extent data");
        CopyFromSource(output, source, logicalLength);
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
        long? physicalOverride = null)
    {
        if (logicalLength <= 0)
        {
            return;
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
            decodedLength = ErofsCompression.AlgorithmDecompress(
                algorithm, compressed, inputMargin, compressedLength - inputMargin, decoded, 0, (int)logicalLength);
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

        output.Write(decoded, 0, (int)logicalLength);
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
    /// Reads fragment data at the specified logical offset in the packed inode.
    /// <para>从 packed inode 的指定逻辑偏移读取片段数据。</para>
    /// </summary>
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

    private FileStream? _packedData;
    private bool _loadingPackedData;

    private void CopyCompressedPackedRange(ulong nid, UnifiedInode inode, long offset, long length, Stream output)
    {
        lock (_streamLock)
        {
            if (_loadingPackedData) throw new FileSystemCorruptedException("Recursive EROFS packed inode reference.");
            if (_packedData is null)
            {
                if (inode.Size > 64UL << 30) throw new FileSystemCorruptedException("EROFS packed inode exceeds the 64 GiB staging limit.");
                var temporary = new FileStream(Path.Combine(Path.GetTempPath(), "uotan-erofs-packed-" + Guid.NewGuid().ToString("N")),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.DeleteOnClose);
                _loadingPackedData = true;
                try
                {
                    WriteCompressedData(nid, inode, temporary);
                    if (temporary.Length != (long)inode.Size) throw new FileSystemCorruptedException("EROFS packed inode length mismatch.");
                    _packedData = temporary;
                }
                catch { temporary.Dispose(); throw; }
                finally { _loadingPackedData = false; }
            }
            _packedData.Position = offset;
            var buffer = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                while (length > 0)
                {
                    var take = (int)Math.Min(length, buffer.Length);
                    _packedData.ReadExactly(buffer, 0, take);
                    output.Write(buffer, 0, take);
                    length -= take;
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
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