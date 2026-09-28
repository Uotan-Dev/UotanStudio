using System.Buffers;
using System.Buffers.Binary;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Models;

namespace UotanStudio.Firmware.FileSystem.Erofs.Parser;

/// <summary>
/// EROFS parser part that copies inode data out of the image: it checks every offset and length against the image size, dispatches on the inline, flat, chunk-based and compressed layouts, and reports kernel pcluster coverage deficits.
/// <para>EROFS 解析器的组成部分，负责把 inode 数据从镜像中取出：先用镜像大小校验每个偏移与长度，
/// 再按 inline、flat、chunk-based 与压缩布局分派，并报告内核对 pcluster 的覆盖缺口。</para>
/// </summary>
public sealed partial class ErofsParser
{
    private void CheckRange(long offset, long length, string what)
    {
        if (offset < 0 || length < 0 || offset > _imageSize || length > _imageSize - offset)
        {
            throw new InvalidDataException($"{what} exceeds the image bounds.");
        }
    }

    private void WriteFileData(ulong nid, ErofsInode inode, Stream output)
    {
        _readCancellation.ThrowIfCancellationRequested();
        if (_readCancellation.CanBeCanceled)
        {
            output = new CancellationWriteStream(output, _readCancellation);
        }
        if (IsSpecialFile(inode))
        {

            throw new InvalidDataException(
                $"Inode nid {nid} is a special file (mode 0x{inode.Mode:X4}) with no data content.");
        }

        switch (inode.DataLayout)
        {
            case ErofsInode.DataLayoutInline:

                WriteInlineData(nid, inode, output);
                break;
            case ErofsInode.DataLayoutFlat:
                WriteFlatData(inode, output);
                break;
            case ErofsInode.DataLayoutChunkBased:
                WriteChunkData(nid, inode, output);
                break;
            case ErofsInode.DataLayoutCompressed:
            case ErofsInode.DataLayoutCompactIndexes:
                WriteCompressedData(nid, inode, output);
                break;
            default:

                throw new InvalidDataException($"Unsupported EROFS data layout {inode.DataLayout} for inode nid {nid}.");
        }
    }

    private void WriteInlineData(ulong nid, ErofsInode inode, Stream output)
    {
        var length = checked((long)inode.Size);
        if (length == 0)
        {
            return;
        }

        var flatLength = (long)(inode.Size / (ulong)_blockSize * (ulong)_blockSize);
        if (flatLength > 0)
        {
            var flatStart = checked(inode.RawBlockAddress * _blockSize);
            CheckRange(flatStart, flatLength, "Tailpacked flat file data");
            CopyFromSource(output, flatStart, flatLength);
        }

        var tailLength = length - flatLength;
        if (tailLength > 0)
        {
            if (_hasMetabox && (nid & NidMetaboxFlag) != 0)
            {
                var tailStart = checked(MetaboxOffset(nid) + inode.InlineDataOffset);
                output.Write(ReadMetaboxBytes(tailStart, checked((int)tailLength), "Tailpacked inline data"));
            }
            else
            {
                var tailStart = checked(_superblock.GetInodeOffset(nid) + inode.InlineDataOffset);
                CheckRange(tailStart, tailLength, "Tailpacked inline data");
                CopyFromSource(output, tailStart, tailLength);
            }
        }
    }

    private void WriteFlatData(ErofsInode inode, Stream output)
    {
        var length = (long)inode.Size;
        if (length == 0)
        {
            return;
        }

        var start = checked(inode.RawBlockAddress * _blockSize);
        CheckRange(start, length, "File data");
        CopyFromSource(output, start, length);
    }

