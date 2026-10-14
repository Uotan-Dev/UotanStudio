using System.Buffers.Binary;
using System.Numerics;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;
using UotanStudio.Hash.Digests;
using ErofsInode = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsInode;

namespace UotanStudio.Firmware.FileSystem.Erofs.Builder;

public sealed partial class ErofsBuilder
{
    /// <summary>
    /// Builds the compression configuration records that follow the superblock, one per codec the build may
    /// encode with. The records exist only while the feature declaration advertises them, so a preserved
    /// rebuild of a pre-5.13 source keeps the compact slot-free layout, and an uncompressed build never
    /// writes them.
    /// <para>构造紧随超级块的压缩配置记录，本次构建每个可能使用的编解码器一条。仅当特性声明 advertise 这些记录时
    /// 才写出，因此对 pre-5.13 源镜像的保留重建维持无槽位的紧凑布局，未压缩的构建则一律不写。</para>
    /// </summary>
    /// <param name="options">Validated build options carrying the declaration.
    /// <para>携带声明的经校验构建选项。</para></param>
    /// <returns>The concatenated record bytes, or an empty array when none are emitted.
    /// <para>串联后的记录字节；不写出时为空数组。</para></returns>
    private static byte[] BuildCompressionConfiguration(ErofsOptions options)
    {
        if (!options.Compress || !DeclaredCompressionConfigs(options)) return [];
        if (options.SourceCompressionOptions is { } codecs)
        {
            using var output = new MemoryStream();
            foreach (var codec in codecs.OrderBy(pair => pair.Key))
                output.Write(BuildSingleCompressionConfiguration(codec.Value));
            return output.ToArray();
        }
        return BuildSingleCompressionConfiguration(options);
    }

