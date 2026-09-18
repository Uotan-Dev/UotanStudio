using System.Buffers;
using System.Buffers.Binary;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;

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
            var tailStart = _superblock.GetInodeOffset(nid) + inode.InlineDataOffset;
            CheckRange(tailStart, tailLength, "Tailpacked inline data");
            CopyFromSource(output, tailStart, tailLength);
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
        var indexTableOffset = checked((_superblock.GetInodeOffset(nid) + inode.InlineDataOffset + indexSize - 1) & ~(long)(indexSize - 1));
        var indexBytes = usesIndexes
            ? checked(chunkCount * 8L)
            : checked(chunkCount * 4L);
        if (indexBytes > MaxMetadataComponentSize)
        {
            throw new InvalidDataException(
                $"Chunk index table size {indexBytes} exceeds the limit of {MaxMetadataComponentSize} bytes.");
        }

        var index = ReadBytes(indexTableOffset, checked((int)indexBytes), "Chunk index table");
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
    /// Reads compressed file data through compact or full EROFS cluster indexes.
    /// <para>通过紧凑或完整的 EROFS 簇索引读取压缩文件数据。</para>
    /// </summary>
    private void WriteCompressedData(ulong nid, ErofsInode inode, Stream output,
        CompressionObserver? observer = null, CancellationToken cancellationToken = default)
    {
        var inodeLoc = _superblock.GetInodeOffset(nid);


        var headerOffset = AlignUp(inodeLoc + inode.OnDiskSize + inode.XattrTotalSize, 8);
        var headerBytes = ReadBytes(headerOffset, ZErofsMapHeader.Size, "compressed map header");
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

        // COMRESSED_FULL with extent metadata (Z_EROFS_ADVISE_EXTENTS): the index area holds
        // variable-size extent records instead of lcluster indexes (fs/erofs/zmap.c z_erofs_map_blocks_ext).
        // COMRESSED_FULL 且启用 extent 元数据（Z_EROFS_ADVISE_EXTENTS）：索引区为可变长度 extent
        // 记录而非 lcluster 索引（fs/erofs/zmap.c z_erofs_map_blocks_ext）。
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
        var cachedIndex = new IndexReadWindow(this, indexBase, Math.Min(indexLength, _imageSize - indexBase));

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

                // Pre-big-pcluster kernels (Linux 5.4 / 5.10, fs/erofs/zmap.c) never clamp the
                // extent end to i_size: the decompressor must produce exactly the covered whole
                // clusters, so the kernel expects (nextLcn << lclusterbits) - m_la here.
                // pre-big-pcluster 内核（Linux 5.4 / 5.10，fs/erofs/zmap.c）不会把 extent 末端
                // 裁剪到 i_size：解压输出必须恰好等于覆盖的整簇数，即 (nextLcn << lclusterbits) - m_la。
                var kernelExpectedOutputSize = checked((int)(((long)nextLcn << lclusterBits) - mLa));

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

                if (lc.Type == LclusterTypePlain)
                {


                    var dataStart = isTailPacked ? CompressedIndexEnd(inode, headerOffset, totalIdx, lclusterBits) : checked(lc.Pblk * _blockSize);
                    if (!interlaced)
                    {
                        CheckRange(dataStart, llen, "Plain lcluster data");
                        CopyFromSource(output, dataStart, llen);
                        lcn = nextLcn;
                        continue;
                    }


                    if (llen > _blockSize || llen < 0)
                    {
                        throw new InvalidDataException(
                            $"Interlaced plain pcluster {lcn} of nid {nid} has length {llen} > block size {_blockSize}.");
                    }

                    var skip = (int)(mLa & (_blockSize - 1));
                    CheckRange(dataStart, _blockSize, "Interlaced plain lcluster block");
                    var rawBlock = ReadBytes(dataStart, _blockSize, "Interlaced plain lcluster block");
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
                    throw new InvalidDataException("Invalid compressed pcluster for nid {nid}.");
                }

                var pblkStart = isTailPacked ? CompressedIndexEnd(inode, headerOffset, totalIdx, lclusterBits) : checked(lc.Pblk * _blockSize);
                CheckRange(pblkStart, compressedByteLen, "Compressed pcluster data");
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

                lock (_streamLock)
                {
                    _stream.Position = checked(_baseOffset + pblkStart);
                    _stream.ReadExactly(compressed, 0, compressedByteLen);
                }


                var inputMargin = 0;
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
                int decodedLen;
                try
                {
                    decodedLen = ErofsCompression.AlgorithmDecompress(
                        algorithm, compressed, inputMargin, compressedByteLen - inputMargin, decoded, 0, (int)llen);
                }
                catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException)
                {
                    throw new InvalidDataException(
                        $"Pcluster {lcn} of nid {nid} failed to decompress ({algorithm}): {ex.Message}");
                }

                if (decodedLen != (int)llen)
                {
                    throw new InvalidDataException(
                        $"Pcluster {lcn} decoded to {decodedLen} bytes, expected {llen}.");
                }

                if (observer is not null)
                {
                    if (!observer((ErofsCompressionAlgorithm)algorithm,
                        compressed.AsSpan(inputMargin, compressedByteLen - inputMargin), decoded.AsSpan(0, (int)llen), lc.ClusterOfs, kernelExpectedOutputSize)) return;
                }
                else output.Write(decoded, 0, (int)llen);
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

    /// <summary>
    /// Reads a COMRESSED_FULL inode whose map header carries extent metadata: the index area is
    /// a sorted array of variable-size z_erofs_extent records (fs/erofs/zmap.c z_erofs_map_blocks_ext).
    /// <para>读取携带 extent 元数据的 COMRESSED_FULL inode：索引区为按逻辑偏移排序的可变长度
    /// z_erofs_extent 记录数组（fs/erofs/zmap.c z_erofs_map_blocks_ext）。</para>
    /// </summary>
    private void WriteExtentsData(ulong nid, ErofsInode inode, ZErofsMapHeader header, byte[] headerBytes,
        long headerOffset, Stream output, CancellationToken cancellationToken)
    {
        // Extent count lives in the header union: h_extents_lo (bytes 0-3) + h_extents_hi (bytes 6-7).
        // extent 总数位于头部联合体：h_extents_lo（字节 0-3）+ h_extents_hi（字节 6-7）。
        var extentCount = (ulong)(BinaryPrimitives.ReadUInt32LittleEndian(headerBytes)
            | ((ulong)header.ExtentsHi << 32));
        if (extentCount == 0)
        {
            return;
        }

        var recSize = 4 << ((header.Advise >> ZErofsMapHeader.ExtRecSizeBit) & ZErofsMapHeader.ExtRecSizeMask);
        var extentsBase = checked((long)AlignUp(headerOffset + ZErofsMapHeader.Size, recSize));
        // A record holds at least plen + pstart_lo; anything shorter cannot address data.
        // 记录至少容纳 plen + pstart_lo；更短的记录无法寻址数据。
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
        var physicalOffset = -1L;
        for (var i = 0; i < (long)extentCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recordOffset = checked(extentsBase + i * recSize);
            var record = ReadBytes(recordOffset, recSize, $"extent record {i}");
            var plenRaw = BinaryPrimitives.ReadUInt32LittleEndian(record);
            var logicalStart = (long)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(12));
            if (recSize > 20)
            {
                logicalStart |= (long)BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(16)) << 32;
            }

            // Records are sorted by logical start; the previous record's logical end is the
            // logical length of this extent (fs/erofs/zmap.c: m_llen = lend - m_la).
            // 记录按逻辑偏移排序；前一条记录的逻辑终点即本 extent 的逻辑长度。
            if (logicalStart < logicalOffset)
            {
                throw new InvalidDataException($"EROFS extent {i} of nid {nid} is not sorted.");
            }

            var logicalLength = logicalStart - logicalOffset;
            var physicalLength = plenRaw & ZErofsMapHeader.ExtentPlenMask;
            var fmt = plenRaw >> ZErofsMapHeader.ExtentPlenFmtBit;
            var isPartial = (plenRaw & ZErofsMapHeader.ExtentPlenPartial) != 0;
            if (fmt > (uint)ErofsAlgorithm.Zstd + 1)
            {
                throw new InvalidDataException($"EROFS extent {i} of nid {nid} uses unknown algorithm {fmt - 1}.");
            }

            // Fragment tail extent: data lives in the packed inode, addressed by (plen, pstart).
            // fragment 尾部 extent：数据在 packed inode 中，以 (plen, pstart) 寻址。
            if (isPartial && physicalLength == 0)
            {
                WriteZeroes(output, logicalLength);
                logicalOffset = logicalStart;
                continue;
            }

            if (fmt == 0)
            {
                // fmt == 0: plain/interlaced extent — the pstart is a byte address into the image.
                // fmt == 0：未压缩 extent —— pstart 为镜像内的字节地址。
                WritePlainExtent(nid, inode, record, recSize, logicalOffset, logicalLength, physicalOffset, output, cancellationToken);
            }
            else
            {
                WriteCompressedExtent(nid, inode, record, recSize, logicalOffset, logicalLength,
                    (ErofsAlgorithm)(fmt - 1), output, cancellationToken);
            }

            // Advance the physical cursor for records that do not carry an absolute pstart
            // (recsz 4: pstart_lo is absent, so extents are packed consecutively).
            // 对不带绝对 pstart 的记录（recsz 4 无 pstart_lo）推进物理游标，extent 连续存放。
            if (recSize > 4)
            {
                var pstart = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(4));
                if (recSize > 8)
                {
                    pstart |= (uint)(BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(8)) << 0);
                }

                physicalOffset = checked((long)pstart * (recSize > 12 ? 1 : 1));
            }

            logicalOffset = logicalStart;
        }

        if (logicalOffset < (long)inode.Size)
        {
            WriteZeroes(output, (long)inode.Size - logicalOffset);
        }
    }

    private void WritePlainExtent(ulong nid, ErofsInode inode, byte[] record, int recSize,
        long logicalOffset, long logicalLength, long physicalOffset, Stream output, CancellationToken cancellationToken)
    {
        if (logicalLength <= 0)
        {
            return;
        }

        long source;
        if (recSize > 8)
        {
            var pstartLo = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(4));
            var pstartHi = recSize > 12 ? BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(8)) : 0;
            source = checked((long)(pstartLo | (pstartHi << 0)));
        }
        else
        {
            source = checked(physicalOffset + logicalOffset);
        }

        CheckRange(source, logicalLength, "Plain extent data");
        CopyFromSource(output, source, logicalLength);
    }

    private void WriteCompressedExtent(ulong nid, ErofsInode inode, byte[] record, int recSize,
        long logicalOffset, long logicalLength, ErofsAlgorithm algorithm, Stream output, CancellationToken cancellationToken)
    {
        if (logicalLength <= 0)
        {
            return;
        }

        long pstart;
        if (recSize > 8)
        {
            var pstartLo = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(4));
            var pstartHi = recSize > 12 ? BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(8)) : 0;
            pstart = checked((long)pstartLo);
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
                decodedLength = ErofsCompression.AlgorithmDecompress(
                    algorithm, compressed, 0, compressedLength, decoded, 0, (int)logicalLength);
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or ArgumentException)
            {
                throw new InvalidDataException(
                    $"Extent of nid {nid} failed to decompress ({algorithm}): {ex.Message}");
            }

            if (decodedLength != (int)logicalLength)
            {
                throw new InvalidDataException(
                    $"Extent of nid {nid} decoded to {decodedLength} bytes, expected {logicalLength}.");
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

            var start = checked(_superblock.GetInodeOffset(packedNid) + packed.InlineDataOffset + (long)fragmentOff);
            CheckRange(start, length, "Packed fragment data");
            CopyFromSource(output, start, length);
            return;
        }

        if (packed.DataLayout != ErofsInode.DataLayoutFlat)
        {
            throw new InvalidDataException($"Unsupported packed inode layout {packed.DataLayout} for nid {packedNid}.");
        }


        WritePackedRange((long)fragmentOff, length, packedSize, packed, packedNid, inlineStartIsFlat: true, output);
    }

    private long CompressedIndexEnd(ErofsInode inode, long headerOffset, int count, int clusterBits)
    {
        if (inode.DataLayout == ErofsInode.DataLayoutCompressed)
            return checked(headerOffset + ZErofsMapHeader.Size + FullIndexStartPad + (long)count * 8);
        var header = ZErofsMapHeader.FromBytes(ReadBytes(headerOffset, ZErofsMapHeader.Size, "inline index header"));
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
        var inlineStart = _superblock.GetInodeOffset(packedNid) + inlineOffset;
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

        CheckRange(inlineStart, remaining, "Packed fragment data");
        CopyFromSource(output, inlineStart, remaining);
    }
}