    /// <summary>
    /// Reads a chunk-based file (EROFS_INODE_CHUNK_BASED): assembles data chunk by chunk from the chunk index table.
    /// <para>读取 chunk-based 文件（EROFS_INODE_CHUNK_BASED）：按 chunk 索引表逐块拼接数据。</para>
    /// </summary>
    private void WriteChunkData(ulong nid, ErofsInode inode, Stream output)
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
            throw new InvalidDataException($"Invalid chunk size (blkbits {chunkBlkBits}) for nid {nid}.");
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
            throw new InvalidDataException(
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

                var zeroes = new byte[(int)Math.Min(chunkLen, 1 << 16)];
                var zeroed = 0L;
                while (zeroed < chunkLen)
                {
                    var n = (int)Math.Min(zeroes.Length, chunkLen - zeroed);
                    output.Write(zeroes, 0, n);
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
    /// Reads compressed file data through compact or full EROFS cluster indexes, including LZ4SC and
    /// ZLIB4C semi-compressed pclusters: a PLAIN head lcluster followed by NONHEAD lclusters decodes as
    /// one raw logical cluster (the head, stored verbatim at the pcluster start) plus the remaining
    /// logical clusters produced by the HEAD1 codec (LZ4 for LZ4SC, raw DEFLATE for ZLIB4C). Semi
    /// detection requires big-pcluster indexing (the CBLKCNT marker on the first NONHEAD lcluster), a
    /// zero head clusterofs, and a non-interlaced, non-tailpacked pcluster; PLAIN heads that encode a
    /// LZMA or ZSTD stream stay fully raw (SHIFTED) per the kernel semantics.
    /// <para>通过紧凑或完整的 EROFS 簇索引读取压缩文件数据，含 LZ4SC 与 ZLIB4C 半压缩 pcluster：
    /// 当 PLAIN head lcluster 后跟随 NONHEAD lcluster 时，首逻辑簇按 pcluster 起点的原始字节直接输出，
    /// 其余逻辑簇由 HEAD1 编解码器解压产出（LZ4SC 为 LZ4，ZLIB4C 为原始 DEFLATE）。半压缩识别要求
    /// big-pcluster 索引（首个 NONHEAD 携带 CBLKCNT 标记）、head 的 clusterofs 为 0，且非 interlaced、
    /// 非尾部打包；编码 LZMA 或 ZSTD 流的 PLAIN head 按内核语义仍整体视为明文（SHIFTED）。</para>
    /// </summary>
    private void WriteCompressedData(ulong nid, ErofsInode inode, Stream output,
        CompressionObserver? observer = null, CancellationToken cancellationToken = default)
    {
        var inodeLoc = InodeOffset(nid);

        var headerOffset = AlignUp(inodeLoc + inode.OnDiskSize + inode.XattrTotalSize, 8);
        var headerBytes = ReadInodeMetadata(nid, headerOffset, ZErofsMapHeader.Size, "compressed map header");
        var header = ZErofsMapHeader.FromBytes(headerBytes);

        if (header.IsFragmentInode)
        {
            if (observer is not null) return;
            var fragmentOff = BinaryPrimitives.ReadUInt64LittleEndian(headerBytes) & ~(1UL << 63);
            WritePackedData(fragmentOff, checked((long)inode.Size), output);
            return;
        }

        var advise = header.Advise;
        var algorithmHead1 = header.Algorithm;

        if (inode.DataLayout == ErofsInode.DataLayoutCompressed && header.HasExtents)
        {
            if (observer is not null) return;
            WriteExtentsData(nid, inode, header, headerBytes, headerOffset, output, cancellationToken);
            return;
        }

        var algorithmHead2 = (ErofsAlgorithm)((header.AlgorithmType >> 4) & ZErofsMapHeader.AlgorithmTypeMask);
        var lclusterBits = _superblock.BlockSizeBits + (header.ClusterBits & ZErofsMapHeader.LclusterBitsMask);
        if (lclusterBits > 14)
        {
            throw new InvalidDataException($"Logical cluster size shift {lclusterBits} exceeds the limit of 14.");
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
        var indexLength = inode.DataLayout == ErofsInode.DataLayoutCompactIndexes
            ? checked((long)totalIdx * 4 + 64)
            : checked((long)totalIdx * ZErofsLclusterIndex.Size + FullIndexStartPad);
        var cachedIndex = new IndexReadWindow(this, nid, indexBase,
            Math.Min(indexLength, InodeMetadataLength(nid) - indexBase));

        var compressed = ArrayPool<byte>.Shared.Rent(_blockSize * 2);
        var decoded = ArrayPool<byte>.Shared.Rent(Math.Max(lclusterSize, _blockSize));
        try
        {
            long lcn = 0;
            while (lcn < totalIdx)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lc = LoadLcluster(nid, inode, headerOffset, advise, lclusterBits, totalIdx, lcn, out var ebase, out var compacted4bInitial, out var compacted2b, cachedIndex);
                if (lc.Type == LclusterTypeNonHead)
                {

                    lcn++;
                    continue;
                }

                var mLa = lcn * lclusterSize + lc.ClusterOfs;
                if (mLa >= (long)inode.Size)
                {
                    lcn++;
                    continue;
                }

                var nextLcn = lcn + 1;
                while (nextLcn < totalIdx)
                {
                    var candidate = LoadLcluster(nid, inode, headerOffset, advise, lclusterBits,
                        totalIdx, nextLcn, out _, out _, out _, cachedIndex);
                    if (candidate.Type != LclusterTypeNonHead) break;
                    nextLcn++;
                }
                long nextMLa;
                if (nextLcn < 0)
                {
                    nextMLa = (long)inode.Size;
                }
                else if (nextLcn >= totalIdx)
                {
                    nextMLa = (long)inode.Size;
                    nextLcn = totalIdx;
                }
                else
                {
                    var t2 = LoadLcluster(nid, inode, headerOffset, advise, lclusterBits, totalIdx, nextLcn, out _, out _, out _, cachedIndex);
                    nextMLa = nextLcn * lclusterSize + t2.ClusterOfs;
                }

                var kernelExpectedOutputSize = checked((int)(Math.Max((long)nextLcn << lclusterBits,
                    Math.Min(nextMLa, (long)inode.Size)) - mLa));

                var llen = Math.Min(nextMLa, (long)inode.Size) - mLa;
                if (llen <= 0)
                {
                    lcn = nextLcn;
                    continue;
                }

                if (llen > ErofsCompression.MaxPclusterDecompressedSize)
                {
                    throw new InvalidDataException(
                        $"Pcluster decompressed size {llen} exceeds the limit of {ErofsCompression.MaxPclusterDecompressedSize} bytes.");
                }

                var isLastExtent = nextMLa >= (long)inode.Size;
                var isTailPacked = tailPacking && isLastExtent;

                var semiRawLength = 0;
                var isSemiPcluster = lc.Type == LclusterTypePlain && !interlaced && !isTailPacked
                    && lc.ClusterOfs == 0 && nextLcn > lcn + 1 && llen > lclusterSize
                    && algorithmHead1 is ErofsAlgorithm.Lz4 or ErofsAlgorithm.Deflate;
                if (isSemiPcluster)
                {
                    semiRawLength = lclusterSize;
                }
                if (observer is not null && (llen > 1 << 20 || lc.Type == LclusterTypePlain || fragmentPcluster && isLastExtent))
                {
                    lcn = nextLcn;
                    continue;
                }
                if (fragmentPcluster && isLastExtent)
                {
                    var fragmentOffset = (ulong)header.FragmentOff;
                    if (inode.DataLayout == ErofsInode.DataLayoutCompressed) fragmentOffset |= (ulong)lc.Pblk << 32;
                    WritePackedData(fragmentOffset, llen, output);
                    lcn = nextLcn;
                    continue;
                }

                if (lc.Type == LclusterTypePlain && !isSemiPcluster)
                {

                    var dataStart = isTailPacked ? CompressedIndexEnd(nid, inode, headerOffset, totalIdx, lclusterBits) : checked(lc.Pblk * _blockSize);
                    if (!interlaced)
                    {
                        if (isTailPacked && IsMetaboxInode(nid))
                        {
                            output.Write(ReadMetaboxBytes(dataStart, checked((int)llen), "Plain lcluster data"));
                        }
                        else
                        {
                            CheckRange(dataStart, llen, "Plain lcluster data");
                            CopyFromSource(output, dataStart, llen);
                        }
                        lcn = nextLcn;
                        continue;
                    }

                    if (llen > _blockSize || llen < 0)
                    {
                        throw new InvalidDataException(
                            $"Interlaced plain pcluster {lcn} of nid {nid} has length {llen} > block size {_blockSize}.");
                    }

                    var skip = (int)(mLa & (_blockSize - 1));
                    var rawBlock = isTailPacked && IsMetaboxInode(nid)
                        ? ReadMetaboxBytes(dataStart, _blockSize, "Interlaced plain lcluster block")
                        : ReadBytes(dataStart, _blockSize, "Interlaced plain lcluster block");
                    var rightpart = Math.Min(_blockSize - skip, (int)llen);
                    output.Write(rawBlock, skip, rightpart);
                    if (llen > rightpart)
                    {
                        output.Write(rawBlock, 0, (int)llen - rightpart);
                    }

                    lcn = nextLcn;
                    continue;
                }

                var compressedBlocks = 1;
                var headType = lc.Type;
                var head1NotBig = headType == LclusterTypeHead1 && !bigPcluster1;
                var plainOrHead2NotBig = (headType == LclusterTypePlain || headType == LclusterTypeHead2) && !bigPcluster2;
                var atEof = (lcn << lclusterBits) >= (long)inode.Size;
                if (!isTailPacked && !head1NotBig && !plainOrHead2NotBig && !atEof && lcn + 1 < totalIdx)
                {

                    var next = LoadLcluster(nid, inode, headerOffset, advise, lclusterBits, totalIdx, lcn + 1, out _, out _, out _, cachedIndex);
                    if (next.Type != LclusterTypeNonHead)
                    {

                        compressedBlocks = 1;
                    }
                    else
                    {

                        if (next.Delta0 != 1 || next.CompressedBlocks == 0)
                        {
                            throw new InvalidDataException(
                                $"Bogus CBLKCNT @ lcn {lcn + 1} of nid {nid}.");
                        }

                        compressedBlocks = next.CompressedBlocks;
                    }
                }

                var compressedByteLen = isTailPacked ? header.IdataSize : checked(compressedBlocks * _blockSize);
                if (compressedByteLen <= 0 || !isTailPacked && lc.Pblk < 0 || compressedByteLen > ErofsCompression.MaxPclusterSize)
                {
                    throw new InvalidDataException($"Invalid compressed pcluster for nid {nid}.");
                }

                if (isSemiPcluster && compressedByteLen <= semiRawLength)
                {
                    throw new InvalidDataException(
                        $"Semi-compressed pcluster {lcn} of nid {nid} has no encoded tail after its raw head cluster ({compressedByteLen} disk bytes, {semiRawLength} raw bytes).");
                }

                var pblkStart = isTailPacked ? CompressedIndexEnd(nid, inode, headerOffset, totalIdx, lclusterBits) : checked(lc.Pblk * _blockSize);
                if (compressedByteLen > compressed.Length)
                {
                    ArrayPool<byte>.Shared.Return(compressed);
                    compressed = ArrayPool<byte>.Shared.Rent(compressedByteLen);
                }

                if (llen > decoded.Length)
                {
                    ArrayPool<byte>.Shared.Return(decoded);
                    decoded = ArrayPool<byte>.Shared.Rent((int)llen);
                }

                if (isTailPacked && IsMetaboxInode(nid))
                {
                    ReadMetaboxBytes(pblkStart, compressedByteLen, "Compressed pcluster data")
                        .CopyTo(compressed, 0);
                }
                else
                {
                    CheckRange(pblkStart, compressedByteLen, "Compressed pcluster data");
                    lock (_streamLock)
                    {
                        _stream.Position = checked(_baseOffset + pblkStart);
                        _stream.ReadExactly(compressed, 0, compressedByteLen);
                    }
                }

                var inputMargin = semiRawLength;
                while (!isTailPacked && inputMargin < compressedByteLen && compressed[inputMargin] == 0)
                {
                    inputMargin++;
                }

                if (inputMargin >= compressedByteLen)
                {
                    throw new InvalidDataException(
                        $"Pcluster {lcn} of nid {nid} is entirely zero-padded (no compressed data).");
                }

                var algorithm = headType == LclusterTypeHead2 ? algorithmHead2 : algorithmHead1;
                var decodedTarget = (int)llen - semiRawLength;
                int decodedLen;
                try
                {
                    decodedLen = ErofsCompression.AlgorithmDecompress(
                        algorithm, compressed, inputMargin, compressedByteLen - inputMargin, decoded, 0, decodedTarget);
                }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException)
                {
                    throw new InvalidDataException(
                        $"Pcluster {lcn} of nid {nid} failed to decompress ({algorithm}): {ex.Message}");
                }

                if (decodedLen != decodedTarget)
                {
                    throw new InvalidDataException(
                        $"Pcluster {lcn} decoded to {decodedLen} bytes, expected {decodedTarget}.");
                }

                if (observer is not null)
                {
                    if (!observer((ErofsCompressionAlgorithm)algorithm,
                        compressed.AsSpan(inputMargin, compressedByteLen - inputMargin), decoded.AsSpan(0, decodedTarget), lc.ClusterOfs, kernelExpectedOutputSize)) return;
                }
                else
                {
                    if (semiRawLength > 0)
                    {
                        output.Write(compressed, 0, semiRawLength);
                    }
                    output.Write(decoded, 0, decodedTarget);
                }
                lcn = nextLcn;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compressed);
            ArrayPool<byte>.Shared.Return(decoded);
        }
    }

    /// <summary>
    /// Returns the largest logical <c>clusterofs</c> among all compressed head lclusters, i.e. the
    /// greatest nonzero SourceOffset modulo the logical cluster size. Standards-conforming images
    /// place every head lcluster at its cluster start (clusterofs == 0); only byte-granular packing
    /// (the dense planner) produces a non-zero clusterofs, which legacy Linux 5.4 / 5.10 decoders
    /// cannot address.
    /// <para>返回所有压缩 head lcluster 中最大的逻辑 clusterofs，即 SourceOffset 对逻辑簇大小取模的
    /// 最大值。符合规范的镜像把每个 head lcluster 放在其逻辑簇起点（clusterofs == 0）；只有按
    /// 字节粒度打包（dense 规划器）才会产生非零 clusterofs，这是 legacy Linux 5.4 / 5.10 解码器
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
    /// CBLKCNT marker so the kernel spans several physical blocks in one extent.
    /// <para>返回所有 pcluster 中最大的压缩流字节数。大于块大小即证明镜像存储了真正的多块 big
    /// pcluster：其索引 head 携带 CBLKCNT 标记，使内核单个 extent 跨越多个物理块。</para>
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
    /// pre-big-pcluster kernel demands (covered whole logical clusters, unclamped by i_size) and
    /// the bytes the stored stream actually decodes to. Zero is required for images that must
    /// mount on Linux 5.4 / 5.10; a positive value reproduces the device-side
    /// "failed to decompress %d in[...] out[...]" corruption. Modern (big-pcluster) kernels clamp
    /// the final extent to i_size, so images using that layout can legitimately report a deficit.
    /// <para>返回所有压缩 pcluster 中，“pre-big-pcluster 内核要求的输出（覆盖的整逻辑簇数，不按
    /// i_size 裁剪）”与“实际解压字节数”之间的最大缺口。需要在 Linux 5.4 / 5.10 上挂载的镜像必须
    /// 为 0；大于 0 即复现设备端 "failed to decompress %d in[...] out[...]" 故障。现代
    /// （big-pcluster）内核会把最后一个 extent 裁剪到 i_size，因此采用该布局的镜像出现缺口属正常。</para>
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
    /// compressed extent whose kernel-expected output exceeds its decoded bytes.
    /// <para><see cref="MaxPclusterKernelCoverageDeficit"/> 的诊断伴侣，列出所有内核期望输出大于实际
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
    /// kernel decodes them, to compare a rebuild's index against the verified source image.
    /// <para>诊断伴侣：按内核的解码方式列出指定文件的每个逻辑簇索引项，用于把重建镜像的索引与已校验
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

    private List<(long Lcn, byte Type, int ClusterOfs, long Pblk, uint Delta0, uint Delta1)> IndexRows(ulong nid, ErofsInode inode)
    {
        var headerOffset = AlignUp(InodeOffset(nid) + inode.OnDiskSize + inode.XattrTotalSize, 8);
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
    /// Reads a COMRESSED_FULL inode whose map header carries extent metadata: the index area is
    /// a sorted array of variable-size z_erofs_extent records (fs/erofs/zmap.c z_erofs_map_blocks_ext).
    /// <para>读取携带 extent 元数据的 COMRESSED_FULL inode：索引区为按逻辑偏移排序的可变长度
    /// z_erofs_extent 记录数组（fs/erofs/zmap.c z_erofs_map_blocks_ext）。</para>
    /// </summary>
    private void WriteExtentsData(ulong nid, ErofsInode inode, ZErofsMapHeader header, byte[] headerBytes,
        long headerOffset, Stream output, CancellationToken cancellationToken)
    {
        var extentCount = (ulong)(BinaryPrimitives.ReadUInt32LittleEndian(headerBytes)
            | ((ulong)header.ExtentsHi << 32));
        var recSize = 4 << ((header.Advise >> ZErofsMapHeader.ExtRecSizeBit) & ZErofsMapHeader.ExtRecSizeMask);
        var extentsBase = checked((long)AlignUp(headerOffset + ZErofsMapHeader.Size, recSize));
        if (recSize is 4 or 8)
        {
            WriteShortExtentsData(nid, inode, header, extentsBase, recSize, output, cancellationToken);
            return;
        }
        if (extentCount == 0)
        {
            if (inode.Size == 0)
            {
                return;
            }
            throw new InvalidDataException($"EROFS extent count is zero for nonempty inode {nid}.");
        }
        if (recSize < 8)
        {
            throw new InvalidDataException($"EROFS extent record size {recSize} is too small.");
        }

        const int maxExtents = 1 << 24;
        if (extentCount > (ulong)maxExtents)
        {
            throw new InvalidDataException($"EROFS extent count {extentCount} exceeds the limit of {maxExtents}.");
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
                throw new InvalidDataException($"EROFS extent {i} of nid {nid} is not sorted.");
            }

            var logicalEnd = i + 1 < (long)extentCount
                ? ReadExtentLogicalStart(nid, checked(recordOffset + recSize), recSize)
                : checked((long)inode.Size);
            if (logicalEnd <= logicalStart || logicalEnd > (long)inode.Size)
            {
                throw new InvalidDataException($"EROFS extent {i} of nid {nid} has an invalid logical range.");
            }
            var logicalLength = logicalEnd - logicalStart;
            var physicalLength = plenRaw & ZErofsMapHeader.ExtentPlenMask;
            var fmt = plenRaw >> ZErofsMapHeader.ExtentPlenFmtBit;
            var physicalOffset = checked((long)(BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(4))
                | (ulong)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(8)) << 32));
            if (fmt > (uint)ErofsAlgorithm.Zstd + 1)
            {
                throw new InvalidDataException($"EROFS extent {i} of nid {nid} uses unknown algorithm {fmt - 1}.");
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
                    (ErofsAlgorithm)(fmt - 1), output, cancellationToken, physicalOffset);
            }