    private static byte[] BuildSingleCompressionConfiguration(ErofsOptions options)
    {

        var length = options.Algorithm switch
        {
            ErofsCompressionAlgorithm.Lz4 or ErofsCompressionAlgorithm.Lzma => 14,
            ErofsCompressionAlgorithm.Deflate or ErofsCompressionAlgorithm.Zstd => 6,
            _ => throw new ArgumentOutOfRangeException(nameof(options.Algorithm))
        };
        var configuration = new byte[length + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(configuration, (ushort)length);

        switch (options.Algorithm)
        {
            case ErofsCompressionAlgorithm.Lz4:
                BinaryPrimitives.WriteUInt16LittleEndian(configuration.AsSpan(2),
                    options.Lz4MaxDistance == 0 ? ErofsCompression.Lz4DistanceMax : options.Lz4MaxDistance);
                BinaryPrimitives.WriteUInt16LittleEndian(configuration.AsSpan(4), checked((ushort)(options.MaxPclusterSize / options.BlockSize)));
                break;
            case ErofsCompressionAlgorithm.Lzma:
                BinaryPrimitives.WriteUInt32LittleEndian(configuration.AsSpan(2), (uint)options.DictionarySize);
                break;
            case ErofsCompressionAlgorithm.Deflate:
                configuration[2] = (byte)(options.WindowBits == 0 ? 15 : options.WindowBits);
                break;
            case ErofsCompressionAlgorithm.Zstd:
                configuration[3] = (byte)((options.WindowBits == 0 ? 20 : options.WindowBits) - 10);
                break;
        }
        return configuration;
    }

    /// <summary>
    /// Plans pclusters for one payload with the cluster-restarting planner. A candidate is accepted only when the
    /// emitted index can name its physical length: without a declared big-pcluster feature every pcluster is
    /// pinned to exactly one filesystem block, so a frame larger than one block would be read back truncated;
    /// with the feature the block count travels in the first non-head row, so a frame larger than one block has
    /// to be given a second logical cluster to carry that row. Non-big candidates are additionally trimmed to
    /// whole logical clusters, which is the granularity that layout has always used.
    /// The search reuses one scratch buffer for every probe, so the accepted frame is copied to a second buffer as
    /// soon as a probe is accepted: the probe that ends the search is usually a longer span whose encoding starts
    /// with the accepted frame's bytes but does not stop where they stop, and staging that prefix would credit the
    /// index with <c>acceptedLen</c> bytes while the stored frame decodes to a different span, which a conformant
    /// reader reports as <c>failed to full decompress</c>.
    /// <para>以逐簇重启的规划器为单个负载规划 pcluster。只有在已发出的索引能够表明其物理长度时才接受候选：
    /// 未声明 big pcluster 时每个 pcluster 被钉死为一个文件系统块，超过一块的帧读回时必被截断；声明该特性时
    /// 块数写在首个非头簇行里，因此超过一块的帧必须有第二个逻辑簇来承载该行。非 big 候选还会被裁剪为整数个
    /// 逻辑簇，这也是该布局一直使用的粒度。
    /// 搜索过程复用同一块暂存缓冲，因此候选一旦被接受就立刻把该帧复制到第二块缓冲：结束搜索的那次探测通常是
    /// 更长的跨度，其编码以被接受帧的字节开头却不会在同样的位置收尾，若把这段前缀写入暂存文件，索引会记给
    /// 该帧 <c>acceptedLen</c> 字节，而盘上帧却解码成另一个跨度，合规读取方会报
    /// <c>failed to full decompress</c>。</para>
    /// </summary>
    private static ErofsPclusterPlan[]? TryPlanCompressed(ErofsInode node, int blockSize, ErofsOptions options,
        Core.CompressionPayloadStaging staging)
    {
        if (options.OptimizeCompression && options.Algorithm == ErofsCompressionAlgorithm.Lz4)
            return TryPlanDenseCompressed(node, blockSize, options, staging);
        using var fs = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var clusterSize = node.LogicalClusterSize;
        if (options.Algorithm == ErofsCompressionAlgorithm.Lz4 && options.Lz4MaxDistance != 0
            && options.Lz4MaxDistance < clusterSize) return null;
        var bigPcluster = DeclaredBigPcluster(options);
        var frameLimit = bigPcluster
            ? Math.Min(options.MaxPclusterSize, ErofsCompression.MaxPclusterSize) - 8
            : blockSize;
        var totalClusters = CeilDiv((long)node.Size, clusterSize);
        var raw = new byte[clusterSize * 64];
        var compressed = new byte[ErofsCompression.MaximumOutputSize((ErofsAlgorithm)options.Algorithm, raw.Length)];
        var accepted = new byte[Math.Clamp(frameLimit, 0, compressed.Length)];
        var plans = new List<ErofsPclusterPlan>();
        var pos = 0L;
        var clusterIndex = 0L;
        while (clusterIndex < totalClusters)
        {
            options.CancellationToken.ThrowIfCancellationRequested();

            var acceptedLen = 0;
            var acceptedOut = 0;
            var buffered = 0;
            var maxTake = (int)Math.Min(64, totalClusters - clusterIndex);
            if (options.Algorithm == ErofsCompressionAlgorithm.Lz4
                && options.Lz4MaxDistance is > 0 and < ErofsCompression.Lz4DistanceMax)
                maxTake = (int)Math.Max(1, Math.Min(maxTake, options.Lz4MaxDistance / clusterSize));

            bool TryTake(int take, out int candidateLen, out int outLen)
            {
                candidateLen = (int)Math.Min((long)node.Size - pos, (long)take * clusterSize);
                if (!bigPcluster)
                {
                    var tail = candidateLen % clusterSize;
                    if (tail != 0) candidateLen -= tail;
                }
                if (candidateLen <= 0)
                {
                    outLen = 0;
                    return false;
                }
                if (candidateLen > buffered)
                {
                    fs.Position = pos + buffered;
                    fs.ReadExactly(raw, buffered, candidateLen - buffered);
                    buffered = candidateLen;
                }

                outLen = ErofsCompression.AlgorithmCompress(
                    options, raw, 0, candidateLen, compressed, 0, compressed.Length);
                if (outLen <= 0 || outLen > frameLimit)
                {
                    return false;
                }

                if (outLen > blockSize && pos % clusterSize + candidateLen <= clusterSize)
                {
                    return false;
                }

                compressed.AsSpan(0, outLen).CopyTo(accepted);
                return true;
            }

            var lo = 0;
            var hi = maxTake + 1;
            for (var probe = 1; probe < hi;)
            {
                if (!TryTake(probe, out var probeLen, out var probeOut))
                {
                    if (probe == 1 && bigPcluster && probeOut > blockSize)
                    {
                        lo = probe;
                        probe = 2;
                        continue;
                    }

                    hi = probe;
                    break;
                }

                lo = probe;
                acceptedLen = probeLen;
                acceptedOut = probeOut;
                probe = probe == 1 ? 2 : probe * 2;
            }

            while (lo + 1 < hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (TryTake(mid, out var midLen, out var midOut))
                {
                    lo = mid;
                    acceptedLen = midLen;
                    acceptedOut = midOut;
                }
                else
                {
                    hi = mid;
                }
            }

            if (acceptedLen <= 0 || acceptedOut <= 0)
            {
                if (clusterSize != blockSize) return null;
                var length = (int)Math.Min((long)node.Size - pos, clusterSize);
                plans.Add(new ErofsPclusterPlan { SourceOffset = pos, SourceLength = length, CompressedLength = length, Plain = true });
                pos += length;
                clusterIndex++;
                continue;
            }

            plans.Add(new ErofsPclusterPlan
            {
                SourceOffset = pos,
                SourceLength = acceptedLen,
                CompressedLength = acceptedOut,
                StagedOffset = staging.Append(accepted.AsSpan(0, acceptedOut))
            });
            pos += acceptedLen;
            clusterIndex += CeilDiv(acceptedLen, clusterSize);
        }

        if (plans.Sum(p => CeilDiv(p.CompressedLength, blockSize)) >= totalClusters)
        {
            return null;
        }

        return [.. plans];
    }

    /// <summary>
    /// Writes each planned pcluster: the encoding accepted by either planner during the planning pass is
    /// copied from the staging file, and only plain clusters re-read the source, so no accepted pcluster is
    /// compressed twice. A plan without a staged offset stays supported by re-reading and re-compressing
    /// the source, which has to reproduce the planned length exactly. Each compressed frame is stored
    /// right-aligned inside its pcluster block region with leading zero padding: the index carries only
    /// <c>di_blkaddr</c> and a block count, so a reader takes the whole padded region as the input length
    /// and removes the padding by skipping leading zero bytes. Padding the tail instead would hand the
    /// decoder trailing zero bytes that it reads as further LZ4 sequences, so the frame would fail with
    /// <c>failed to full decompress</c> even though the index and the frame agree on the logical span.
    /// <para>写出每个规划的 pcluster：任一规划器在规划阶段接受的编码都从暂存文件复制，只有原样簇会
    /// 重读源文件，因此被接受的 pcluster 不会被压缩第二次。未记录暂存偏移的规划仍受支持：重读源文件
    /// 并重新压缩，其结果长度必须与规划值完全一致。每个压缩帧都以首部零填充的方式右对齐存放于其
    /// pcluster 块区间：索引只记录 <c>di_blkaddr</c> 与块数，因此读取方会把整段含填充的区域当作输入长度，
    /// 再靠前导零字节数扣除填充。若改为尾部填充，解码器会把填充零字节当作后续的 LZ4 序列读取，
    /// 即使索引与帧对逻辑长度的认定一致，也会以 <c>failed to full decompress</c> 失败。</para>
    /// </summary>
    private static void WriteCompressedData(Stream output, ErofsInode node, int blockSize, ErofsOptions options,
        Core.CompressionPayloadStaging staging)
    {
        using var fs = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var raw = new byte[node.Pclusters!.Max(p => p.SourceLength)];
        var compressed = new byte[ErofsCompression.MaximumOutputSize((ErofsAlgorithm)options.Algorithm, raw.Length)];
        foreach (var p in node.Pclusters!)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            if (p.Fragment)
            {
                continue;
            }

            if (p.Plain)
            {
                fs.Position = p.SourceOffset;
                fs.ReadExactly(raw, 0, p.SourceLength);
                var offset = checked((long)p.BlockAddress * blockSize);
                if (p.InterlacedOffset > 0)
                {
                    WriteInterlacedFrame(output, raw, p, offset, blockSize);
                    continue;
                }

                WriteAt(output, offset, raw, p.SourceLength);
                WriteZeroes(output, offset + p.SourceLength, blockSize - p.SourceLength);
                continue;
            }

            int outLen;
            if (p.StagedOffset >= 0)
            {
                outLen = p.CompressedLength;
                staging.Read(p.StagedOffset, compressed.AsSpan(0, outLen));
            }
            else
            {
                fs.Position = p.SourceOffset;
                fs.ReadExactly(raw, 0, p.SourceLength);
                outLen = ErofsCompression.AlgorithmCompress(
                    options, raw, 0, p.SourceLength, compressed, 0, compressed.Length);
                if (outLen != p.CompressedLength)
                {
                    throw new FileSystemCorruptedException(
                        $"Compressed length mismatch for '{node.FullPath}': {outLen} != {p.CompressedLength}.");
                }
            }

            if (node.InlineCompressedTail && ReferenceEquals(p, node.Pclusters![^1]))
            {
                var tailStart = node.MetaOffset + GetInodeRecordSize(node, blockSize) - outLen;
                WriteAt(output, tailStart, compressed, outLen);
                continue;
            }

            var blockBytes = CeilDiv(p.CompressedLength, blockSize) * blockSize;
            var start = checked(p.BlockAddress * blockSize);
            if (outLen < blockBytes)
            {
                WriteZeroes(output, start, blockBytes - outLen);
            }

            WriteAt(output, start + blockBytes - outLen, compressed, outLen);
        }
    }

    /// <summary>
    /// Reports whether a node's planned index form is the variable-size extent array. The test reads the
    /// data layout first, because bit 0 of the advise word means <c>EXTENTS</c> only under the compressed
    /// layout and means <c>COMPACTED_2B</c> under the compact-index layout
    /// (<c>include/erofs_fs.h</c> gives both the same value <c>0x0001</c>).
    /// <para>判断节点的已规划索引形态是否为可变长度 extent 数组。该判断先读数据布局，因为 advise 字的第 0 位
    /// 只在压缩布局下表示 <c>EXTENTS</c>，在紧凑索引布局下表示 <c>COMPACTED_2B</c>
    /// （<c>include/erofs_fs.h</c> 给两者同一个值 <c>0x0001</c>）。</para>
    /// </summary>
    /// <param name="node">Node whose planned form is tested. <para>待判定已规划形态的节点。</para></param>
    /// <returns><see langword="true"/> only when the layout is compressed and the extent flag is raised.
    /// <para>仅在布局为压缩且 extent 标志置起时为 <see langword="true"/>。</para></returns>
    private static bool UsesExtentIndexes(ErofsInode node)
        => node.DataLayout == ErofsDataLayout.Compressed
           && (node.ZAdvise & ZErofsMapHeader.AdviseExtents) != 0;

