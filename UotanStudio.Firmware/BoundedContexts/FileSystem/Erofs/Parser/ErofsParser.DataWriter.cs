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
                    if (mapped.MappedBlkAddr != 0 && startBlk >= mapped.MappedBlkAddr && startBlk - mapped.MappedBlkAddr < mapped.Blocks)
                    {
                        deviceId = mappedId;
                        startBlk -= mapped.MappedBlkAddr;
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
    private void WriteCompressedData(ulong nid, ErofsInode inode, Stream output)
    {
        var inodeLoc = _superblock.GetInodeOffset(nid);


        var headerOffset = AlignUp(inodeLoc + inode.OnDiskSize + inode.XattrTotalSize, 8);
        var headerBytes = ReadBytes(headerOffset, ZErofsMapHeader.Size, "compressed map header");
        var header = ZErofsMapHeader.FromBytes(headerBytes);


        if (header.IsFragmentInode)
        {
            var fragmentOff = header.FragmentOff & ~(1u << ZErofsMapHeader.FragmentInodeBit);
            WritePackedData(inode, fragmentOff, output);
            return;
        }

        var advise = header.Advise;
        var algorithmHead1 = header.Algorithm;


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

        // 预读紧凑索引表：同一 inode 的所有 LoadLcluster 调用复用缓存，避免 O(N×H) 次重复磁盘 I/O。
        // Pre-read compact index table: all LoadLcluster calls for the same inode reuse the cache,
        // avoiding O(N×H) repeated disk I/O.
        byte[]? cachedIndex = null;
        if (inode.DataLayout == ErofsInode.DataLayoutCompactIndexes)
        {
            var ebase = headerOffset + ZErofsMapHeader.Size;
            var indexLength = checked((long)totalIdx * 4 + 64);
            if (indexLength <= MaxMetadataComponentSize)
                cachedIndex = ReadBytes(ebase, checked((int)Math.Min(indexLength, _imageSize - ebase)), "compact index table");
        }

        // 一次性预扫描全部 lcluster，建立 head → nextHead 映射，消除内层 while(true) 线性搜索。
        // Pre-scan all lclusters once to build head → nextHead mapping, eliminating the inner while(true) linear search.
        var headMap = PreScanLclusterHeads(nid, inode, headerOffset, advise, lclusterBits, totalIdx, cachedIndex);

        var compressed = ArrayPool<byte>.Shared.Rent(_blockSize * 2);
        var decoded = ArrayPool<byte>.Shared.Rent(Math.Max(lclusterSize, _blockSize));
        try
        {
            long lcn = 0;
            while (lcn < totalIdx)
            {
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


                var nextLcn = headMap[lcn];
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

                var llen = Math.Min(nextMLa, (long)inode.Size) - mLa;
                if (llen <= 0)
                {
                    lcn = nextLcn;
                    continue;
                }

                if (llen > MaxMetadataComponentSize)
                {
                    throw new InvalidDataException(
                        $"Pcluster decompressed size {llen} exceeds the limit of {MaxMetadataComponentSize} bytes.");
                }


                var isTailPacked = tailPacking && nextLcn >= totalIdx;
                if (isTailPacked)
                {
                    var idataSize = (int)header.IdataSize;
                    if (idataSize < 0 || idataSize > llen)
                    {
                        throw new InvalidDataException($"Invalid tailpacking idata size {idataSize} for nid {nid}.");
                    }

                    if (idataSize == 0)
                    {

                        WriteZeroes(output, llen);
                        lcn = nextLcn;
                        continue;
                    }

                    var tailStart = headerOffset + ZErofsMapHeader.Size;
                    CheckRange(tailStart, idataSize, "Tailpacked data");
                    CopyFromSource(output, tailStart, idataSize);

                    if (llen > idataSize)
                    {
                        WriteZeroes(output, llen - idataSize);
                    }

                    lcn = nextLcn;
                    continue;
                }

                if (lc.Type == LclusterTypePlain)
                {


                    var dataStart = checked(lc.Pblk * _blockSize);
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
                if (!head1NotBig && !plainOrHead2NotBig && !atEof)
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

                var compressedByteLen = checked(compressedBlocks * _blockSize);
                if (compressedByteLen <= 0 || lc.Pblk < 0)
                {
                    throw new InvalidDataException("Invalid compressed pcluster for nid {nid}.");
                }

                var pblkStart = checked(lc.Pblk * _blockSize);
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
                while (inputMargin < compressedByteLen && compressed[inputMargin] == 0)
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

                output.Write(decoded, 0, (int)llen);
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
    /// One-pass pre-scan of all lcluster entries for a compressed inode: builds a head → nextHead
    /// index array so the main decompression loop can jump directly instead of linearly scanning.
    /// <para>一次性预扫描压缩 inode 的全部 lcluster 条目，建立 head → nextHead 索引数组，
    /// 使主解压循环可直接跳转而无需线性搜索。</para>
    /// </summary>
    private long[] PreScanLclusterHeads(
        ulong nid,
        ErofsInode inode,
        long headerOffset,
        ushort advise,
        int lclusterBits,
        int totalIdx,
        byte[]? cachedIndex)
    {
        var heads = new long[totalIdx];
        Array.Fill(heads, -1L);
        long lastHead = -1;
        for (long i = 0; i < totalIdx; i++)
        {
            var lc = LoadLcluster(nid, inode, headerOffset, advise, lclusterBits, totalIdx, i,
                out _, out _, out _, cachedIndex);
            if (lc.Type != LclusterTypeNonHead)
            {
                if (lastHead >= 0)
                    heads[lastHead] = i;
                lastHead = i;
            }
        }

        if (lastHead >= 0)
            heads[lastHead] = totalIdx;
        return heads;
    }

    private static void WriteZeroes(Stream output, long count)
    {
        var zeroes = new byte[Math.Min(count, 1 << 16)];
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
    private void WritePackedData(ErofsInode fragmentInode, uint fragmentOff, Stream output)
    {
        var packedNid = _superblock.PackedNid;
        if (packedNid == 0)
        {
            throw new InvalidDataException("Fragment inode without a packed inode (s_packed_nid == 0).");
        }

        _packedInode ??= ReadInode(packedNid);
        var packed = _packedInode;
        if (!packed.IsRegularFile && !packed.IsDirectory)
        {
            throw new InvalidDataException($"Packed inode nid {packedNid} is not a regular file.");
        }

        var length = (long)fragmentInode.Size;
        if (length == 0)
        {
            return;
        }


        var packedSize = (long)packed.Size;
        if (fragmentOff >= packedSize || length > packedSize - fragmentOff)
        {
            throw new InvalidDataException(
                $"Packed fragment range [{fragmentOff}..{fragmentOff + length}) exceeds the packed inode size {packedSize}.");
        }

        var inlineAll = packed.DataLayout == ErofsInode.DataLayoutInline &&
                        packed.RawBlockAddress == uint.MaxValue;
        if (packed.DataLayout == ErofsInode.DataLayoutInline && !inlineAll)
        {


            var flatLength = packedSize / _blockSize * _blockSize;
            WritePackedRange(fragmentOff, length, flatLength, packed, packedNid, inlineStartIsFlat: false, output);
            return;
        }

        if (packed.DataLayout == ErofsInode.DataLayoutInline)
        {

            var start = _superblock.GetInodeOffset(packedNid) + packed.InlineDataOffset + fragmentOff;
            CheckRange(start, length, "Packed fragment data");
            CopyFromSource(output, start, length);
            return;
        }

        if (packed.DataLayout != ErofsInode.DataLayoutFlat)
        {
            throw new InvalidDataException($"Unsupported packed inode layout {packed.DataLayout} for nid {packedNid}.");
        }


        WritePackedRange(fragmentOff, length, packedSize, packed, packedNid, inlineStartIsFlat: true, output);
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