            logicalOffset = logicalEnd;
        }

        if (logicalOffset != (long)inode.Size)
        {
            throw new InvalidDataException($"EROFS extents of nid {nid} do not cover the file.");
        }
    }

    private long ReadExtentLogicalStart(ulong nid, long offset, int recordSize)
    {
        var record = ReadInodeMetadata(nid, offset, recordSize, "extent logical start");
        return checked((long)(BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(12))
            | (recordSize > 16 ? (ulong)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(16)) << 32 : 0)));
    }

    private void WriteShortExtentsData(ulong nid, ErofsInode inode, ZErofsMapHeader header,
        long indexStart, int recordSize, Stream output, CancellationToken cancellationToken)
    {
        if (inode.Size == 0)
        {
            return;
        }
        var clusterBits = _superblock.BlockSizeBits + (header.ClusterBits & ZErofsMapHeader.LclusterBitsMask);
        if (clusterBits > 23)
        {
            throw new InvalidDataException($"EROFS extent cluster size shift {clusterBits} exceeds the supported limit.");
        }
        var clusterSize = 1L << clusterBits;
        if (clusterSize > ErofsCompression.MaxPclusterDecompressedSize)
        {
            throw new InvalidDataException("EROFS short extent exceeds the decompression budget.");
        }
        var count = checked(((long)inode.Size - 1) / clusterSize + 1);
        if (count > 1 << 24)
        {
            throw new InvalidDataException("EROFS short extent count exceeds the supported limit.");
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
                    throw new InvalidDataException("EROFS plain short extent is smaller than its logical cluster.");
                }
                CheckRange(physical, logicalLength, "Plain short extent");
                CopyFromSource(output, physical, logicalLength);
            }
            else
            {
                if (format > (uint)ErofsAlgorithm.Zstd + 1)
                {
                    throw new InvalidDataException($"Unknown EROFS short extent compression algorithm {format - 1}.");
                }
                WriteCompressedExtent(nid, inode, record, recordSize, i * clusterSize, logicalLength,
                    (ErofsAlgorithm)(format - 1), output, cancellationToken, physical);
            }
            if (recordSize == 4)
            {
                physical = checked(physical + physicalLength);
            }
        }
    }

    private void WritePlainExtent(ulong nid, ErofsInode inode, byte[] record, int recSize,
        long logicalOffset, long logicalLength, long physicalOffset, Stream output, CancellationToken cancellationToken)
    {
        if (logicalLength <= 0)
        {
            return;
        }
        if ((BinaryPrimitives.ReadUInt32LittleEndian(record) & ZErofsMapHeader.ExtentPlenMask) < logicalLength)
        {
            throw new InvalidDataException("EROFS plain extent is smaller than its logical range.");
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

    private void WriteCompressedExtent(ulong nid, ErofsInode inode, byte[] record, int recSize,
        long logicalOffset, long logicalLength, ErofsAlgorithm algorithm, Stream output, CancellationToken cancellationToken,
        long? physicalOverride = null)
    {
        if (logicalLength <= 0)
        {
            return;
        }
        if (logicalLength > ErofsCompression.MaxPclusterDecompressedSize)
        {
            throw new InvalidDataException("EROFS compressed extent exceeds the decompression budget.");
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
            throw new InvalidDataException($"EROFS compressed extent of nid {nid} lacks a physical start.");
        }

        var plenRaw = BinaryPrimitives.ReadUInt32LittleEndian(record);
        var compressedLength = (int)(plenRaw & ZErofsMapHeader.ExtentPlenMask);
        if (compressedLength <= 0 || compressedLength > ErofsCompression.MaxPclusterSize)
        {
            throw new InvalidDataException($"EROFS extent of nid {nid} has invalid encoded length {compressedLength}.");
        }

        CheckRange(pstart, compressedLength, "Compressed extent data");
        var compressed = ArrayPool<byte>.Shared.Rent(compressedLength);
        var decoded = ArrayPool<byte>.Shared.Rent((int)logicalLength);
        try
        {
            lock (_streamLock)
            {
                _stream.Position = checked(_baseOffset + pstart);
                _stream.ReadExactly(compressed, 0, compressedLength);
            }

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
                    throw new InvalidDataException("EROFS compressed extent is entirely zero-padded.");
                }
                decodedLength = ErofsCompression.AlgorithmDecompress(
                    algorithm, compressed, inputMargin, compressedLength - inputMargin, decoded, 0, (int)logicalLength);
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException)
            {
                throw new InvalidDataException(
                    $"Extent of nid {nid} failed to decompress ({algorithm}): {ex.Message}");
            }

            if (decodedLength != (int)logicalLength)
            {
                throw new InvalidDataException(
                    $"Extent of nid {nid} decoded to {decodedLength} bytes, expected {logicalLength}; " +
                    $"algorithm={algorithm}, physical={pstart}, encoded={compressedLength}, prefix={Convert.ToHexString(compressed.AsSpan(0, Math.Min(16, compressedLength)))}.");
            }

            output.Write(decoded, 0, (int)logicalLength);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compressed);
            ArrayPool<byte>.Shared.Return(decoded);
        }
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
            throw new InvalidDataException("Fragment inode without a packed inode (s_packed_nid == 0).");
        }

        ErofsInode packed;
        lock (_streamLock)
        {
            _packedInode ??= ReadInode(packedNid);
            packed = _packedInode;
        }
        if (!packed.IsRegularFile && !packed.IsDirectory)
        {
            throw new InvalidDataException($"Packed inode nid {packedNid} is not a regular file.");
        }

        if (length == 0)
        {
            return;
        }

        var packedSize = (long)packed.Size;
        if (fragmentOff >= (ulong)packedSize || length > packedSize - (long)fragmentOff)
        {
            throw new InvalidDataException(
                $"Packed fragment offset {fragmentOff} and length {length} exceed the packed inode size {packedSize}.");
        }

        if (packed.IsCompressed)
        {
            CopyCompressedPackedRange(packedNid, packed, (long)fragmentOff, length, output);
            return;
        }

        var inlineAll = packed.DataLayout == ErofsInode.DataLayoutInline &&
                        packed.RawBlockAddress == uint.MaxValue;
        if (packed.DataLayout == ErofsInode.DataLayoutInline && !inlineAll)
        {

            var flatLength = packedSize / _blockSize * _blockSize;
            WritePackedRange((long)fragmentOff, length, flatLength, packed, packedNid, inlineStartIsFlat: false, output);
            return;
        }

        if (packed.DataLayout == ErofsInode.DataLayoutInline)
        {

            var start = checked(InodeOffset(packedNid) + packed.InlineDataOffset + (long)fragmentOff);
            if (IsMetaboxInode(packedNid))
            {
                output.Write(ReadMetaboxBytes(start, checked((int)length), "Packed fragment data"));
            }
            else
            {
                CheckRange(start, length, "Packed fragment data");
                CopyFromSource(output, start, length);
            }
            return;
        }

        if (packed.DataLayout != ErofsInode.DataLayoutFlat)
        {
            throw new InvalidDataException($"Unsupported packed inode layout {packed.DataLayout} for nid {packedNid}.");
        }

        WritePackedRange((long)fragmentOff, length, packedSize, packed, packedNid, inlineStartIsFlat: true, output);
    }

    private long CompressedIndexEnd(ulong nid, ErofsInode inode, long headerOffset, int count, int clusterBits)
    {
        if (inode.DataLayout == ErofsInode.DataLayoutCompressed)
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

    private void CopyCompressedPackedRange(ulong nid, ErofsInode inode, long offset, long length, Stream output)
    {
        lock (_streamLock)
        {
            if (_loadingPackedData) throw new InvalidDataException("Recursive EROFS packed inode reference.");
            if (_packedData is null)
            {
                if (inode.Size > 64UL << 30) throw new InvalidDataException("EROFS packed inode exceeds the 64 GiB staging limit.");
                var temporary = new FileStream(Path.Combine(Path.GetTempPath(), "uotan-erofs-packed-" + Guid.NewGuid().ToString("N")),
                    FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.DeleteOnClose);
                _loadingPackedData = true;
                try
                {
                    WriteCompressedData(nid, inode, temporary);
                    if (temporary.Length != (long)inode.Size) throw new InvalidDataException("EROFS packed inode length mismatch.");
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
        ErofsInode packed,
        ulong packedNid,
        bool inlineStartIsFlat,
        Stream output)
    {
        var remaining = length;
        var pos = offset;
        if (pos < flatLength)
        {
            var flatBytes = Math.Min(remaining, flatLength - pos);
            var flatStart = checked(packed.RawBlockAddress * _blockSize + pos);
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
                throw new InvalidDataException("Packed fragment straddles the flat/inline boundary unexpectedly.");
            }

            inlineStart += pos - flatLength;
        }
        else
        {

            if (pos >= flatLength)
            {
                throw new InvalidDataException("Packed fragment extends beyond a pure-flat packed inode.");
            }
        }

        if (IsMetaboxInode(packedNid))
        {
            output.Write(ReadMetaboxBytes(inlineStart, checked((int)remaining), "Packed fragment data"));
        }
        else
        {
            CheckRange(inlineStart, remaining, "Packed fragment data");
            CopyFromSource(output, inlineStart, remaining);
        }
    }
}