    /// <summary>
    /// Returns the physical length an extent record stores for one pcluster: the padded physical length, not
    /// the exact encoded length, because every frame is right-aligned inside its block-rounded region with
    /// leading zero padding and the reader removes that padding from the recorded length. Recording the
    /// unpadded length instead would make a reader fetch the padding and truncate the frame.
    /// <para>返回某 pcluster 在 extent 记录中存放的物理长度：记录的是补齐后的物理长度而非精确编码长度，每个帧
    /// 都以首部零填充右对齐存放于其按块取整的区间，读取方再从记录长度中扣除该填充。若记录未补齐的长度，
    /// 读取方会取到填充并把帧截断。</para>
    /// </summary>
    /// <param name="plan">Planned pcluster of the extent. <para>该 extent 的已规划 pcluster。</para></param>
    /// <param name="blockSize">Image block size in bytes. <para>镜像块大小（字节）。</para></param>
    /// <returns>The block-rounded length for an encoded frame, or the raw length for a plain one.
    /// <para>编码帧为按块取整的长度，原样帧为原始长度。</para></returns>
    private static int ExtentPhysicalLength(ErofsPclusterPlan plan, int blockSize)
        => plan.Plain
            ? plan.CompressedLength
            : checked((int)(CeilDiv(plan.CompressedLength, blockSize) * blockSize));

