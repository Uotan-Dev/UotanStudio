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
    /// <para>Reads also credit each accepted frame with the cluster that carries its end: 5.10's
    /// <c>z_erofs_map_blocks_iter</c> keeps <c>end = (lcn + 1) &lt;&lt; lclusterbits</c> for the cluster a lookup
    /// starts in and <c>z_erofs_extent_lookback</c> only rewinds the start, so <c>zdata.c</c> folds that rounded
    /// length into <c>pcl-&gt;length</c> and <c>z_erofs_lz4_decompress</c> returns <c>-EIO</c> whenever
    /// <c>ret != rq-&gt;outputsize</c>. A frame for a file whose data ends inside its final logical cluster
    /// therefore has to encode that whole cluster, so the candidate that reaches the end of file with a partial
    /// tail is compressed from a zero-filled input up to the cluster boundary. erofs-utils meets the same
    /// contract differently — it leaves the residual end offset in <c>ctx-&gt;clusterofs</c> and
    /// <c>z_erofs_fini_full_indexes</c> closes the index with a trailing row — which is only available while the
    /// row after the head stays free for a block count; padding the input keeps the emitted rows unchanged and
    /// keeps a sub-block tail inside its own frame instead of spending a whole block on it.</para>
    /// <para>读取方还会把每个被接受的帧记到承载其末端的那个簇上：5.10 的 <c>z_erofs_map_blocks_iter</c> 对查找起点的簇
    /// 保持 <c>end = (lcn + 1) &lt;&lt; lclusterbits</c>，而 <c>z_erofs_extent_lookback</c> 只回退起点，因此
    /// <c>zdata.c</c> 把取整后的长度折进 <c>pcl-&gt;length</c>，而 <c>z_erofs_lz4_decompress</c> 只要
    /// <c>ret != rq-&gt;outputsize</c> 就返回 <c>-EIO</c>。因此数据在末尾逻辑簇内结束的文件，其帧必须编码整个簇，
    /// 所以触及文件末尾且带不满簇尾巴的候选会从补零到簇边界的输入压缩。erofs-utils 以另一种方式满足同一契约——把
    /// 剩余结束偏移留在 <c>ctx-&gt;clusterofs</c> 而由 <c>z_erofs_fini_full_indexes</c> 以尾行收尾——但那只在头行之后
    /// 的那一行仍可用于块数时可行；补零输入保持已发出的行不变，并让不足一块的尾巴留在自己的帧里，而不必为它花掉
    /// 整整一个块。</para>
    /// </summary>
    /// <summary>
    /// Reports whether a frame accepted for <paramref name="candidateLength"/> bytes at
    /// <paramref name="position"/> can be closed by the index row a reader expects when a file's data ends
    /// inside its final logical cluster. A reader credits such a frame with the cluster that carries its end
    /// (5.10's <c>z_erofs_map_blocks_iter</c> keeps <c>end = (lcn + 1) &lt;&lt; lclusterbits</c> and
    /// <c>z_erofs_extent_lookback</c> only rewinds the start, so <c>zdata.c</c> demands that rounded size and
    /// <c>z_erofs_lz4_decompress</c> returns <c>-EIO</c> when the stream produces less). The row that closes the
    /// extent — the trailing row erofs-utils emits from <c>z_erofs_fini_full_indexes</c>, a plain row whose
    /// cluster offset is the residual end offset — keeps the demand equal to the bytes the frame encodes, and it
    /// is written for the last cluster of the last pcluster. It can only stand in for a non-head row of a
    /// multi-cluster pcluster, and it takes the place of the block count row a multi-block frame needs, so a
    /// two-cluster frame above one block cannot use it: such a candidate is refused here and the planner falls
    /// back to a shorter span plus a plain pcluster for the residual, which every reader copies instead of
    /// decompressing.
    /// <para>判断在 <paramref name="position"/> 处为 <paramref name="candidateLength"/> 字节接受的帧，能否由读取方在
    /// 文件数据结束于末尾逻辑簇内时所期待的索引行收尾。读取方会把这样的帧记到承载其末端的簇上（5.10 的
    /// <c>z_erofs_map_blocks_iter</c> 保持 <c>end = (lcn + 1) &lt;&lt; lclusterbits</c>，而
    /// <c>z_erofs_extent_lookback</c> 只回退起点，因此 <c>zdata.c</c> 要求的正是取整后的长度，而
    /// <c>z_erofs_lz4_decompress</c> 在流产出不足时返回 <c>-EIO</c>）。收尾该 extent 的行——erofs-utils 由
    /// <c>z_erofs_fini_full_indexes</c> 发出的尾行，即簇偏移等于剩余结束偏移的 plain 行——会让要求量等于帧实际编码的
    /// 字节数，且它为最后一个 pcluster 的最后一个簇而写。它只能替代多簇 pcluster 的非头行，并且会占掉多块帧所需的块数行，
    /// 因此跨两簇且超过一块的帧不能使用它：此类候选在此被拒绝，规划器退化为更短的跨度加一个承载剩余字节的原样
    /// pcluster，任何读取方都逐字节拷贝而不解压。</para>
    /// </summary>
    /// <param name="position">Byte offset the candidate starts at. <para>候选的起始字节偏移。</para></param>
    /// <param name="candidateLength">Candidate length in bytes. <para>候选的字节长度。</para></param>
    /// <param name="encodedLength">Compressed length of the accepted frame. <para>已接受帧的压缩长度。</para></param>
    /// <param name="clusterSize">Logical cluster size in bytes. <para>逻辑簇大小（字节）。</para></param>
    /// <param name="size">File size in bytes. <para>文件大小（字节）。</para></param>
    /// <param name="blockSize">Filesystem block size in bytes. <para>文件系统块大小（字节）。</para></param>
    /// <returns><see langword="true"/> when no closing row is due or the due row can carry the extent.
    /// <para>无需收尾行，或应付的收尾行能够承载该 extent 时为 <see langword="true"/>。</para></returns>
    private static bool AcceptsClosingIndexRow(long position, int candidateLength, int encodedLength,
        int clusterSize, long size, int blockSize)
    {
        if (size % clusterSize == 0 || position + candidateLength != size)
        {
            return true;
        }

        var clusters = CeilDiv(position + candidateLength, clusterSize) - position / clusterSize;
        if (clusters < 2)
        {
            return false;
        }

        return clusters >= 3 || CeilDiv(encodedLength, blockSize) == 1;
    }

    /// <summary>
    /// Reports whether every extent of a finished plan can be closed the way a reader needs at end of file.
    /// Both planners enforce the closing-row rule while they search, so this is the last guard over a finished
    /// plan: a frame refused by <see cref="AcceptsClosingIndexRow"/> would make the plan drop to the
    /// cluster-restarting planner, which re-derives the same rows from its own spans.
    /// <para>判断一份已完成规划中的每个 extent 都能按读取方在文件末尾所需的方式收尾。两个规划器都在搜索时执行
    /// 收尾行规则，因此这里是已完成规划的最后一道校验：被 <see cref="AcceptsClosingIndexRow"/> 拒绝的帧会让该规划
    /// 改交逐簇重启规划器，由它按自己的跨度重新推导同样的行。</para>
    /// </summary>
    /// <param name="node">Node the plan belongs to. <para>该规划所属的节点。</para></param>
    /// <param name="plans">Plans produced by the dense planner. <para>dense 规划器产出的规划。</para></param>
    /// <param name="blockSize">Filesystem block size in bytes. <para>文件系统块大小（字节）。</para></param>
    /// <returns><see langword="true"/> when no closing row is refused. <para>没有被拒绝的收尾行时为
    /// <see langword="true"/>。</para></returns>
    private static bool ClosesSafely(ErofsInode node, ErofsPclusterPlan[] plans, int blockSize)
    {
        if (plans.Length == 0)
        {
            return true;
        }

        var clusterSize = node.LogicalClusterSize;
        for (var index = 0; index < plans.Length; index++)
        {
            var plan = plans[index];
            if (plan.Plain || clusterSize <= 0)
            {
                continue;
            }

            if (!AcceptsClosingIndexRow(plan.SourceOffset, plan.SourceLength, plan.CompressedLength,
                clusterSize, (long)node.Size, blockSize))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reports whether the row at <paramref name="clusterIndex"/> is the closing row of the file's last
    /// compressed pcluster, i.e. the trailing plain row erofs-utils writes from
    /// <c>z_erofs_fini_full_indexes</c> whose cluster offset is the residual end offset. A reader only reaches
    /// that offset at end of file, so the row never resolves to a physical address; it exists so the extent
    /// before it is credited with the bytes it really encodes instead of a whole final cluster.
    /// <para>判断 <paramref name="clusterIndex"/> 处的行是否为文件最后一个压缩 pcluster 的收尾行，即 erofs-utils 由
    /// <c>z_erofs_fini_full_indexes</c> 写出、簇偏移等于剩余结束偏移的尾部 plain 行。读取方只会在文件末尾到达该偏移，
    /// 因此该行永远不会解析出物理地址；它的存在只是让之前的 extent 被记为它真正编码的字节数，而不是整个末尾簇。</para>
    /// </summary>
    /// <param name="node">Node being written. <para>正在写出的节点。</para></param>
    /// <param name="pclusters">Planned pclusters of the node. <para>该节点已规划的 pcluster。</para></param>
    /// <param name="pclusterIndex">Plan the row belongs to. <para>该行所属的规划项。</para></param>
    /// <param name="clusterIndex">Logical cluster the row describes. <para>该行描述的逻辑簇。</para></param>
    /// <returns><see langword="true"/> when the row closes the extent. <para>该行收尾该 extent 时为
    /// <see langword="true"/>。</para></returns>
    private static bool IsClosingIndexRow(ErofsInode node, ErofsPclusterPlan[] pclusters, int pclusterIndex, long clusterIndex)
    {
        if (pclusterIndex + 1 != pclusters.Length || (long)node.Size % node.LogicalClusterSize == 0)
        {
            return false;
        }

        if (clusterIndex != (long)node.Size / node.LogicalClusterSize)
        {
            return false;
        }

        var headCluster = pclusters[pclusterIndex].SourceOffset / node.LogicalClusterSize;
        return PlanEndCluster(node, pclusterIndex) - headCluster >= 2;
    }

    /// <summary>
    /// Plans one payload, letting the byte-granular planner run first whenever the cluster-restarting one
    /// cannot reach the density this build asks for. A dense plan that no compressed layout saves a block on,
    /// that the index cannot close at end of file, or that the node's preserved source shape forbids is not
    /// forced: the cluster-restarting planner then plans the node with its own fallbacks.
    /// <para>规划一份负载：只要逐簇重启规划器无法达到本次构建要求的密度，就优先运行按字节粒度的规划器。
    /// 若 dense 规划没有省下任何块、其末尾 extent 无法被索引收尾，或节点所保留的源形态不允许，则不强塞该规划，
    /// 改由逐簇重启规划器按其自身的回退方式规划该节点。</para>
    /// </summary>
    /// <param name="node">Node whose payload is planned; its path must name a readable payload.
    /// <para>待规划负载的节点；其路径必须指向可读负载。</para></param>
    /// <param name="blockSize">Filesystem block size in bytes.
    /// <para>以字节计的文件系统块大小。</para></param>
    /// <param name="options">Per-node compression options.
    /// <para>逐节点的压缩选项。</para></param>
    /// <param name="staging">Staging area receiving every accepted frame, so the write pass copies frames
    /// instead of compressing them a second time.
    /// <para>接收所有被接受帧的暂存区，使写出阶段复制帧而不再压缩第二次。</para></param>
    /// <param name="codecStates">Worker encoder state reused by every probe of this node.
    /// <para>该节点每次探测都复用的工作者编码器状态。</para></param>
    /// <returns>The planned pclusters, or <see langword="null"/> when no compressed layout saves a block.
    /// <para>规划出的 pcluster；没有任何压缩布局能省下一块时为 <see langword="null"/>。</para></returns>
    private static ErofsPclusterPlan[]? TryPlanCompressed(ErofsInode node, int blockSize, ErofsOptions options,
        Core.CompressionPayloadStaging staging, ErofsCodecStatePool codecStates)
    {
        using var batch = new ErofsStagingBatch(staging);
        var context = codecStates.Rent();
        try
        {
            if (RequiresDensePlanner(node, options))
            {
                var dense = TryPlanDenseCompressed(node, blockSize, options, batch, context);
                if (dense is not null && ClosesSafely(node, dense, blockSize))
                {
                    return dense;
                }
            }

            return PlanClusterRestartCompressed(node, blockSize, options, batch, context);
        }
        finally
        {
            codecStates.Return(context);
        }
    }

    /// <summary>
    /// Reports whether a payload must be offered to the byte-granular planner. That is the case for an LZ4
    /// build the cluster-restarting planner cannot fill: an explicit optimization request, a pcluster budget
    /// pinned to one block, where restarting on a cluster boundary cannot spend a block's leftover bytes, and
    /// the interlaced plain layout, whose byte-granular windows only the dense planner lays out. A preserved
    /// source that stored its payload as one whole-file fragment is not offered to it, because that node has
    /// no pcluster shape to fill and the layout packs it whole.
    /// <para>判断一份负载是否必须交给按字节粒度的规划器。适用于逐簇重启规划器填不满的 LZ4 构建：显式要求优化、
    /// pcluster 预算被钉死为一个块（此时在逻辑簇边界重启无法用掉一个块的剩余字节），以及交错原样布局——其按字节
    /// 窗口只有 dense 规划器才会产出。所保留源把负载存成整文件 fragment 时不交给它，因为该节点没有可供填充的
    /// pcluster 形态，布局会把它整份打包。</para>
    /// </summary>
    /// <param name="node">Node the plan belongs to. <para>该规划所属的节点。</para></param>
    /// <param name="options">Per-node compression options.
    /// <para>逐节点的压缩选项。</para></param>
    /// <returns><see langword="true"/> when the dense planner must be tried first.
    /// <para>必须先尝试 dense 规划器时为 <see langword="true"/>。</para></returns>
    internal static bool RequiresDensePlanner(ErofsInode node, ErofsOptions options)
        => options.Algorithm == ErofsCompressionAlgorithm.Lz4
           && (options.OptimizeCompression
               || options.MaxPclusterSize <= options.BlockSize
               || options.InterlacedPlainPclusters)
           && node.SourceInode is not { SourceFragmentShape: ErofsFragmentShape.WholeFile };

    /// <summary>
    /// Plans one synthetic payload, such as an assembled packed-fragment body, through the same planner pair a
    /// collected file uses. The node has no source record, so the caller supplies the path its bytes were
    /// materialized to; this helper fills the compressed-state fields the planners read and returns the plan
    /// without assigning it to the node.
    /// <para>把一份合成负载（例如装配出的 packed fragment 本体）交由收集到的文件所用的同一组规划器规划。该节点
    /// 没有源记录，因此调用方提供负载落地路径；本方法补上规划器读取的压缩状态字段，并返回规划结果而不把它写到
    /// 节点上。</para>
    /// </summary>
    /// <param name="node">Synthetic node describing the materialized payload; a zero logical cluster size
    /// defaults to <paramref name="blockSize"/>, and a missing compression options reference defaults to
    /// <paramref name="options"/>. <para>描述已落地负载的合成节点；逻辑簇大小为零时取
    /// <paramref name="blockSize"/>，压缩选项为空时取 <paramref name="options"/>。</para></param>
    /// <param name="blockSize">Filesystem block size the plan is bounded by.
    /// <para>规划所受约束的文件系统块大小。</para></param>
    /// <param name="options">Compression options to plan with.
    /// <para>规划所用的压缩选项。</para></param>
    /// <param name="staging">Staging area receiving the accepted frames.
    /// <para>接收已接受帧的暂存区。</para></param>
    /// <returns>The planned pclusters, or <see langword="null"/> for an empty payload or when no compressed
    /// layout saves a block, in which case the caller keeps the payload verbatim.
    /// <para>规划出的 pcluster；负载为空、或没有任何压缩布局能省下一块时为 <see langword="null"/>，此时调用方按明文
    /// 保留负载。</para></returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.
    /// <para>某个参数为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="node"/> carries no payload path.
    /// <para><paramref name="node"/> 没有负载路径。</para></exception>
    internal static ErofsPclusterPlan[]? PlanSyntheticCompressedPayload(ErofsInode node, int blockSize,
        ErofsOptions options, Core.CompressionPayloadStaging staging)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(staging);
        if (string.IsNullOrEmpty(node.FullPath))
        {
            throw new ArgumentException(
                "A synthetic payload needs the path its bytes were materialized to.", nameof(node));
        }

        if (node.Size == 0)
        {
            return null;
        }

        node.CompressionOptions ??= options;
        if (node.LogicalClusterSize == 0)
        {
            node.LogicalClusterSize = blockSize;
        }

        using var codecStates = new ErofsCodecStatePool();
        return TryPlanCompressed(node, blockSize, options, staging, codecStates);
    }

    private static ErofsPclusterPlan[]? PlanClusterRestartCompressed(ErofsInode node, int blockSize,
        ErofsOptions options, ErofsStagingBatch batch, ErofsEncoderContext codec)
    {
        using var fs = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var clusterSize = node.LogicalClusterSize;
        if (options.Algorithm == ErofsCompressionAlgorithm.Lz4 && options.Lz4MaxDistance != 0
            && options.Lz4MaxDistance < clusterSize) return null;
        var bigPcluster = DeclaredBigPcluster(options);
        var frameLimit = bigPcluster ? Math.Min(options.MaxPclusterSize, ErofsCompression.MaxPclusterSize) : blockSize;
        var fillDestination = bigPcluster && options.Algorithm == ErofsCompressionAlgorithm.Lz4
            && !options.InterlacedPlainPclusters;
        var totalClusters = CeilDiv((long)node.Size, clusterSize);
        var rawLength = (int)Math.Min((long)clusterSize * 64, (long)node.Size);
        var raw = System.Buffers.ArrayPool<byte>.Shared.Rent(rawLength);
        var compressedLength = ErofsCompression.MaximumOutputSize((ErofsAlgorithm)options.Algorithm, rawLength);
        var compressed = System.Buffers.ArrayPool<byte>.Shared.Rent(compressedLength);
        var acceptedLength = Math.Clamp(frameLimit, 1, compressedLength);
        var accepted = System.Buffers.ArrayPool<byte>.Shared.Rent(acceptedLength);
        var plans = new List<ErofsPclusterPlan>();
        try
        {
            var pos = 0L;
            var clusterIndex = 0L;
            var windowBase = 0L;
            var windowFilled = 0;
            var lastTerminalStart = bigPcluster && (long)node.Size % clusterSize == 0
                ? (totalClusters - 1L) * clusterSize
                : long.MaxValue;
            while (pos < (long)node.Size)
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                if (bigPcluster && pos >= lastTerminalStart)
                {
                    var tail = (int)Math.Min((long)node.Size - pos, clusterSize);
                    plans.Add(new ErofsPclusterPlan
                    {
                        SourceOffset = pos,
                        SourceLength = tail,
                        CompressedLength = tail,
                        Plain = true,
                    });
                    pos += tail;
                    clusterIndex = CeilDiv(pos, clusterSize);
                    continue;
                }

                var acceptedLen = 0;
                var acceptedOut = 0;
                var maxTake = (int)Math.Min(64, totalClusters - clusterIndex);
                if (options.Algorithm == ErofsCompressionAlgorithm.Lz4
                    && options.Lz4MaxDistance is > 0 and < ErofsCompression.Lz4DistanceMax)
                    maxTake = (int)Math.Max(1, Math.Min(maxTake, options.Lz4MaxDistance / clusterSize));

                var inputCap = (int)Math.Min((long)node.Size - pos, (long)maxTake * clusterSize);

                bool TrySpan(int candidateLen, out int outLen)
                {
                    outLen = 0;
                    if (candidateLen <= 0 || candidateLen > rawLength
                        || (bigPcluster && pos + candidateLen > lastTerminalStart))
                    {
                        return false;
                    }

                    FillReadWindow(fs, raw, rawLength, pos, candidateLen, ref windowBase, ref windowFilled);
                    outLen = ErofsPlannerCompression.Compress(
                        options, codec, raw, (int)(pos - windowBase), candidateLen, compressed, compressedLength);
                    if (outLen <= 0 || outLen > frameLimit)
                    {
                        return false;
                    }

                    if (outLen > blockSize && pos % clusterSize + candidateLen <= clusterSize)
                    {
                        return false;
                    }

                    if (pos % clusterSize != 0 && pos % clusterSize + candidateLen <= clusterSize)
                    {
                        return false;
                    }

                    if (!AcceptsClosingIndexRow(pos, candidateLen, outLen, clusterSize, (long)node.Size, blockSize))
                    {
                        return false;
                    }

                    compressed.AsSpan(0, outLen).CopyTo(accepted);
                    return true;
                }

                int ClusterLength(int take)
                {
                    var candidateLen = (int)Math.Min((long)node.Size - pos, (long)take * clusterSize);
                    if (!bigPcluster)
                    {
                        var tail = candidateLen % clusterSize;
                        if (tail != 0)
                        {
                            candidateLen -= tail;
                        }
                    }

                    return candidateLen;
                }

                bool TryTake(int take, out int candidateLen, out int outLen)
                {
                    candidateLen = ClusterLength(take);
                    return TrySpan(candidateLen, out outLen);
                }

                // A big LZ4 pcluster receives the frame limit as its destination budget, so the encoder names
                // the input it consumed: exactly the byte-granular span the cluster search below can only
                // bracket. The window grows only while the encoder exhausts it, so a span far denser than one
                // frame is still found without reading the whole window up front.
                bool TryFill(out int spanLength, out int encodedLength)
                {
                    spanLength = 0;
                    encodedLength = 0;
                    if (!fillDestination || inputCap <= 0)
                    {
                        return false;
                    }

                    var window = (int)Math.Min((long)inputCap, Math.Max(frameLimit, clusterSize) * 3L);
                    window = (int)Math.Min(window, Math.Max(0L, lastTerminalStart - pos));
                    if (window <= 0)
                    {
                        return false;
                    }

                    FillReadWindow(fs, raw, rawLength, pos, window, ref windowBase, ref windowFilled);
                    var outLen = ErofsCompression.CompressLz4Fill(options, codec, raw,
                        (int)(pos - windowBase), window, accepted, Math.Min(frameLimit, accepted.Length),
                        out var consumed);
                    if (outLen <= 0 || consumed <= 0 || consumed > window)
                    {
                        return false;
                    }

                    if (outLen > blockSize && pos % clusterSize + consumed <= clusterSize)
                    {
                        return false;
                    }

                    if (pos % clusterSize != 0 && pos % clusterSize + consumed <= clusterSize)
                    {
                        return false;
                    }

                    if (pos + consumed > (CeilDiv((long)node.Size, clusterSize) - 1L) * clusterSize)
                    {
                        return false;
                    }

                    if (!AcceptsClosingIndexRow(pos, consumed, outLen, clusterSize, (long)node.Size, blockSize))
                    {
                        return false;
                    }

                    spanLength = consumed;
                    encodedLength = outLen;
                    return true;
                }

                if (!TryFill(out acceptedLen, out acceptedOut))
                {
                    acceptedLen = 0;
                    acceptedOut = 0;
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

                    if (fillDestination)
                    {
                        // lib/compress.c:602-614 hands the encoder a destination budget of one pcluster and
                        // takes whatever input it consumed (`erofs_compress_destsize`, lib/compressor_lz4.c:15-26),
                        // so the span a pcluster covers is neither a whole number of logical clusters nor bounded
                        // by the pcluster size. The fill encode above applies that contract directly; this byte
                        // search recovers the same span when the fill frame was refused, for example because the
                        // index could not close it. The cluster search can therefore only bracket the span: it
                        // stops at the first whole-cluster multiple whose encoding no longer fits.
                        // <para>lib/compress.c:602-614 把一整个 pcluster 的输出预算交给编码器，并采纳它消耗掉的
                        // 输入（<c>erofs_compress_destsize</c>，lib/compressor_lz4.c:15-26），因此 pcluster 覆盖的
                        // 跨度既不是整数个逻辑簇，也不受 pcluster 大小约束。上面的填满编码直接执行这一契约；当它的
                        // 帧被拒绝（例如索引无法收尾）时，由这段按字节搜索还原同一跨度。逐簇搜索因此只能给出跨度的
                        // 区间：它在第一个编码放不下的整簇倍数处停下。</para>
                        //
                        // A bracket that ends inside the first block cannot yield a different decision, because the
                        // block-rounding test after the search compares a block-rounded encoded length against the
                        // candidate span: every candidate here is at most one block long, so its rounded length is
                        // at least one block and therefore never shorter than the span itself, which makes the test
                        // discard it. The same reasoning covers a cluster candidate accepted before the search, so
                        // the byte search would only spend probes on an outcome that is dropped either way.
                        // <para>止于首块之内的区间不可能给出不同判定，因为搜索之后的按块取整判定比较的是取整后的编码
                        // 长度与候选跨度：此处每个候选都不长于一块，其取整长度至少一块，因而绝不短于跨度本身，判定必然
                        // 丢弃它。搜索之前已被接受的簇候选同理，因此字节搜索只会把探测花在一个无论如何都被丢弃的结果上。</para>
                        var high = (int)Math.Min(inputCap, (long)hi * clusterSize);
                        if (high > blockSize)
                        {
                            var low = acceptedLen;
                            while (high - low > 1)
                            {
                                var mid = low + (high - low) / 2;
                                if (TrySpan(mid, out var midOut))
                                {
                                    low = mid;
                                    acceptedLen = mid;
                                    acceptedOut = midOut;
                                }
                                else
                                {
                                    high = mid;
                                }
                            }
                        }
                    }
                }

                // lib/compress.c:616-628 rounds an encoded length up to a whole block and keeps the compressed frame
                // only when that rounded length is strictly smaller than the bytes the frame encodes; otherwise the
                // span is stored raw one block at a time. The test has to run on the accepted span, not on a probe:
                // moving it into the probe would make the very first whole-cluster probe fail on any span whose
                // encoding rounds up to its own length, and the escalating probe leaves the search at that first
                // failure, so the file would fall back to raw storage as a whole instead of one block at a time.
                // <para>lib/compress.c:616-628 把编码长度向上取整到整块，且仅当取整后的长度严格小于该帧编码的
                // 字节数时才保留压缩帧；否则该跨度按一块一段原样存放。该判定必须作用于最终接受的跨度而不是某次探测：若把它
                // 移进探测内部，任何编码长度向上取整后等于自身长度的跨度都会让第一个整簇探测失败，而递增式探测正
                // 停在这次失败处，于是整份文件会退回整文件原样存放，而不是一块一段地原样存放。</para>
                if (acceptedLen > 0 && acceptedOut > 0
                    && CeilDiv(acceptedOut, blockSize) * blockSize >= acceptedLen)
                {
                    acceptedLen = 0;
                    acceptedOut = 0;
                }

                if (acceptedLen <= 0 || acceptedOut <= 0)
                {
                    if (clusterSize != blockSize) return null;
                    var length = (int)Math.Min((long)node.Size - pos, clusterSize);
                    plans.Add(new ErofsPclusterPlan { SourceOffset = pos, SourceLength = length, CompressedLength = length, Plain = true });
                    pos += length;
                    clusterIndex = CeilDiv(pos, clusterSize);
                    continue;
                }

                var plan = new ErofsPclusterPlan
                {
                    SourceOffset = pos,
                    SourceLength = acceptedLen,
                    CompressedLength = acceptedOut,
                };
                batch.Append(plan, accepted.AsSpan(0, acceptedOut));
                plans.Add(plan);
                pos += acceptedLen;
                clusterIndex = CeilDiv(pos, clusterSize);
            }

            if (plans.Count > 0 && node.SourceInode is not { SourceCompressed: true }
                && plans.Sum(p => CeilDiv(p.CompressedLength, blockSize)) >= totalClusters)
            {
                return null;
            }

            return [.. plans];
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(raw);
            System.Buffers.ArrayPool<byte>.Shared.Return(compressed);
            System.Buffers.ArrayPool<byte>.Shared.Return(accepted);
        }
    }

    /// <summary>
    /// Makes the bytes of one candidate span available in the read window, reading only the part the window
    /// still lacks. Consecutive pclusters overlap heavily: a pcluster starts where the previous one ended while
    /// its probes reach up to sixty-four clusters ahead, so refilling from the pcluster start would read the
    /// same bytes once per pcluster. The window therefore keeps everything already read and only moves its base
    /// when the candidate would run past the buffer, which is when the surviving bytes are moved to its front.
    /// Reads stay sequential either way, and the encoder receives exactly the bytes the file holds.
    /// <para>让一个候选跨度的字节出现在读取窗口中，只读取窗口尚缺的部分。相邻 pcluster 大幅重叠：pcluster 从
    /// 上一个结束处开始，其探测却向前伸到六十四个簇，因此每次从 pcluster 起点重新读取会让同一批字节被读一遍又
    /// 一遍。窗口因此保留已读字节，只在候选越过缓冲区时移动基址，并把仍然有用的字节移到窗口前端。两种情况下读取
    /// 都保持顺序，编码器拿到的正是文件中的那些字节。</para>
    /// </summary>
    /// <param name="source">Source payload stream positioned by this method, never by the caller.
    /// <para>由本方法定位的源负载流，调用方不负责其位置。</para></param>
    /// <param name="buffer">Read window shared by every probe of the node.
    /// <para>该节点所有探测共用的读取窗口。</para></param>
    /// <param name="bufferLength">Usable length of <paramref name="buffer"/>; the rented array may be longer.
    /// <para><paramref name="buffer"/> 的可用长度；借用的数组可能更长。</para></param>
    /// <param name="position">File offset the candidate starts at, which becomes the window base when the
    /// candidate no longer fits. <para>候选的起始文件偏移；候选放不下时它成为窗口基址。</para></param>
    /// <param name="length">Candidate length in bytes. <para>候选的字节长度。</para></param>
    /// <param name="windowBase">File offset of the first window byte, updated in place.
    /// <para>窗口首字节的文件偏移，就地更新。</para></param>
    /// <param name="windowFilled">Number of valid window bytes from its base, updated in place.
    /// <para>自窗口基址起的有效字节数，就地更新。</para></param>
    private static void FillReadWindow(FileStream source, byte[] buffer, int bufferLength, long position,
        int length, ref long windowBase, ref int windowFilled)
    {
        if (position + length > windowBase + bufferLength)
        {
            var keep = (int)Math.Clamp(windowBase + windowFilled - position, 0L, bufferLength);
            if (keep > 0)
            {
                Buffer.BlockCopy(buffer, (int)(position - windowBase), buffer, 0, keep);
            }

            windowBase = position;
            windowFilled = keep;
        }

        if (position + length > windowBase + windowFilled)
        {
            var need = (int)(position + length - (windowBase + windowFilled));
            source.Position = windowBase + windowFilled;
            source.ReadExactly(buffer, windowFilled, need);
            windowFilled += need;
        }
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
        var rawLength = node.Pclusters!.Max(static p => p.SourceLength);
        var compressedCapacity = ErofsCompression.MaximumOutputSize((ErofsAlgorithm)options.Algorithm, rawLength);
        var raw = System.Buffers.ArrayPool<byte>.Shared.Rent(rawLength);
        var compressed = System.Buffers.ArrayPool<byte>.Shared.Rent(compressedCapacity);
        using var codec = new ErofsEncoderContext();
        try
        {
            foreach (var p in node.Pclusters!)
            {
                options.CancellationToken.ThrowIfCancellationRequested();
                if (p.Fragment)
                {
                    continue;
                }

                if (node.InlineCompressedTail && ReferenceEquals(p, node.Pclusters![^1]))
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
                        options, codec, raw, 0, p.SourceLength, compressed, 0, compressedCapacity);
                    if (outLen != p.CompressedLength)
                    {
                        throw new FileSystemCorruptedException(
                            $"Compressed length mismatch for '{node.FullPath}': {outLen} != {p.CompressedLength}.");
                    }
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
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(raw);
            System.Buffers.ArrayPool<byte>.Shared.Return(compressed);
        }
    }

    /// <summary>
    /// Writes the final compressed pcluster into the inode metadata record when the node stores it inline.
    /// The frame is read back from the build's staging file, or re-encoded from the source when no staged
    /// offset was recorded; it is placed so that it ends at the record's measured end, which is the layout
    /// <see cref="GetInodeRecordSize"/> already accounted for. A metabox resident passes its body-relative
    /// slot offset, so the tail lands inside the container body exactly where the plain metadata area would
    /// carry it.
    /// <para>当节点把末尾压缩 pcluster 内联存放时，把它写入 inode 元数据记录。帧从本次构建的暂存文件读回，未记录
    /// 暂存偏移时则从源重新编码；其结束位置对齐到记录的度量末端，这正是 <see cref="GetInodeRecordSize"/> 已经计入的
    /// 布局。metabox 常驻节点传入其相对容器本体的槽位偏移，因此尾部落在容器本体内，与普通元数据区所携带的位置一致。</para>
    /// </summary>
    /// <param name="output">Destination stream in the coordinates of <paramref name="inodeOffset"/>.
    /// <para>与 <paramref name="inodeOffset"/> 同坐标的目标流。</para></param>
    /// <param name="inodeOffset">Byte offset of the inode record in <paramref name="output"/>.
    /// <para>inode 记录在 <paramref name="output"/> 中的字节偏移。</para></param>
    /// <param name="node">Node whose final pcluster is stored inline.
    /// <para>末尾 pcluster 内联存放的节点。</para></param>
    /// <param name="blockSize">Filesystem block size in bytes.
    /// <para>以字节计的文件系统块大小。</para></param>
    /// <param name="options">Validated build options used when the frame has to be re-encoded.
    /// <para>需要重新编码帧时所使用的经校验构建选项。</para></param>
    /// <param name="staging">Staging file holding accepted frames, or <see langword="null"/> when the build
    /// staged none. <para>承载已接受帧的暂存文件；构建未暂存任何帧时为 <see langword="null"/>。</para></param>
    /// <exception cref="FileSystemCorruptedException">Re-encoding the frame does not reproduce the recorded
    /// compressed length. <para>重新编码的帧长度与记录的压缩长度不同。</para></exception>
    private static void WriteInlineCompressedTail(Stream output, long inodeOffset, ErofsInode node,
        int blockSize, ErofsOptions options, Core.CompressionPayloadStaging? staging)
    {
        var plan = node.Pclusters![^1];
        var encodedLength = plan.CompressedLength;
        var compressionOptions = node.CompressionOptions ?? options;
        var capacity = plan.StagedOffset >= 0 && staging is not null
            ? encodedLength
            : ErofsCompression.MaximumOutputSize((ErofsAlgorithm)compressionOptions.Algorithm, plan.SourceLength);
        var compressed = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(capacity, 1));
        try
        {
            if (plan.StagedOffset >= 0 && staging is not null)
            {
                staging.Read(plan.StagedOffset, compressed.AsSpan(0, encodedLength));
            }
            else
            {
                var raw = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(plan.SourceLength, 1));
                try
                {
                    using var source = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    source.Position = plan.SourceOffset;
                    source.ReadExactly(raw, 0, plan.SourceLength);
                    using var codec = new ErofsEncoderContext();
                    var written = ErofsCompression.AlgorithmCompress(compressionOptions, codec, raw, 0,
                        plan.SourceLength, compressed, 0, capacity);
                    if (written != encodedLength)
                    {
                        throw new FileSystemCorruptedException(
                            $"Inline compressed tail length mismatch for '{node.FullPath}': {written} != {encodedLength}.");
                    }
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(raw);
                }
            }

            var tailStart = inodeOffset + GetInodeRecordSize(node, blockSize) - encodedLength;
            WriteAt(output, tailStart, compressed, encodedLength);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(compressed);
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
        var chained = recordSize <= 4;
        var physical = chained ? checked((long)pclusters[0].BlockAddress * blockSize) : 0L;
        var logical = 0L;
        if (chained)
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
                var extentPhysical = chained
                    ? physical
                    : checked((long)plan.BlockAddress * blockSize);
                BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan(recordOffset + 4), (uint)extentPhysical);
            }

            if (recordSize >= 16)
            {
                var extentPhysical = chained
                    ? physical
                    : checked((long)plan.BlockAddress * blockSize);
                BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan(recordOffset + 8), (uint)(extentPhysical >> 32));
                BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan(recordOffset + 12), (uint)logical);
            }

            if (recordSize >= 32)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan(recordOffset + 16), (uint)(logical >> 32));
            }

            pending++;
            logical += plan.SourceLength;
            if (chained)
            {
                physical += encoded & ZErofsMapHeader.ExtentPlenMask;
            }

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
                if (IsClosingIndexRow(node, pclusters, pclusterIndex, c))
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(entries.AsSpan(entryStart), ZErofsLclusterIndex.LclusterTypePlain);
                    BinaryPrimitives.WriteUInt16LittleEndian(entries.AsSpan(entryStart + 2),
                        (ushort)((long)node.Size % node.LogicalClusterSize));
                    BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan(entryStart + 4), 0);
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
    /// 4-byte head and a 4-byte tail. Every pack's trailing four bytes carry the block address a reader adds
    /// the block count it recovers by walking that pack's entries backwards, and the value follows
    /// <c>write_compacted_indexes</c> in <c>lib/compress.c</c> literally: the base is the running block
    /// address at the pack start, raised by one exactly once per pack when the pack's first non-NONHEAD row
    /// (a head, a plain row, or a trailing hole row) observes <c>dummy_head</c> set, because the reader's
    /// big-pcluster walk counts from zero there. A pack whose first row is a NONHEAD row is deliberately not
    /// raised: when that row is the CBLKCNT row of the pcluster the previous pack ended inside, the reader
    /// recovers that pcluster's block count from it instead, and the same row clears <c>dummy_head</c>, so
    /// the head following it never sees the flag and is never raised either. The raise is consumed by the
    /// first non-NONHEAD row whether or not it observed the flag, which is what upstream's
    /// <c>update_blkaddr</c> latch does. The zero filler slots that pad the final pack's tail are not
    /// non-NONHEAD rows here: they can only follow every real row of that pack, so a pack that carries a head
    /// consumes the latch on a real row first and the base it records is unaffected.
    /// <para>写出紧凑 EROFS 索引：每条 4 字节、按 8 字节打包；当 inode 声明
    /// <c>Z_EROFS_ADVISE_COMPACTED_2B</c> 时，在 4 字节头部与 4 字节尾部之间插入按 32 字节打包的 2 字节中段。
    /// 每个包末尾四字节存放读取方在包内反向遍历时叠加块数的基准，其取值逐条对应 <c>lib/compress.c</c> 的
    /// <c>write_compacted_indexes</c>：基准等于包起点处的运行块地址，且每包最多抬高一次——当且仅当本包首个非
    /// 非头行（head、plain 行或尾部洞行）观察到 <c>dummy_head</c> 置起时抬高，因为读取方的 big-pcluster 遍历在
    /// 该处从零起算。首行为非头行的包刻意不抬高：若该行是上一包结束处所在 pcluster 的 CBLKCNT 行，读取方改为
    /// 从该行取回该 pcluster 的块数，而同一行会把 <c>dummy_head</c> 置为假，因此紧随其后的 head 观察不到该标志，
    /// 同样不会被抬高。抬高只被本包首个非头行消费一次，无论该行是否观察到标志，这正是上游
    /// <c>update_blkaddr</c> 闩锁的语义。补齐末包尾部的全零槽位在此不算非头行：它们只能位于该包所有真实行
    /// 之后，因此承载 head 的包必先在某真实行上消费掉该闩锁，其记录的基准不受这些槽位影响。</para>
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

            var packStartBlkaddr = blkaddr;
            var updatePackBlkaddr = bigPcluster;
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
                        if (dummyHead)
                        {
                            blkaddr++;
                            if (updatePackBlkaddr)
                            {
                                packStartBlkaddr = blkaddr;
                            }
                        }

                        dummyHead = true;
                        updatePackBlkaddr = false;
                    }
                    else if (entryIndex == headCluster)
                    {

                        type = plan.Plain ? (byte)0 : (byte)1;
                        offset = (uint)(plan.SourceOffset % node.LogicalClusterSize);
                        if (dummyHead)
                        {
                            blkaddr++;
                            if (updatePackBlkaddr)
                            {
                                packStartBlkaddr = blkaddr;
                            }

                            if (!plan.Fragment && blkaddr != plan.BlockAddress)
                            {
                                throw new FileSystemCorruptedException(
                                    $"Compacted index blkaddr {blkaddr} != planned block {plan.BlockAddress} for '{node.FullPath}'.");
                            }
                        }

                        dummyHead = true;
                        updatePackBlkaddr = false;
                    }
                    else
                    {
                        type = 2;
                        var d0 = entryIndex - headCluster;
                        if (IsClosingIndexRow(node, pclusters, pclusterIndex, entryIndex))
                        {
                            type = 0;
                            offset = (uint)((long)node.Size % node.LogicalClusterSize);
                        }
                        else if (bigPcluster && d0 == 1 && !plan.Fragment)
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
        // Mirrors WriteAt: a plain cluster writes its padding immediately after its payload, so the
        // cursor already sits at the padding start and re-seeking is a wasted syscall.
        // <para>与 WriteAt 一致：原样簇在负载之后紧接着写补齐，因此游标已停在补齐起点，重复定位是浪费的系统调用。</para>
        if (output.Position != offset)
        {
            output.Position = offset;
        }

        var zeroes = ZeroBuffer;
        var written = 0L;
        while (written < count)
        {
            var n = (int)Math.Min(zeroes.Length, count - written);
            output.Write(zeroes, 0, n);
            written += n;
        }
    }

    /// <summary>
    /// Shared zero buffer reused by every padding write. Padding runs once per pcluster and once per plain
    /// cluster, so a per-call array would turn one image into tens of thousands of short-lived buffers.
    /// The buffer is only ever read, so sharing it between writes needs no synchronization.
    /// <para>由所有补齐写共享的零缓冲。补齐在每个 pcluster 与每个原样簇上各发生一次，因此按调用分配数组会让
    /// 一次镜像构建产生数万个短命缓冲。该缓冲只被读取，因此在写之间共享无需同步。</para>
    /// </summary>
    private static readonly byte[] ZeroBuffer = new byte[1 << 16];

    private const int HashChunkBytes = 1 << 20;

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
    /// Whether an uncompressed regular inode and its complete metadata record fit within one METABOX block.
    /// This is the fresh-build eligibility policy; a preserved rebuild follows each node's recorded metadata
    /// creation type first and only falls back to this policy when the profile recorded none.
    /// <para>未压缩的常规 inode 及其完整元数据记录是否能放入单个 METABOX 块。这是全新构建的资格策略；保留式重建
    /// 优先遵循每个节点记录的元数据创建形态，仅在档案未记录形态时才退回该策略。</para>
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
    /// Assembles the metabox container body: every resident's complete metadata placed at its slot through
    /// <see cref="WriteInodeMetadata"/>, plus the shared xattr area at its planned offset. The body is
    /// produced as one buffer rather than written straight into the image because the container may be
    /// encoded, and an encoder needs the whole payload addressable before it plans a single pcluster.
    /// <para>装配 metabox 容器本体：经 <see cref="WriteInodeMetadata"/> 把每个常驻 inode 的完整元数据放到其槽位，
    /// 并在规划的偏移处写入共享 xattr 区。本体以单个缓冲产出而不是直接写进镜像，因为容器可能被编码，而编码器需要
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
    /// <param name="staging">Staging file holding accepted compressed frames, or <see langword="null"/> when
    /// this build staged none. <para>承载已接受压缩帧的暂存文件；本次构建未暂存任何帧时为
    /// <see langword="null"/>。</para></param>
    /// <returns>The zero-filled body buffer with every resident and shared xattr record in place. Gaps stay
    /// zero, which is what the image preallocation used to guarantee for the slot-wise writer.
    /// <para>已填入全部常驻记录与共享 xattr 记录、其余为零的本体缓冲。空隙保持为零，这正是逐槽位写入方原先
    /// 依赖镜像预分配保证的结果。</para></returns>
    private static byte[] BuildMetaboxContainerBody(int blockSize, ulong now, List<ErofsInode> residents,
        long bodyLength, long sharedXattrOffset, List<byte[]> sharedXattrs, ErofsOptions options,
        Core.CompressionPayloadStaging? staging)
    {
        var body = new byte[checked((int)bodyLength)];

        using (var bodyStream = new MemoryStream(body, writable: true))
        {
            foreach (var resident in residents)
            {
                var slotOffset = checked(resident.MetaboxSlot * ErofsSuperblock.CompactInodeSize);
                WriteInodeMetadata(bodyStream, slotOffset, resident, blockSize, now, options, staging);
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
    /// <param name="staging">Staging file holding accepted compressed frames, or <see langword="null"/> when
    /// this build staged none. <para>承载已接受压缩帧的暂存文件；本次构建未暂存任何帧时为
    /// <see langword="null"/>。</para></param>
    private static void WriteMetaboxContainer(Stream output, ErofsInode metaboxNode, int blockSize, ulong now,
        List<ErofsInode> residents, long bodyLength, long sharedXattrOffset, List<byte[]> sharedXattrs,
        ErofsOptions options, Core.CompressionPayloadStaging? staging)
    {
        var baseOffset = checked(metaboxNode.FlatBlockAddr * blockSize);
        var body = BuildMetaboxContainerBody(blockSize, now, residents, bodyLength, sharedXattrOffset,
            sharedXattrs, options, staging);
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
    /// <param name="inodeOffset">Byte offset of the inode record in the same coordinates as
    /// <paramref name="output"/>; a metabox resident passes its body-relative slot offset.
    /// <para>inode 记录在 <paramref name="output"/> 同坐标下的字节偏移；metabox 常驻节点传入其相对容器本体的槽位偏移。</para></param>
    /// <exception cref="InvalidOperationException">The node carries no chunk index.
    /// <para>该节点不携带 chunk 索引。</para></exception>
    /// <exception cref="FileSystemUnsupportedFeatureException">A nonzero device id was requested while the
    /// node's format selects the 4-byte block map, which has no field for it.
    /// <para>节点格式选择 4 字节块映射时请求了非零设备号，而该形态没有承载设备号的字段。</para></exception>
    private static void WriteChunkIndexTable(Stream output, ErofsInode node, long inodeOffset)
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
        var tableOffset = AlignUp(inodeOffset + node.InlineDataOffset, unit);
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
    /// <param name="output">Destination stream in the coordinates of <paramref name="inodeOffset"/>.
    /// <para>与 <paramref name="inodeOffset"/> 同坐标的目标流。</para></param>
    /// <param name="node">Fragment inode whose map header is written.
    /// <para>待写出映射头的 fragment inode。</para></param>
    /// <param name="inodeOffset">Byte offset of the inode record in <paramref name="output"/>.
    /// <para>inode 记录在 <paramref name="output"/> 中的字节偏移。</para></param>
    private static void WriteFragmentHeader(Stream output, ErofsInode node, long inodeOffset)
    {
        var headerOffset = inodeOffset + AlignUp(node.OnDiskSize + node.XattrISize, 8);
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
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(HashChunkBytes);
        try
        {
            var remaining = (long)node.Size;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                var count = (int)Math.Min((long)HashChunkBytes, remaining);
                fs.ReadExactly(buffer, 0, count);
                hash.Append(buffer.AsSpan(0, count));
                remaining -= count;
            }

            return hash.GetHashAndReset();
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
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
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(HashChunkBytes);
        try
        {
            var remaining = length;
            var pos = targetOffset;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                var count = (int)Math.Min((long)HashChunkBytes, remaining);
                source.ReadExactly(buffer, 0, count);
                WriteAt(output, pos, buffer, count);
                pos += count;
                remaining -= count;
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
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
        // Pcluster writes are right-aligned inside a block-rounded region, so a padded frame performs a
        // padding write immediately followed by a frame write at the offset that write just reached.
        // Re-issuing the seek in that case costs a syscall per pcluster for a position the stream already
        // reports, so the seek is skipped when the cursor is already where the write belongs.
        // <para>pcluster 写在按块取整的区间内右对齐，因此带填充的帧会在补齐写之后紧接着把帧写到该写刚到达的
        // 偏移上。此时重复发定位会为每个 pcluster 白付一次系统调用，而流自身已经报告了该位置，因此在游标
        // 已就位时跳过定位。</para>
        if (output.Position != offset)
        {
            output.Position = offset;
        }

        output.Write(data, 0, count);
    }

    /// <summary>
    /// Writes a span at an absolute offset, skipping the seek when the stream already sits there. The inode
    /// record is written from a bounded stack span, so this overload avoids materializing a temporary array
    /// for every inode of a large image.
    /// <para>在绝对偏移处写出一个跨度，流已位于该处时跳过定位。inode 记录由有界的栈上跨度写出，因此该重载避免为
    /// 大镜像的每个 inode 生成临时数组。</para>
    /// </summary>
    /// <param name="output">Destination stream. <para>目标流。</para></param>
    /// <param name="offset">Absolute write offset in the stream's coordinates.
    /// <para>流坐标下的绝对写入偏移。</para></param>
    /// <param name="data">Bytes to write. <para>待写出的字节。</para></param>
    private static void WriteAt(Stream output, long offset, ReadOnlySpan<byte> data)
    {
        if (output.Position != offset)
        {
            output.Position = offset;
        }

        output.Write(data);
    }
}