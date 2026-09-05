using System.Buffers;
using System.Buffers.Binary;

namespace UotanStudio.Firmware.Erofs;

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
            // 特殊文件（字符/块设备、FIFO、套接字）无数据内容：i_u 存设备号而非块地址，
            // 按数据布局读取会得到错误字节。显式失败而非静默产出伪数据。
            // Special files (char/block devices, FIFOs, sockets) carry no data: i_u holds the
            // device number instead of a block address, so reading by data layout would yield
            // garbage. Fail loudly instead of silently producing bogus bytes.
            throw new InvalidDataException(
                $"Inode nid {nid} is a special file (mode 0x{inode.Mode:X4}) with no data content.");
        }

        switch (inode.DataLayout)
        {
            case ErofsInode.DataLayoutInline:
                // inline 布局（EROFS_INODE_FLAT_INLINE）：文件数据紧随 inode 与 inline xattr 之后，
                // 位于同一元数据块内；i_u 为哨兵 0xffffffff 时表示整个文件都是 inline 数据。
                // Inline layout (EROFS_INODE_FLAT_INLINE): the file data follows the inode and inline
                // xattrs within the same metadata block; when i_u is the sentinel 0xffffffff the whole
                // file is inline data.
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
                // Fail loudly instead of silently reading raw bytes as file content.
                // 显式失败而非静默把原始字节当作文件内容。
                throw new InvalidDataException($"Unsupported EROFS data layout {inode.DataLayout} for inode nid {nid}.");
        }
    }

    private void WriteInlineData(ulong nid, ErofsInode inode, Stream output)
    {
        var length = (long)inode.Size;
        if (length == 0)
        {
            return;
        }

        // FLAT_INLINE = tail-packing：文件主体为 flat 数据（自 i_u 块起连续），仅最后一个
        // 不足一块的尾部内联在 inode + xattr 之后（内核 data.c __erofs_map_blocks：
        // pos = (ceil(size/blksz) - 1) * blksz；m_la < pos 时 m_pa = i_u*blksz + m_la，
        // 否则从 iloc + isize + xattr_isize 读取内联尾部）。mkfs 的 flat 块数 =
        // floor(size/blksz)（size 恰为整块时无内联尾）。
        // FLAT_INLINE means tail-packing: the bulk of the file is flat data (contiguous from the
        // i_u block) and only the trailing part (less than one block) is inlined right after the
        // inode + xattrs. mkfs stores floor(size/blksz) flat blocks (no inline tail when size is an
        // exact multiple of the block size).
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
    /// 读取 chunk-based 文件（EROFS_INODE_CHUNK_BASED）：按 chunk 索引表逐块拼接数据。
    /// chunk 索引（8 字节 erofs_inode_chunk_index：startblk_hi u16 + device_id u16 + startblk_lo u32）
    /// 位于 inode + xattr 之后；chunk 大小 = 块大小 &lt;&lt; chunk blkbits（chunk format 低 5 位）。
    /// EROFS_NULL_ADDR（0xFFFFFFFFFFFFFFFF）表示空洞（零填充）。
    /// <para>Reads a chunk-based file (EROFS_INODE_CHUNK_BASED): assembles data chunk by chunk from
    /// the chunk index table. Chunk indexes (8-byte erofs_inode_chunk_index: startblk_hi u16 +
    /// device_id u16 + startblk_lo u32) sit right after the inode + xattrs; the chunk size equals
    /// block size &lt;&lt; chunk blkbits (low 5 bits of the chunk format). EROFS_NULL_ADDR
    /// (0xFFFFFFFFFFFFFFFF) marks a hole (zero fill).</para>
    /// </summary>
    private void WriteChunkData(ulong nid, ErofsInode inode, Stream output)
    {
        var length = (long)inode.Size;
        if (length == 0)
        {
            return;
        }

        var chunkBlkBits = inode.ChunkFormatBlkBits;
        var chunkSize = _blockSize << chunkBlkBits;
        if (chunkSize <= 0)
        {
            throw new InvalidDataException($"Invalid chunk size (blkbits {chunkBlkBits}) for nid {nid}.");
        }

        var chunkCount = (length + chunkSize - 1) / chunkSize;
        var usesIndexes = inode.UsesChunkIndexes;
        var indexTableOffset = _superblock.GetInodeOffset(nid) + inode.InlineDataOffset;
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
            var chunkLen = (int)Math.Min(chunkSize, remaining);
            ulong startBlk;
            var deviceId = 0;
            if (usesIndexes)
            {
                var slot = index.AsSpan(i * 8, 8);
                startBlk = ((ulong)BinaryPrimitives.ReadUInt16LittleEndian(slot) << 32)
                           | BinaryPrimitives.ReadUInt32LittleEndian(slot.Slice(4));
                deviceId = BinaryPrimitives.ReadUInt16LittleEndian(slot.Slice(2));
            }
            else
            {
                // 4 字节块号数组（EROFS_BLOCK_MAP_ENTRY_SIZE）：块号按顺序拼接。
                // 4-byte block-number array: block numbers concatenated in order.
                startBlk = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(i * 4, 4));
            }

            if (startBlk == ErofsNullAddr)
            {
                // 空洞：零填充。Hole: zero fill.
                var zeroes = new byte[Math.Min(chunkLen, 1 << 16)];
                var zeroed = 0;
                while (zeroed < chunkLen)
                {
                    var n = Math.Min(zeroes.Length, chunkLen - zeroed);
                    output.Write(zeroes, 0, n);
                    zeroed += n;
                }

                remaining -= chunkLen;
                continue;
            }

            if (deviceId > 0 && _deviceStreams is not null && deviceId < _deviceStreams.Length)
            {
                var devOffset = checked((long)startBlk * _blockSize);
                var devData = ReadDeviceBytes(deviceId, devOffset, chunkLen, "Multi-device chunk data");
                output.Write(devData, 0, devData.Length);
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

    /// <summary>空洞哨兵块号（EROFS_NULL_ADDR = -1）。Hole sentinel block number (EROFS_NULL_ADDR = -1).</summary>
    private const ulong ErofsNullAddr = ulong.MaxValue;

    // ---- 压缩数据读取（内核 fs/erofs/zmap.c 语义）。Compressed data reading (kernel zmap.c). ----

    private const byte LclusterTypePlain = 0;
    private const byte LclusterTypeHead1 = 1;
    private const byte LclusterTypeNonHead = 2;
    private const byte LclusterTypeHead2 = 3;

    /// <summary>z_erofs_map_header 之后、紧凑索引起始前的固定字节数（内核 Z_EROFS_MAP_HEADER_END 之后 8 字节）。
    /// Fixed bytes between z_erofs_map_header and the full index start (8 bytes after Z_EROFS_MAP_HEADER_END).</summary>
    private const int FullIndexStartPad = 8;

    /// <summary>
    /// 读取压缩文件（COMPRESSED_FULL / COMPRESSED_COMPACT）。严格对照内核
    /// <c>z_erofs_fill_inode</c> + <c>z_erofs_map_blocks_fo</c> + <c>z_erofs_load_lcluster_from_disk</c>：
    /// 读取 8 字节 z_erofs_map_header 确定算法与簇大小，按紧凑（4B/2B 位级）或完整（8B）索引逐
    /// pcluster 解压，支持 big pcluster（D0_CBLKCNT）、PLAIN 字面簇、tailpacking（INLINE_PCLUSTER）
    /// 与 fragment 引用。
    /// <para>Reads a compressed file (COMPRESSED_FULL / COMPRESSED_COMPACT), strictly following the
    /// kernel <c>z_erofs_fill_inode</c> + <c>z_erofs_map_blocks_fo</c> +
    /// <c>z_erofs_load_lcluster_from_disk</c>: reads the 8-byte z_erofs_map_header to determine the
    /// algorithm and cluster size, then decompresses pcluster by pcluster from compact (bit-level
    /// 4B/2B) or full (8B) indexes, supporting big pclusters (D0_CBLKCNT), PLAIN literal clusters,
    /// tailpacking (INLINE_PCLUSTER) and fragment references.</para>
    /// </summary>
    private void WriteCompressedData(ulong nid, ErofsInode inode, Stream output)
    {
        var inodeLoc = _superblock.GetInodeOffset(nid);
        // 压缩配置头 z_erofs_map_header（8 字节）位于 inode + xattr 之后、8 字节对齐处。
        // The z_erofs_map_header (8 bytes) sits right after the inode + xattrs, aligned to 8 bytes.
        var headerOffset = AlignUp(inodeLoc + inode.OnDiskSize + inode.XattrTotalSize, 8);
        var headerBytes = ReadBytes(headerOffset, ZErofsMapHeader.Size, "compressed map header");
        var header = ZErofsMapHeader.FromBytes(headerBytes);

        // fragment-inode：整文件打包在 packed inode 中（h_clusterbits bit7）。
        // Fragment inode: the whole file is packed into the packed inode (h_clusterbits bit 7).
        if (header.IsFragmentInode)
        {
            var fragmentOff = header.FragmentOff & ~(1u << ZErofsMapHeader.FragmentInodeBit);
            WritePackedData(inode, fragmentOff, output);
            return;
        }

        var advise = header.Advise;
        var algorithmHead1 = header.Algorithm;
        // HEAD2 算法编码于 h_algorithmtype 高 4 位（内核 z_erofs_fill_inode）。
        // The HEAD2 algorithm is encoded in the high nibble of h_algorithmtype (kernel z_erofs_fill_inode).
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
        // 交错 pcluster（Z_EROFS_ADVISE_INTERLACED_PCLUSTER）：仅对 PLAIN 字面簇生效——mkfs
        // 把相邻 pcluster 的数据交错填入同一物理块以消除块间空洞，读端需整块读入后按块内
        // 偏移旋转还原（内核 z_erofs_map_blocks_fo：PLAIN 簇 + 该 advise → m_algorithmformat =
        // INTERLACED；z_erofs_decompress 的 INTERLACED 分支做旋转复制）。
        // Interlaced pclusters (Z_EROFS_ADVISE_INTERLACED_PCLUSTER) affect only PLAIN literal
        // clusters: mkfs interleaves neighbouring pclusters into one physical block to remove
        // intra-block holes, so the reader must load the whole block and rotate it back by the
        // in-block offset (kernel z_erofs_map_blocks_fo: PLAIN + this advise →
        // m_algorithmformat = INTERLACED; z_erofs_decompress handles it with a rotated copy).
        var interlaced = (advise & ZErofsMapHeader.AdviseInterlacedPcluster) != 0;
        if (bigPcluster1 != bigPcluster2)
        {
            // 内核要求 HEAD1/HEAD2 big-pcluster 标志一致；不一致视为损坏。
            // The kernel requires the HEAD1/HEAD2 big-pcluster advises to be consistent.
            throw new InvalidDataException($"Inconsistent big-pcluster advises 0x{advise:X4} for nid {nid}.");
        }

        var compressed = ArrayPool<byte>.Shared.Rent(_blockSize * 2);
        var decoded = ArrayPool<byte>.Shared.Rent(Math.Max(lclusterSize, _blockSize));
        try
        {
            long lcn = 0;
            while (lcn < totalIdx)
            {
                var lc = LoadLcluster(nid, inode, headerOffset, advise, lclusterBits, totalIdx, lcn, out var ebase, out var compacted4bInitial, out var compacted2b);
                if (lc.Type == LclusterTypeNonHead)
                {
                    // NONHEAD 簇属于其前一个 HEAD pcluster，随 HEAD 一起解压；跳过。
                    // This cluster belongs to the previous HEAD pcluster and is covered by its
                    // decompressed data; skip it.
                    lcn++;
                    continue;
                }

                // HEAD/PLAIN 簇：逻辑起点 mLa = (lcn << lclusterbits) | clusterofs。
                // HEAD/PLAIN cluster: logical start mLa = (lcn << lclusterbits) | clusterofs.
                var mLa = lcn * lclusterSize + lc.ClusterOfs;
                if (mLa >= (long)inode.Size)
                {
                    lcn++;
                    continue;
                }

                // 向后找下一个 HEAD（跳过 NONHEAD）以确定本 pcluster 的解压长度边界。
                // Scan forward past NONHEAD clusters to find the next HEAD, bounding this pcluster.
                var nextLcn = lcn + 1;
                long nextMLa;
                while (true)
                {
                    if (nextLcn >= totalIdx)
                    {
                        nextMLa = (long)inode.Size;
                        break;
                    }

                    var t2 = LoadLcluster(nid, inode, headerOffset, advise, lclusterBits, totalIdx, nextLcn, out _, out _, out _);
                    if (t2.Type != LclusterTypeNonHead)
                    {
                        nextMLa = nextLcn * lclusterSize + t2.ClusterOfs;
                        break;
                    }

                    nextLcn++;
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

                // 尾部打包（tailpacking）：最后一个 pcluster 的数据内联在 map header 之后。
                // Tail packing: the last pcluster's data is inlined right after the map header.
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
                        // 无内联数据时按字面簇处理（空洞？零填充）。No inline data: treat as a hole.
                        WriteZeroes(output, llen);
                        lcn = nextLcn;
                        continue;
                    }

                    var tailStart = headerOffset + ZErofsMapHeader.Size;
                    CheckRange(tailStart, idataSize, "Tailpacked data");
                    CopyFromSource(output, tailStart, idataSize);
                    // 尾部打包的剩余部分为逻辑空洞（零填充）。The rest of a tailpacked pcluster is a hole.
                    if (llen > idataSize)
                    {
                        WriteZeroes(output, llen - idataSize);
                    }

                    lcn = nextLcn;
                    continue;
                }

                if (lc.Type == LclusterTypePlain)
                {
                    // 字面簇：数据未压缩。普通情形（SHIFTED）物理上从 pblk 块起始连续存放；
                    // clusterofs 是逻辑簇内偏移，只用于 m_la 计算，不叠加到物理位置（内核
                    // z_erofs_map_blocks_fo 的 m_pa = erofs_pos(sb, m.pblk)，随后按 SHIFTED
                    // 直接复制 m_llen 字节）。
                    // Literal cluster: the data is stored uncompressed. In the normal (SHIFTED)
                    // case it is contiguous from the pblk block start; clusterofs is the logical
                    // in-cluster offset used only for m_la, never added to the physical position
                    // (kernel z_erofs_map_blocks_fo sets m_pa = erofs_pos(sb, m.pblk) then copies
                    // m_llen bytes as SHIFTED).
                    var dataStart = checked(lc.Pblk * _blockSize);
                    if (!interlaced)
                    {
                        CheckRange(dataStart, llen, "Plain lcluster data");
                        CopyFromSource(output, dataStart, llen);
                        lcn = nextLcn;
                        continue;
                    }

                    // 交错 pcluster（INTERLACED）：mkfs 把相邻 pcluster 交错填入同一物理块，
                    // 字面数据在该块内从 skip = blkoff(m_la) 处起连续存放并绕回块首。读端
                    // 整块读入后旋转还原（对照 erofs-utils z_erofs_decompress 的 INTERLACED
                    // 分支：rightpart = min(blksz - skip, count)，先取 in[skip..]，再取块首
                    // 剩余）。该特性仅由 mkfs 交错写端产生，真实镜像罕见；约束不符即显式失败。
                    // Interlaced pcluster (INTERLACED): mkfs interleaves neighbouring pclusters
                    // into one physical block, so the literal data starts at skip = blkoff(m_la)
                    // inside the block and wraps around the block head. The reader loads the whole
                    // block and rotates it back (cf. z_erofs_decompress INTERLACED branch:
                    // rightpart = min(blksz - skip, count), take in[skip..] first then the block
                    // head remainder). Only the mkfs interleaved writer produces this; real images
                    // rarely do. Constraints that do not hold fail loudly.
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

                // HEAD1/HEAD2 压缩簇：确定压缩块数并解压。
                // HEAD1/HEAD2 compressed cluster: determine the compressed block count and decompress.
                var compressedBlocks = 1;
                var headType = lc.Type;
                var head1NotBig = headType == LclusterTypeHead1 && !bigPcluster1;
                var plainOrHead2NotBig = (headType == LclusterTypePlain || headType == LclusterTypeHead2) && !bigPcluster2;
                var atEof = (lcn << lclusterBits) >= (long)inode.Size;
                if (!head1NotBig && !plainOrHead2NotBig && !atEof)
                {
                    // 需要从下一个 NONHEAD 的 D0_CBLKCNT 取得压缩块数（big pcluster）。
                    // The compressed block count comes from the next NONHEAD's D0_CBLKCNT (big pcluster).
                    var next = LoadLcluster(nid, inode, headerOffset, advise, lclusterBits, totalIdx, lcn + 1, out _, out _, out _);
                    if (next.Type != LclusterTypeNonHead)
                    {
                        // 下一个条目是 PLAIN/HEAD 而非 CBLKCNT：单块 pcluster。
                        // The next entry is PLAIN/HEAD rather than CBLKCNT: a one-block pcluster.
                        compressedBlocks = 1;
                    }
                    else
                    {
                        // 内核要求首个 NONHEAD 的 delta[0] == 1（带 CBLKCNT 的条目在解码时
                        // 已把 delta[0] 归一化为 1 并将压缩块数放入 CompressedBlocks）；delta[0]
                        // 非 1 才是损坏。对照 erofs-utils z_erofs_get_extent_compressedlen：
                        // "if (m->delta[0] != 1) return -EFSCORRUPTED"。
                        // The kernel requires delta[0] == 1 on the first NONHEAD (a CBLKCNT entry
                        // is normalised to delta[0] == 1 at decode time with the block count in
                        // CompressedBlocks); anything but 1 is corrupt. Cf. erofs-utils
                        // z_erofs_get_extent_compressedlen: "if (m->delta[0] != 1) ... corrupt".
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

                // LZ4_0PADDING / 压缩配置特性：压缩数据前可能存在 0 前缀（mkfs 写入时
                // 从 dst-padding 起写），erofs-utils 解压前经 z_erofs_fixup_insize 跳过前导 0。
                // Compressed data may carry a leading-zero prefix (0-padding feature: mkfs writes
                // from dst - padding); erofs-utils skips it via z_erofs_fixup_insize before
                // decompression.
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
    /// 从 packed inode（fragment 特性）读取片段数据：packed_nid 指向特殊 packed inode，
    /// 其数据按普通文件映射（flat 主体 + 可能的 FLAT_INLINE 尾部内联），fragmentoff 为片段
    /// 在其中的逻辑字节偏移。严格对照内核 <c>fs/erofs/data.c</c> 对 packed inode 的处理：
    /// packed inode 是一个普通 flat/inline 文件，fragment 只是其数据区间。
    /// <para>Reads fragment data from the packed inode (fragments feature): s_packed_nid points to
    /// the special packed inode whose data is mapped like an ordinary file (flat body plus an
    /// optional FLAT_INLINE tail), and fragmentoff is the logical byte offset of the fragment
    /// within it. Mirrors the kernel's <c>fs/erofs/data.c</c> handling of the packed inode: it is
    /// an ordinary flat/inline file and a fragment is just one of its data ranges.</para>
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

        // packed inode 数据区映射：flat 布局（含 FLAT_INLINE）下，前 floor(size/blk)*blk 字节
        // 连续存放于 i_u 起始块，余数尾部内联在 inode + xattr 之后；纯 inline（i_u = 空洞
        // 哨兵）时全部数据都在 inode 之后。fragment 区间可能完全落在 flat 段、完全落在
        // inline 段，或跨两者，需分段复制。
        // The packed inode data mapping: under the flat layouts (incl. FLAT_INLINE) the first
        // floor(size/blk)*blk bytes live contiguously from the i_u block and the remainder is
        // inlined after the inode + xattrs; under pure inline (i_u = hole sentinel) all data sits
        // after the inode. A fragment range may fall entirely in the flat section, entirely in the
        // inline section, or straddle both, so it is copied in segments.
        var packedSize = (long)packed.Size;
        if (fragmentOff >= packedSize || length > packedSize - fragmentOff)
        {
            throw new InvalidDataException(
                $"Packed fragment range [{fragmentOff}..{fragmentOff + length}) exceeds the packed inode size {packedSize}.");
        }

        var inlineAll = packed.DataLayout == ErofsInode.DataLayoutInline &&
                        packed.RawBlockAddress == uint.MaxValue; // i_u = EROFS_NULL_ADDR
        if (packed.DataLayout == ErofsInode.DataLayoutInline && !inlineAll)
        {
            // FLAT_INLINE（tail-packing）：主体 flat 块 + 内联尾。
            // FLAT_INLINE (tail-packing): flat body plus an inlined tail.
            var flatLength = packedSize / _blockSize * _blockSize;
            WritePackedRange(fragmentOff, length, flatLength, packed, packedNid, inlineStartIsFlat: false, output);
            return;
        }

        if (packed.DataLayout == ErofsInode.DataLayoutInline)
        {
            // 纯 inline：全部数据在 inode 之后。Pure inline: everything after the inode.
            var start = _superblock.GetInodeOffset(packedNid) + packed.InlineDataOffset + fragmentOff;
            CheckRange(start, length, "Packed fragment data");
            CopyFromSource(output, start, length);
            return;
        }

        if (packed.DataLayout != ErofsInode.DataLayoutFlat)
        {
            throw new InvalidDataException($"Unsupported packed inode layout {packed.DataLayout} for nid {packedNid}.");
        }

        // 纯 flat：全部数据连续存放。Pure flat: all data is contiguous.
        WritePackedRange(fragmentOff, length, packedSize, packed, packedNid, inlineStartIsFlat: true, output);
    }

    /// <summary>
    /// 按 flat/inline 分段映射 packed inode 的 [<paramref name="offset"/>,
    /// <paramref name="offset"/> + <paramref name="length"/>) 区间并复制到输出。
    /// <para>Maps and copies the range [<paramref name="offset"/>,
    /// <paramref name="offset"/> + <paramref name="length"/>) of the packed inode across its flat
    /// and inline sections.</para>
    /// </summary>
    /// <param name="inlineStartIsFlat">纯 flat（无内联尾）时为 true，inline 起点即 flat 终点。
    /// True for pure-flat (no inlined tail): the inline start equals the flat end.</param>
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

        // 内联段（FLAT_INLINE 尾或纯 inline 整体）。Inline section (FLAT_INLINE tail or the whole
        // pure-inline file). The inline section starts at InlineDataOffset; for FLAT_INLINE only the
        // data beyond flatLength is stored inline, so the in-file offset must be rebased.
        var inlineOffset = packed.InlineDataOffset;
        var inlineStart = _superblock.GetInodeOffset(packedNid) + inlineOffset;
        if (!inlineStartIsFlat)
        {
            // FLAT_INLINE：inline 起点对应文件偏移 flatLength。FLAT_INLINE: the inline section
            // corresponds to file offset flatLength.
            if (pos < flatLength)
            {
                throw new InvalidDataException("Packed fragment straddles the flat/inline boundary unexpectedly.");
            }

            inlineStart += pos - flatLength;
        }
        else
        {
            // 纯 flat 且调用方已保证无越界（flatLength == packed size）。Pure flat with the caller
            // having verified bounds (flatLength == packed size); defensive check only.
            if (pos >= flatLength)
            {
                throw new InvalidDataException("Packed fragment extends beyond a pure-flat packed inode.");
            }
        }

        CheckRange(inlineStart, remaining, "Packed fragment data");
        CopyFromSource(output, inlineStart, remaining);
    }
}