    /// <summary>
    /// Reports whether any planned pcluster of a node makes an extent chain non-contiguous: a physical start
    /// or a recorded physical length that is not a whole number of blocks. Because the writer pads every
    /// encoded frame up to its block-rounded region and assigns block-aligned starts, this is false for a
    /// sequentially assigned node and the extent form therefore keeps the four-byte accumulate-only form
    /// whenever its logical cluster size survives.
    /// <para>判断节点的任一已规划 pcluster 是否使 extent 链不再连续：物理起点或记录的物理长度不是整数块。
    /// 由于写入方把每个编码帧补齐到其按块取整的区间，且起点按块对齐，顺序分配的节点该值为假，
    /// 因此只要逻辑簇大小成立，extent 形态就保持四字节纯累积形态。</para>
    /// </summary>
    /// <param name="pclusters">Planned pclusters of one node, in logical order.
    /// <para>单个节点按逻辑顺序排列的已规划 pcluster。</para></param>
    /// <param name="blockSize">Image block size in bytes. <para>镜像块大小（字节）。</para></param>
    /// <returns><see langword="true"/> when a recorded physical length is not a whole number of blocks.
    /// <para>某个记录的物理长度不是整数块时为 <see langword="true"/>。</para></returns>
    private static bool ExtentDataUnaligned(ErofsPclusterPlan[] pclusters, int blockSize)
    {
        foreach (var plan in pclusters)
        {
            if ((ExtentPhysicalLength(plan, blockSize) & (blockSize - 1)) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Selects the extent record size: thirty-two bytes for a file past four gibibytes, sixteen otherwise,
    /// four when the accumulated physical address stayed adjacent across the whole chain, and eight when it
    /// did not but every physical address fits <c>uint</c>. A single extent whose decompressed length
    /// differs from the chain-wide logical cluster size collapses the logical cluster size to zero unless it
    /// is the final, shorter extent, which leaves only the sixteen or thirty-two byte forms.
    /// <para>选取 extent 记录尺寸：文件超过四吉比字节取三十二，否则取十六；累积物理地址在整条链上保持
    /// 相邻时取四；不相邻但每个物理地址都能装进 <c>uint</c> 时取八。某条 extent 的解压长度与全链逻辑簇大小
    /// 不同时逻辑簇大小归零，除非它是末尾那条更短的 extent，此时只剩十六或三十二两种形态。</para>
    /// </summary>
    /// <param name="pclusters">Planned pclusters of one node, in logical order.
    /// <para>单个节点按逻辑顺序排列的已规划 pcluster。</para></param>
    /// <param name="size">File size in bytes (<c>i_size</c>). <para>文件字节大小（<c>i_size</c>）。</para></param>
    /// <param name="blockSize">Image block size in bytes. <para>镜像块大小（字节）。</para></param>
    /// <param name="physicalAddressAbove32Bits">Whether any extent's physical byte offset exceeds
    /// <c>uint</c>, which forbids the eight-byte form because it stores only the low half.
    /// <para>是否存在 extent 的物理字节偏移超过 <c>uint</c>；这禁止八字节形态，因为它只存放低半部分。</para></param>
    /// <returns>The record size in bytes and the chain-wide logical cluster shift, which the short forms
    /// store in <c>h_clusterbits</c> so a reader can derive the record count from the file size.
    /// <para>记录字节数，以及全链逻辑簇位数；短形态把它存进 <c>h_clusterbits</c>，使读取方能由文件大小推出记录数量。</para></returns>
    private static (int RecordSize, int ClusterBits) SelectExtentGeometry(ErofsPclusterPlan[] pclusters, ulong size,
        int blockSize, bool physicalAddressAbove32Bits)
    {
        var clusterBits = Math.Max(CeilLog2(pclusters[0].SourceLength), BitOperations.Log2((uint)blockSize));
        for (var i = 0; i < pclusters.Length; i++)
        {
            if (pclusters[i].SourceLength != 1L << clusterBits
                && (i != pclusters.Length - 1 || pclusters[i].SourceLength > 1L << clusterBits))
            {
                clusterBits = 0;
            }
        }

        var chainIntact = true;
        for (var i = 0; i < pclusters.Length - 1; i++)
        {
            if ((ExtentPhysicalLength(pclusters[i], blockSize) & (blockSize - 1)) != 0)
            {
                chainIntact = false;
                break;
            }
        }

        var recordSize = size > uint.MaxValue ? 32 : 16;
        if (clusterBits != 0)
        {
            if (chainIntact)
            {
                recordSize = 4;
            }
            else if (recordSize <= 16 && !physicalAddressAbove32Bits)
            {
                recordSize = 8;
            }
        }

        return (recordSize, clusterBits);
    }

    /// <summary>
    /// Returns the smallest exponent whose power of two reaches a length, i.e. <c>ilog2(length - 1) + 1</c>,
    /// with zero for lengths at or below one where that expression is undefined.
    /// <para>返回使 2 的幂不小于某长度的最小指数，即 <c>ilog2(length - 1) + 1</c>；长度不大于一时该表达式
    /// 无定义，此处取零。</para>
    /// </summary>
    /// <param name="length">Non-negative length to round up. <para>待向上取整的非负长度。</para></param>
    /// <returns>The exponent, or zero for a length at or below one. <para>该指数；长度不大于一时为零。</para></returns>
    private static int CeilLog2(long length)
    {
        var bits = 0;
        var value = length - 1;
        while (value > 0)
        {
            bits++;
            value >>= 1;
        }

        return bits;
    }

    /// <summary>
    /// Writes a compressed-full inode's index as the variable-size extent array the extent flag licenses:
    /// an eight-byte map header whose remaining four bytes hold the extent count in the long forms or the
    /// logical cluster size in the short ones, then the record array at
    /// <see cref="ZErofsMapHeader.ExtentAreaBase"/>. The extent form stores no algorithm type in the map header
    /// at all: each record names its codec through the format field of its encoded length.
    /// <para>把 compressed-full inode 的索引写成 extent 标志许可的可变长度数组：一个八字节映射头，其剩余四字节
    /// 在长形态下存放 extent 数量、在短形态下存放逻辑簇大小，随后是位于
    /// <see cref="ZErofsMapHeader.ExtentAreaBase"/> 的记录数组。extent 形态完全不在映射头里存放算法类型：
    /// 每条记录经其编码长度的 format 字段指名编解码器。</para>
    /// </summary>
    /// <param name="output">Destination stream in image coordinates.
    /// <para>镜像坐标下的目标流。</para></param>
    /// <param name="indexOffset">Absolute byte offset of the eight-byte map header.
    /// <para>八字节映射头的绝对字节偏移。</para></param>
    /// <param name="node">Compressed node whose planned form is the extent array.
    /// <para>已规划形态为 extent 数组的压缩节点。</para></param>
    /// <param name="blockSize">Image block size in bytes. <para>镜像块大小（字节）。</para></param>
    /// <param name="options">Compression options of this node, whose algorithm names each record's format.
    /// <para>该节点的压缩选项，其算法为每条记录指名 format。</para></param>
    /// <exception cref="FileSystemCorruptedException">The planned record size disagrees with the geometry
    /// the assigned physical addresses imply, or a record cannot express its address.
    /// <para>已规划记录尺寸与已分配物理地址推出的几何不一致，或某条记录无法表达其地址。</para></exception>
    /// <exception cref="FileSystemUnsupportedFeatureException">The node carries HEAD2 secondary-algorithm
    /// rows, which the extent form cannot express. <para>节点带有 HEAD2 第二算法行，而 extent 形态无法表达。</para></exception>
    private static void WriteExtentIndexes(Stream output, long indexOffset, ErofsInode node, int blockSize,
        ErofsOptions options)
    {
        if (node.SecondaryAlgorithm is not null)
        {
            throw new FileSystemUnsupportedFeatureException(
                $"EROFS extent indexes cannot express the secondary algorithm of '{node.FullPath}'.");
        }

        var pclusters = node.Pclusters!;
        var physicalAddressAbove32Bits = false;
        foreach (var plan in pclusters)
        {
            if (checked((long)plan.BlockAddress * blockSize) > uint.MaxValue)
            {
                physicalAddressAbove32Bits = true;
                break;
            }
        }

        var (recordSize, chainClusterBits) = SelectExtentGeometry(pclusters, node.Size, blockSize,
            physicalAddressAbove32Bits);
        var plannedRecordSize = ZErofsMapHeader.ExtentRecordSize(node.ZAdvise);
        if (recordSize != plannedRecordSize || node.ZExtents != (ulong)pclusters.Length)
        {
            throw new FileSystemCorruptedException(
                $"EROFS extent geometry of '{node.FullPath}' changed after the layout pass: planned record "
                + $"size {plannedRecordSize} with {node.ZExtents} extents, assigned addresses imply record "
                + $"size {recordSize} with {pclusters.Length} extents.");
        }

        var header = new byte[ZErofsMapHeader.Size];
        var advise = ZErofsMapHeader.ExtentAdvise(recordSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), advise);
        if (recordSize > ZErofsMapHeader.ExtentPrefixBytes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(ulong)pclusters.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)((ulong)pclusters.Length >> 32));
        }
        else
        {
            var storedClusterBits = chainClusterBits - BitOperations.Log2((uint)blockSize);
            if (storedClusterBits is < 0 or > ZErofsMapHeader.LclusterBitsMask)
            {
                throw new FileSystemUnsupportedFeatureException(
                    $"EROFS extent indexes of '{node.FullPath}' cannot store logical cluster bits {storedClusterBits} "
                    + "in the four-bit h_clusterbits field.");
            }

            header[7] = (byte)storedClusterBits;
        }

        WriteAt(output, indexOffset, header, header.Length);

        var cursor = ZErofsMapHeader.ExtentAreaBase(indexOffset, recordSize);
        var physical = checked((long)pclusters[0].BlockAddress * blockSize);
        var logical = 0L;
        if (recordSize <= 4)
        {
            var initial = new byte[ZErofsMapHeader.ExtentPrefixBytes];
            BinaryPrimitives.WriteUInt64LittleEndian(initial, (ulong)physical);
            WriteAt(output, cursor, initial, initial.Length);
            cursor += initial.Length;
        }

        var entries = new byte[Math.Max(1, recordSize) * 256];
        var pending = 0;
        foreach (var plan in pclusters)
        {
            var physicalLength = ExtentPhysicalLength(plan, blockSize);
            var encoded = plan.Plain
                ? (uint)physicalLength
                : (uint)physicalLength | (((uint)options.Algorithm + 1) << ZErofsMapHeader.ExtentPlenFmtBit);
            var recordOffset = pending * recordSize;
            BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan(recordOffset), encoded);
            if (recordSize >= 8)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan(recordOffset + 4), (uint)physical);
            }

            if (recordSize >= 16)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan(recordOffset + 8), (uint)(physical >> 32));
                BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan(recordOffset + 12), (uint)logical);
            }

            if (recordSize >= 32)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan(recordOffset + 16), (uint)(logical >> 32));
            }

            pending++;
            logical += plan.SourceLength;
            physical += encoded & ZErofsMapHeader.ExtentPlenMask;

            if (pending * recordSize == entries.Length)
            {
                WriteAt(output, cursor, entries, entries.Length);
                cursor += entries.Length;
                pending = 0;
            }
        }

        if (pending > 0)
        {
            WriteAt(output, cursor, entries, pending * recordSize);
        }
    }

    private static void WriteCompressedIndexes(Stream output, long indexOffset, ErofsInode node, int blockSize, ErofsOptions options)
    {
        if (UsesExtentIndexes(node))
        {

            WriteExtentIndexes(output, indexOffset, node, blockSize, options);
            return;
        }

        var bigPcluster = node.BigPcluster;
        var advise = bigPcluster ? ZErofsMapHeader.AdviseBigPcluster1 : (ushort)0;
        if (bigPcluster && node.CompactedIndexes)
        {
            advise |= ZErofsMapHeader.AdviseBigPcluster2;
        }

        if (node.Compacted2B)
        {

            advise |= ZErofsMapHeader.AdviseCompacted2B;
        }

        var header = new byte[ZErofsMapHeader.Size];
        if (node.InlineCompressedTail)
        {
            advise |= ZErofsMapHeader.AdviseInlinePcluster;
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), checked((ushort)node.Pclusters![^1].CompressedLength));
        }

        if (node.TailFragment)
        {
            advise |= ZErofsMapHeader.AdviseFragmentPcluster;
            BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)(ulong)node.FragmentOffset));
        }

        if (node.Pclusters!.Any(p => p.InterlacedOffset > 0))
        {
            advise |= ZErofsMapHeader.AdviseInterlacedPcluster;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), advise);

        var secondary = node.SecondaryAlgorithm;
        header[6] = (byte)((secondary is { } nibble ? (int)nibble << 4 : 0) | (int)options.Algorithm);
        header[7] = (byte)(BitOperations.Log2((uint)node.LogicalClusterSize) - BitOperations.Log2((uint)blockSize));
        WriteAt(output, indexOffset, header, header.Length);

        if (node.CompactedIndexes)
        {

            WriteCompactedIndexes4B(output, indexOffset + ZErofsMapHeader.Size, node, blockSize);
            return;
        }

        var entryOffset = indexOffset + ZErofsMapHeader.Size + 8;
        var clusters = node.ClusterCount;
        var pclusters = node.Pclusters!;
        var pclusterIndex = 0;
        var entries = new byte[ZErofsLclusterIndex.Size * 512];
        var pending = 0;
        for (var c = 0L; c < clusters; c++)
        {
            var plan = pclusters[pclusterIndex];
            var headCluster = plan.SourceOffset / node.LogicalClusterSize;
            var coveredClusters = PlanEndCluster(node, pclusterIndex) - headCluster;
            var entryStart = pending * ZErofsLclusterIndex.Size;
            if (c >= PlanEndCluster(node, pclusterIndex))
            {
                entries.AsSpan(entryStart, ZErofsLclusterIndex.Size).Clear();
                BinaryPrimitives.WriteUInt16LittleEndian(entries.AsSpan(entryStart + 2), (ushort)((long)node.Size % node.LogicalClusterSize));
            }
            else if (c == headCluster)
            {

                var headType = secondary is null
                    ? plan.Plain ? ZErofsLclusterIndex.LclusterTypePlain : ZErofsLclusterIndex.LclusterTypeHead1
                    : ZErofsLclusterIndex.LclusterTypeHead2;
                BinaryPrimitives.WriteUInt16LittleEndian(entries.AsSpan(entryStart), headType);
                BinaryPrimitives.WriteUInt16LittleEndian(entries.AsSpan(entryStart + 2), (ushort)(plan.SourceOffset % node.LogicalClusterSize));
                BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan(entryStart + 4), plan.BlockAddress);
            }
            else
            {
                var d0 = c - headCluster;

                var d1 = (ushort)Math.Min(headCluster + coveredClusters - c, (long)(ZErofsLclusterIndex.LiD0CblkCnt - 1));
                BinaryPrimitives.WriteUInt16LittleEndian(entries.AsSpan(entryStart), ZErofsLclusterIndex.LclusterTypeNonHead);
                BinaryPrimitives.WriteUInt16LittleEndian(entries.AsSpan(entryStart + 2), 0);
                if (bigPcluster && d0 == 1 && !plan.Fragment)
                {

                    var compressedBlocks = CeilDiv(plan.CompressedLength, blockSize);
                    if (compressedBlocks >= ZErofsLclusterIndex.LiD0CblkCnt)
                    {
                        throw new FileSystemCorruptedException(
                            $"Pcluster block count {compressedBlocks} exceeds the CBLKCNT limit for '{node.FullPath}'.");
                    }

                    BinaryPrimitives.WriteUInt16LittleEndian(
                        entries.AsSpan(entryStart + 4), (ushort)((uint)compressedBlocks | ZErofsLclusterIndex.LiD0CblkCnt));
                }
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        entries.AsSpan(entryStart + 4), (ushort)Math.Min(d0, (long)(ZErofsLclusterIndex.LiD0CblkCnt - 1)));
                }

                BinaryPrimitives.WriteUInt16LittleEndian(entries.AsSpan(entryStart + 6), d1);
            }

            pending++;
            if (pending * ZErofsLclusterIndex.Size == entries.Length || c == clusters - 1)
            {
                WriteAt(output, entryOffset, entries, pending * ZErofsLclusterIndex.Size);
                entryOffset += pending * ZErofsLclusterIndex.Size;
                pending = 0;
            }

            if (c == headCluster + coveredClusters - 1 && pclusterIndex + 1 < pclusters.Length)
            {
                pclusterIndex++;
            }
        }
    }

    /// <summary>
    /// Writes compact EROFS indexes: 4-byte entries in 8-byte packs, and, when the inode declares
    /// <c>Z_EROFS_ADVISE_COMPACTED_2B</c>, a middle section of 2-byte entries in 32-byte packs between a
    /// 4-byte head and a 4-byte tail.
    /// <para>写出紧凑 EROFS 索引：每条 4 字节、按 8 字节打包；当 inode 声明
    /// <c>Z_EROFS_ADVISE_COMPACTED_2B</c> 时，在 4 字节头部与 4 字节尾部之间插入按 32 字节打包的 2 字节中段。</para>
    /// </summary>
    private static void WriteCompactedIndexes4B(Stream output, long tableOffset, ErofsInode node, int blockSize)
    {
        var lclusterBits = BitOperations.Log2((uint)node.LogicalClusterSize);
        var lobits = Math.Max(lclusterBits, 12);
        var bigPcluster = node.BigPcluster;

        var clusters = node.ClusterCount;
        var pclusters = node.Pclusters!;
        var packOffset = tableOffset;
        var entryIndex = 0L;
        var pclusterIndex = 0;

        var blkaddr = (long)pclusters[0].BlockAddress - (bigPcluster ? 0 : 1);

        var dummyHead = !bigPcluster;

        var segmentEnds = new long[3];
        var segmentDestSizes = new int[3];
        var segmentVcnts = new int[3];
        var segmentCount = 0;
        if (node.Compacted2B)
        {
            var (initial, twoByte, _) = Compacted2BSplit(tableOffset, clusters);
            if (initial > 0)
            {
                segmentEnds[segmentCount] = initial;
                segmentDestSizes[segmentCount] = 4;
                segmentVcnts[segmentCount] = 2;
                segmentCount++;
            }
            if (twoByte > 0)
            {
                segmentEnds[segmentCount] = initial + twoByte;
                segmentDestSizes[segmentCount] = 2;
                segmentVcnts[segmentCount] = 16;
                segmentCount++;
            }
            if (clusters > initial + twoByte)
            {
                segmentEnds[segmentCount] = clusters;
                segmentDestSizes[segmentCount] = 4;
                segmentVcnts[segmentCount] = 2;
                segmentCount++;
            }
        }
        if (segmentCount == 0)
        {
            segmentEnds[0] = clusters;
            segmentDestSizes[0] = 4;
            segmentVcnts[0] = 2;
            segmentCount = 1;
        }

        var segmentIndex = 0;
        var destsize = segmentDestSizes[0];
        var vcnt = segmentVcnts[0];
        var segmentEnd = segmentEnds[0];

        while (entryIndex < clusters)
        {
            if (entryIndex >= segmentEnd && segmentIndex + 1 < segmentCount)
            {
                segmentIndex++;
                destsize = segmentDestSizes[segmentIndex];
                vcnt = segmentVcnts[segmentIndex];
                segmentEnd = segmentEnds[segmentIndex];
            }

            var packSize = vcnt * destsize;
            var encodebits = (packSize * 8 - 32) / vcnt;
            var pack = new byte[packSize];

            var packStartBlkaddr = blkaddr + (bigPcluster && dummyHead ? 1 : 0);
            var bits = 0;
            for (var i = 0; i < vcnt; i++)
            {
                uint offset;
                byte type;
                if (entryIndex < segmentEnd)
                {
                    var plan = pclusters[pclusterIndex];
                    var headCluster = plan.SourceOffset / node.LogicalClusterSize;
                    var coveredClusters = PlanEndCluster(node, pclusterIndex) - headCluster;
                    if (entryIndex >= PlanEndCluster(node, pclusterIndex))
                    {
                        type = 0;
                        offset = (uint)((long)node.Size % node.LogicalClusterSize);
                        if (dummyHead) blkaddr++;
                        dummyHead = true;
                    }
                    else if (entryIndex == headCluster)
                    {

                        type = plan.Plain ? (byte)0 : (byte)1;
                        offset = (uint)(plan.SourceOffset % node.LogicalClusterSize);
                        if (dummyHead)
                        {
                            blkaddr++;
                            if (!plan.Fragment && blkaddr != plan.BlockAddress)
                            {
                                throw new FileSystemCorruptedException(
                                    $"Compacted index blkaddr {blkaddr} != planned block {plan.BlockAddress} for '{node.FullPath}'.");
                            }
                        }

                        dummyHead = true;
                    }
                    else
                    {
                        type = 2;
                        var d0 = entryIndex - headCluster;
                        if (bigPcluster && d0 == 1 && !plan.Fragment)
                        {

                            var compressedBlocks = CeilDiv(plan.CompressedLength, blockSize);
                            if (compressedBlocks >= ZErofsLclusterIndex.LiD0CblkCnt)
                            {
                                throw new FileSystemCorruptedException(
                                    $"Pcluster block count {compressedBlocks} exceeds the CBLKCNT limit for '{node.FullPath}'.");
                            }

                            offset = (uint)compressedBlocks | ZErofsLclusterIndex.LiD0CblkCnt;
                            blkaddr += compressedBlocks;
                            dummyHead = false;
                            if (pclusterIndex + 1 < pclusters.Length && blkaddr != pclusters[pclusterIndex + 1].BlockAddress)
                            {
                                throw new FileSystemCorruptedException(
                                    $"Compacted index running blkaddr {blkaddr} != planned block {pclusters[pclusterIndex + 1].BlockAddress} for '{node.FullPath}'.");
                            }
                        }
                        else if (i + 1 == vcnt)
                        {

                            var d1 = coveredClusters - d0;
                            offset = (uint)Math.Min(d1, ZErofsLclusterIndex.LiD0CblkCnt - 1);
                        }
                        else
                        {
                            offset = (uint)Math.Min(d0, (long)(ZErofsLclusterIndex.LiD0CblkCnt - 1));
                        }
                    }

                    if (entryIndex == headCluster + coveredClusters - 1 && pclusterIndex + 1 < pclusters.Length)
                    {
                        pclusterIndex++;
                    }

                    entryIndex++;
                }
                else
                {
                    type = 0;
                    offset = 0;
                }

                var v = ((uint)type << lobits) | offset;
                var bytePos = bits >> 3;
                var bitShift = bits & 7;
                var chunk = v << bitShift;
                for (var b = 0; b < 4; b++)
                {
                    if (bytePos + b < packSize - 4)
                    {
                        pack[bytePos + b] |= (byte)(chunk >> (8 * b));
                    }
                }

                bits += encodebits;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(pack.AsSpan(packSize - 4, 4), (uint)packStartBlkaddr);
            WriteAt(output, packOffset, pack, pack.Length);
            packOffset += pack.Length;
        }
    }

    /// <summary>
    /// Splits a compacted index area into its 4-byte head, 2-byte middle section, and 4-byte tail counts.
    /// The head is the number of 4-byte entries needed so that the first 2-byte pack starts on a 32-byte
    /// boundary; a head that would land exactly on that boundary is zero instead of eight.
    /// <para>把紧凑索引区拆成 4 字节头部、2 字节中段与 4 字节尾部的条目数。头部是让首个 2 字节包落在
    /// 32 字节边界上所需的 4 字节条目数；头部正好落在该边界上时取 0 而非 8。</para>
    /// </summary>
    /// <param name="indexBase">Offset of the compacted index area, i.e. the byte after the map header.
    /// <para>紧凑索引区的偏移，即映射头之后的那个字节。</para></param>
    /// <param name="clusterCount">Number of logical clusters to encode.
    /// <para>需要编码的逻辑簇数量。</para></param>
    /// <returns>The head, middle, and tail entry counts, which always sum to
    /// <paramref name="clusterCount"/>. <para>头部、中段与尾部的条目数，三者之和恒等于
    /// <paramref name="clusterCount"/>。</para></returns>
    private static (long Initial, long TwoByte, long End) Compacted2BSplit(long indexBase, long clusterCount)
    {
        var initial = (32 - indexBase % 32) / 4;
        if (initial == 32 / 4)
        {
            initial = 0;
        }
        if (initial > clusterCount)
        {
            return (0, 0, clusterCount);
        }
        var twoByte = (clusterCount - initial) / 16 * 16;
        return (initial, twoByte, clusterCount - initial - twoByte);
    }

    /// <summary>
    /// Writes one plain frame with the interlaced byte rotation the pcluster plan selected, mirroring
    /// <c>lib/compress.c</c> in erofs-utils: the stored block starts at the in-block offset the frame's
    /// logical window lands on and wraps at the block end, and the remaining bytes of the block stay zero.
    /// <para>按 pcluster 规划选定的字节旋转写出单个原样帧，与 erofs-utils 的 <c>lib/compress.c</c> 一致：
    /// 存储块从该帧逻辑窗口落入的块内偏移开始，在块尾回绕，块内其余字节保持为零。</para>
    /// </summary>
    /// <param name="output">Destination stream in image coordinates.
    /// <para>镜像坐标下的目标流。</para></param>
    /// <param name="raw">Buffer holding the frame's plain source bytes, read from the node's payload.
    /// <para>存放该帧原样源字节的缓冲区，已从节点负载读出。</para></param>
    /// <param name="p">Planned pcluster carrying the rotation in
    /// <see cref="ErofsPclusterPlan.InterlacedOffset"/>.
    /// <para>携带 <see cref="ErofsPclusterPlan.InterlacedOffset"/> 旋转量的已规划 pcluster。</para></param>
    /// <param name="offset">First byte of the frame's physical block region.
    /// <para>该帧物理块区间的首字节。</para></param>
    /// <param name="blockSize">Filesystem block size in bytes.
    /// <para>以字节计的文件系统块大小。</para></param>
    /// <exception cref="FileSystemCorruptedException">The rotation and frame length together exceed one
    /// block. <para>旋转量与帧长度之和超过一个块。</para></exception>
    private static void WriteInterlacedFrame(Stream output, byte[] raw, ErofsPclusterPlan p, long offset,
        int blockSize)
    {
        var rotation = p.InterlacedOffset;
        var rightpart = Math.Min(blockSize - rotation, p.SourceLength);
        if (rightpart < 0 || p.SourceLength > blockSize)
        {
            throw new FileSystemCorruptedException(
                $"Interlaced plain pcluster at {p.SourceOffset} does not fit its block: rotation {rotation} "
                + $"with length {p.SourceLength} exceeds {blockSize} bytes.");
        }

        var block = new byte[blockSize];
        Array.Copy(raw, 0, block, rotation, rightpart);
        if (p.SourceLength > rightpart)
        {
            Array.Copy(raw, rightpart, block, 0, p.SourceLength - rightpart);
        }

        WriteAt(output, offset, block, block.Length);
    }

    private static void WriteZeroes(Stream output, long offset, long count)
    {
        output.Position = offset;
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
    /// Writes the packed inode's fragment data area: each fragment slot's source bytes are streamed to its offset inside the packed region (unwritten gaps stay zeroed by the image's preallocation).
    /// <para>写出 packed inode 的碎片数据区：把每个碎片槽位按文件内偏移写入数据区对应位置 （槽位之间未写的空白由镜像预分配/置零保证为零）。</para>
    /// </summary>
    private static void WritePackedData(Stream output, ErofsInode packedNode, int blockSize, CancellationToken token)
    {
        var baseOffset = packedNode.FlatBlockAddr * blockSize;
        foreach (var slot in packedNode.PackedFragments!)
        {
            token.ThrowIfCancellationRequested();
            CopyFileRange(output, slot.Owner, slot.SourceOffset, slot.Length, baseOffset + slot.Offset, token);
        }
    }

    /// <summary>
    /// Whether an uncompressed regular inode and its inline payload fit within one METABOX block.
    /// <para>仅将 inode、扩展属性和内联数据整体能放入单个 METABOX 块的普通文件作为驻留记录。</para>
    /// </summary>
    private static bool IsMetaboxEligible(ErofsInode node, int blockSize)
        => !node.IsDirectory
           && !node.IsSymlink
           && !node.IsDevice
           && !node.IsPacked
           && node.Size <= uint.MaxValue
           && node.Pclusters is null
           && !node.IsFragment
           && GetInodeRecordSize(node, blockSize) <= blockSize;

    /// <summary>
    /// Assembles the metabox container body: every resident's inode record placed at its slot, followed by
    /// that record's inline xattrs and inline tail data, plus the shared xattr area at its planned offset.
    /// The body is produced as one buffer rather than written straight into the image because the container
    /// may be encoded, and an encoder needs the whole payload addressable before it plans a single pcluster.
    /// <para>装配 metabox 容器本体：把每个常驻 inode 的记录放到其槽位，紧随其内联 xattr 与内联尾部数据，并在
    /// 规划的偏移处写入共享 xattr 区。本体以单个缓冲产出而不是直接写进镜像，因为容器可能被编码，而编码器需要
    /// 在规划任何 pcluster 之前就能寻址整个负载。</para>
    /// </summary>
    /// <param name="blockSize">Filesystem block size in bytes.
    /// <para>以字节计的文件系统块大小。</para></param>
    /// <param name="now">Superblock epoch seconds every resident's record is stamped against.
    /// <para>各常驻记录所依据的超级块纪元秒。</para></param>
    /// <param name="residents">Nodes stored in the container, each with its slot already assigned.
    /// <para>存放在容器中的节点，其槽位均已分配。</para></param>
    /// <param name="bodyLength">Logical body length the layout measured.
    /// <para>布局度量出的本体逻辑长度。</para></param>
    /// <param name="sharedXattrOffset">Offset of the shared xattr area inside the body, or <c>-1</c> when the
    /// area lives in a data block instead. <para>共享 xattr 区在本体内的偏移；该区位于数据块中时为
    /// <c>-1</c>。</para></param>
    /// <param name="sharedXattrs">Encoded shared xattr records in slot order.
    /// <para>按槽位顺序排列的已编码共享 xattr 记录。</para></param>
    /// <param name="options">Validated build options the residents' records are built against.
    /// <para>构建常驻记录所依据的经校验选项。</para></param>
    /// <returns>The zero-filled body buffer with every resident and shared xattr record in place. Gaps stay
    /// zero, which is what the image preallocation used to guarantee for the slot-wise writer.
    /// <para>已填入全部常驻记录与共享 xattr 记录、其余为零的本体缓冲。空隙保持为零，这正是逐槽位写入方原先
    /// 依赖镜像预分配保证的结果。</para></returns>
    private static byte[] BuildMetaboxContainerBody(int blockSize, ulong now, List<ErofsInode> residents,
        long bodyLength, long sharedXattrOffset, List<byte[]> sharedXattrs, ErofsOptions options)
    {
        var body = new byte[checked((int)bodyLength)];
        foreach (var resident in residents)
        {
            BuildInode(resident, blockSize, now, options);
            var buffer = new byte[resident.OnDiskSize];
            resident.WriteTo(buffer);
            var slotOffset = checked((int)(resident.MetaboxSlot * ErofsSuperblock.CompactInodeSize));
            buffer.CopyTo(body, slotOffset);
            if (resident.XattrIbody.Length > 0)
            {
                resident.XattrIbody.CopyTo(body, slotOffset + resident.OnDiskSize);
            }

            if (resident.InlineData.Length > 0)
            {
                resident.InlineData.CopyTo(body, slotOffset + checked((int)resident.InlineDataOffset));
            }
        }

        if (sharedXattrOffset >= 0 && sharedXattrs.Count > 0)
        {
            var cursor = checked((int)sharedXattrOffset);
            foreach (var entry in sharedXattrs)
            {
                entry.CopyTo(body, cursor);
                cursor += checked((int)AlignUp(entry.Length, ErofsXattr.EntryAlignment));
            }
        }

        return body;
    }

    /// <summary>
    /// Writes each variable-sized METABOX inode record with its inline xattrs and tail data.
    /// <para>按分配的变长槽位写入 inode、扩展属性与内联尾部数据。</para>
    /// </summary>
    /// <param name="output">Destination stream in image coordinates.
    /// <para>镜像坐标下的目标流。</para></param>
    /// <param name="metaboxNode">Synthetic container node whose flat block address anchors the area.
    /// <para>其平坦块地址锚定该区域的合成容器节点。</para></param>
    /// <param name="blockSize">Filesystem block size in bytes.
    /// <para>以字节计的文件系统块大小。</para></param>
    /// <param name="now">Superblock epoch seconds every resident's record is stamped against.
    /// <para>各常驻记录所依据的超级块纪元秒。</para></param>
    /// <param name="residents">Nodes stored in the container, each with its slot already assigned.
    /// <para>存放在容器中的节点，其槽位均已分配。</para></param>
    /// <param name="bodyLength">Logical body length the layout measured.
    /// <para>布局度量出的本体逻辑长度。</para></param>
    /// <param name="sharedXattrOffset">Offset of the shared xattr area inside the body, or <c>-1</c> when the
    /// area is written separately. <para>共享 xattr 区在本体内的偏移；该区另行写出时为 <c>-1</c>。</para></param>
    /// <param name="sharedXattrs">Encoded shared xattr records in slot order.
    /// <para>按槽位顺序排列的已编码共享 xattr 记录。</para></param>
    /// <param name="options">Validated build options the residents' records are built against.
    /// <para>构建常驻记录所依据的经校验选项。</para></param>
    private static void WriteMetaboxContainer(Stream output, ErofsInode metaboxNode, int blockSize, ulong now,
        List<ErofsInode> residents, long bodyLength, long sharedXattrOffset, List<byte[]> sharedXattrs,
        ErofsOptions options)
    {
        var baseOffset = checked(metaboxNode.FlatBlockAddr * blockSize);
        var body = BuildMetaboxContainerBody(blockSize, now, residents, bodyLength, sharedXattrOffset,
            sharedXattrs, options);
        WriteAt(output, baseOffset, body, body.Length);
    }

    /// <summary>
    /// Writes a chunk-based inode's chunk index table, which follows the inode record and its inline xattrs
    /// at the next multiple of the entry size. Two forms exist and the inode's <c>u</c> field selects
    /// between them: a bare 32-bit block map, or the 8-byte <c>erofs_inode_chunk_index</c>
    /// (<c>startblk_hi</c>, <c>device_id</c>, <c>startblk_lo</c>) that a device id or a 48-bit start
    /// block requires. Each entry is encoded in one of three branches: a hole keeps the null address, a
    /// payload on an extra device stores its device-relative start block together with its device id, and a
    /// payload on the primary image stores the filesystem-wide address. That address is emitted verbatim
    /// from the source's already-unified device-0 form, so the stored entry is the final value.
    /// <para>写出 chunk-based inode 的 chunk 索引表，它紧随 inode 记录及其 inline xattr，起点为表项尺寸的
    /// 下一个整数倍。该表有两种形态，由 inode 的 <c>u</c> 字段选择：裸 32 位块映射，或 8 字节的
    /// <c>erofs_inode_chunk_index</c>（<c>startblk_hi</c>、<c>device_id</c>、<c>startblk_lo</c>），后者是
    /// 设备号或 48 位首块所必需的。每条表项按三种分支编码：空洞保持空地址，
    /// 位于额外设备上的负载存其设备内相对首块与设备号，位于主镜像上的负载存文件系统范围内的地址。该地址
    /// 按源已统一化的 device-0 形式逐字重输出，因此存储值就是最终值。</para>
    /// </summary>
    /// <param name="output">Destination stream in image coordinates.
    /// <para>镜像坐标下的目标流。</para></param>
    /// <param name="node">Chunk-based file node whose index was filled by the address pass.
    /// <para>索引已由地址分配阶段填充的 chunk-based 文件节点。</para></param>
    /// <exception cref="InvalidOperationException">The node carries no chunk index.
    /// <para>该节点不携带 chunk 索引。</para></exception>
    /// <exception cref="FileSystemUnsupportedFeatureException">A nonzero device id was requested while the
    /// node's format selects the 4-byte block map, which has no field for it.
    /// <para>节点格式选择 4 字节块映射时请求了非零设备号，而该形态没有承载设备号的字段。</para></exception>
    private static void WriteChunkIndexTable(Stream output, ErofsInode node)
    {
        var index = node.ChunkIndex ?? throw new InvalidOperationException("Chunk-based node has no chunk index.");
        var usesIndexes = node.UsesChunkIndexes;
        var deviceId = node.DeviceId;
        if (deviceId != 0 && !usesIndexes)
        {
            throw new FileSystemUnsupportedFeatureException(
                $"EROFS chunk-based inode '{node.FullPath}' places its payload on device {deviceId}, but only the "
                + "8-byte chunk index entries can carry a device id.");
        }

        var unit = usesIndexes ? ErofsInode.ChunkIndexEntrySize : ErofsInode.BlockMapEntrySize;
        var tableOffset = AlignUp(node.MetaOffset + node.InlineDataOffset, unit);
        var entries = new byte[checked(index.Length * unit)];
        var addrmask = node.Chunk48Bit ? ChunkIndex48BitMask : uint.MaxValue;
        var nullAddress = node.Chunk48Bit ? ChunkIndexNullAddress48 : uint.MaxValue;
        for (var i = 0; i < index.Length; i++)
        {
            var startBlock = (index[i] & addrmask) == addrmask ? nullAddress : index[i];
            if (!usesIndexes)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan(i * unit), checked((uint)startBlock));
                continue;
            }

            var slot = entries.AsSpan(i * unit, ErofsInode.ChunkIndexEntrySize);
            BinaryPrimitives.WriteUInt16LittleEndian(slot, checked((ushort)(startBlock >> 32)));
            BinaryPrimitives.WriteUInt16LittleEndian(slot[2..], checked((ushort)deviceId));
            BinaryPrimitives.WriteUInt32LittleEndian(slot[4..], checked((uint)startBlock));
        }

        WriteAt(output, tableOffset, entries, entries.Length);
    }

    private const ulong ChunkIndex48BitMask = (1UL << 48) - 1;
    private const ulong ChunkIndexNullAddress48 = ChunkIndex48BitMask;

    /// <summary>
    /// Writes a fragment inode's 8-byte z_erofs_map_header: h_fragmentoff is the byte offset inside the packed inode and h_clusterbits bit 7 (ZErofsMapHeader.FragmentInodeMask) marks a whole-file fragment.
    /// <para>写出 fragment inode 的 8 字节 z_erofs_map_header：h_fragmentoff = packed 区内字节偏移、 h_clusterbits bit7（ZErofsMapHeader.FragmentInodeMask）声明整文件 fragment。</para>
    /// </summary>
    private static void WriteFragmentHeader(Stream output, ErofsInode node)
    {
        var headerOffset = node.MetaOffset + AlignUp(node.OnDiskSize + node.XattrISize, 8);
        var header = new byte[ZErofsMapHeader.Size];
        BinaryPrimitives.WriteUInt64LittleEndian(header, checked((ulong)node.FragmentOffset) | (1UL << 63));
        WriteAt(output, headerOffset, header, header.Length);
    }

    /// <summary>
    /// Streams a whole file through SHA-256 via a digest accumulator in chunks without loading the file wholly into memory.
    /// <para>流式计算整文件 SHA-256（经摘要累加器分块读取，不整读大文件）。</para>
    /// </summary>
    private static byte[] HashFile(ErofsInode node, CancellationToken token)
    {
        using var fs = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = Sha256.CreateAccumulator();
        var buffer = new byte[1 << 20];
        var remaining = (long)node.Size;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, remaining);
            fs.ReadExactly(buffer, 0, count);
            hash.Append(buffer.AsSpan(0, count));
            remaining -= count;
        }

        return hash.GetHashAndReset();
    }

    /// <summary>
    /// Whole-file fragment offsets occupy the low 63 bits of the map header and need no alignment.
    /// </summary>
    private static long AlignFragmentStart(long cursor)
        => cursor;

    private static void CopyFileRange(Stream output, ErofsInode node, long sourceOffset, long length, long targetOffset, CancellationToken token)
    {
        if (length <= 0)
        {
            return;
        }

        using var source = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        source.Position = sourceOffset;
        var buffer = new byte[1 << 20];
        var remaining = length;
        var pos = targetOffset;
        while (remaining > 0)
        {
            token.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, remaining);
            source.ReadExactly(buffer, 0, count);
            WriteAt(output, pos, buffer, count);
            pos += count;
            remaining -= count;
        }
    }

    private static long CeilDiv(long value, long divisor) => value == 0 ? 0 : (value + divisor - 1) / divisor;

    private static long AlignUp(long value, long alignment) => (value + alignment - 1) / alignment * alignment;

    private static void ZeroImage(Stream output, long length, CancellationToken token)
    {
        output.Position = 0;
        var zeroes = new byte[1 << 16];
        while (length > 0)
        {
            token.ThrowIfCancellationRequested();
            var count = (int)Math.Min(zeroes.Length, length);
            output.Write(zeroes, 0, count);
            length -= count;
        }
    }

    private static void WriteAt(Stream output, long offset, byte[] data, int count)
    {
        output.Position = offset;
        output.Write(data, 0, count);
    }
}