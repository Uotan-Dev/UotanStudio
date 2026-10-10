using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using UotanStudio.Firmware.FileSystem.Erofs.Models;

namespace UotanStudio.Firmware.FileSystem.Erofs.Builder;

/// <summary>
/// EROFS builder part that orchestrates the image layout phases extracted from
/// <see cref="ErofsBuilder.BuildImage"/>: layout origin computation, edit plan binding,
/// hardlink merging, payload planning, inode/data offset assignment, and the final write-out.
/// <para>EROFS 构建器的组成部分，承载从 <see cref="ErofsBuilder.BuildImage"/> 拆出的镜像布局阶段：
/// 布局起点计算、编辑计划绑定、硬链接合并、负载规划、inode 与数据偏移分配以及最终写出。</para>
/// </summary>
public sealed partial class ErofsBuilder
{
    /// <summary>
    /// Shared mutable context threaded through the image build phases; it carries the values that
    /// were locals of the former monolithic <see cref="ErofsBuilder.BuildImage"/> body.
    /// <para>贯穿镜像构建各阶段共享的上下文；承载原先单体 <see cref="ErofsBuilder.BuildImage"/> 方法体
    /// 中的局部状态。</para>
    /// </summary>
    private sealed class ImageBuildContext(BuildNode root, ErofsOptions options)
    {
        public BuildNode Root { get; } = root;

        public ErofsOptions Options { get; } = options;

        /// <summary>Block size in bytes from the validated options.
        /// <para>来自经校验选项的块大小（字节）。</para></summary>
        public int BlockSize { get; init; }

        /// <summary>Image build timestamp; the explicit build time or the current UTC seconds.
        /// <para>镜像构建时间戳；为显式构建时间或当前 UTC 秒数。</para></summary>
        public ulong Now { get; init; }

        /// <summary>Number of extra devices declared by the options.
        /// <para>选项声明的额外设备数量。</para></summary>
        public int ExtraDevices { get; init; }

        /// <summary>Image offset where the inode metadata table starts.
        /// <para>inode 元数据表在镜像中的起始偏移。</para></summary>
        public long InodeTableOffset { get; init; }

        /// <summary>Image offset of the extra device table; zero when there are no extra devices.
        /// <para>额外设备表的镜像偏移；无额外设备时为 0。</para></summary>
        public long DeviceTableOffset { get; init; }

        /// <summary>s_devt_slotoff value: the device table offset in device-slot units.
        /// <para>s_devt_slotoff 值：以设备槽为单位的设备表偏移。</para></summary>
        public ushort DeviceTableSlotOffset { get; init; }

        /// <summary>Compression configuration bytes written right after the superblock (or metabox slot).
        /// <para>写在超级块（或 metabox 槽）之后的压缩配置字节。</para></summary>
        public byte[] CompressionConfiguration { get; init; } = [];

        /// <summary>Pre-order list of all collected nodes; packed and metabox nodes are appended later.
        /// <para>全部收集节点的先序列表；打包节点与 metabox 节点稍后追加。</para></summary>
        public List<BuildNode> Ordered { get; } = [];

        /// <summary>
        /// Staging file holding the compressed payloads accepted during <see cref="PlanNodePayloads"/>;
        /// the write pass copies from it instead of re-compressing each pcluster. Null until the first
        /// compressed plan stages bytes, and disposed with the build.
        /// <para>承载 <see cref="PlanNodePayloads"/> 阶段接受的压缩负载的暂存文件；写出阶段从中复制而不再
        /// 重新压缩。首次压缩规划写入前为 null，并随构建结束释放。</para>
        /// </summary>
        public Core.CompressionPayloadStaging? StagedCompression { get; private set; }

        /// <summary>Creates the compression payload staging file on first use.
        /// <para>首次使用时创建压缩负载暂存文件。</para></summary>
        public Core.CompressionPayloadStaging EnsureStagedCompression() => StagedCompression ??= new();

        public List<(BuildNode Alias, BuildNode Canonical)> Hardlinks { get; } = [];

        public List<PackedFragment> PackedFragments { get; } = [];

        /// <summary>End offset (exclusive) of the fragment packing area.
        /// <para>fragment 打包区的结束偏移（不含）。</para></summary>
        public long PackedEnd { get; set; }

        public Dictionary<string, BuildNode> DedupFlat { get; } = new();

        public Dictionary<string, BuildNode> DedupFragments { get; } = new();

        /// <summary>Synthetic node backing the shared fragment packing area; null when unused.
        /// <para>承载共享 fragment 打包区的合成节点；未使用时为 null。</para></summary>
        public BuildNode? PackedNode { get; private set; }

        /// <summary>Nodes chosen as metabox-resident inodes; non-null once metabox planning ran.
        /// <para>被选为 metabox 常驻 inode 的节点；metabox 规划执行后非 null。</para></summary>
        public List<BuildNode>? MetaboxResident { get; private set; }

        /// <summary>Synthetic node backing the metabox container; null when metabox is unused.
        /// <para>承载 metabox 容器的合成节点；未启用 metabox 时为 null。</para></summary>
        public BuildNode? MetaboxNode { get; private set; }

        /// <summary>
        /// Creates the build context: captures the superblock checksum window and computes the layout
        /// origin (extra device table, compression configuration, and inode table start offsets).
        /// <para>创建构建上下文：捕获超级块校验和窗口并计算布局起点（额外设备表、压缩配置与 inode 表
        /// 的起始偏移）。</para>
        /// </summary>
        /// <param name="root">Root of the collected node tree.
        /// <para>收集到的节点树根节点。</para></param>
        /// <param name="options">Validated build options.
        /// <para>经校验的构建选项。</para></param>
        /// <param name="output">Offset-wrapped destination stream the window is captured on.
        /// <para>捕获窗口所用的偏移包装输出流。</para></param>
        /// <returns>The initialized build context.
        /// <para>初始化完成的构建上下文。</para></returns>
        public static ImageBuildContext Create(BuildNode root, ErofsOptions options, OffsetStream output)
        {
            var blockSize = options.BlockSize;
            var now = options.BuildTime ?? (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            output.CaptureWindow(ErofsSuperblock.SuperblockOffset,
                blockSize > ErofsSuperblock.SuperblockOffset ? blockSize - ErofsSuperblock.SuperblockOffset : blockSize);

            var extraDevices = options.ExtraDeviceCount;
            var compressionConfiguration = BuildCompressionConfiguration(options);
            var compressionConfigurationOffset = InodeTableStart + (options.Metabox ? ErofsSuperblock.ExtSlotSize : 0);
            long inodeTableStart;
            long devtOffset;
            ushort devtSlotOff;
            if (extraDevices > 0)
            {
                devtOffset = blockSize;
                var devtSlotCount = extraDevices;
                var devtTableSize = (long)devtSlotCount * ErofsDeviceSlot.Size;
                inodeTableStart = AlignUp(devtOffset + devtTableSize, blockSize);
                devtSlotOff = checked((ushort)(devtOffset / ErofsDeviceSlot.Size));
            }
            else
            {
                inodeTableStart = AlignUp(compressionConfigurationOffset + compressionConfiguration.Length,
                    ErofsSuperblock.CompactInodeSize);
                devtOffset = 0;
                devtSlotOff = 0;
            }

            return new ImageBuildContext(root, options)
            {
                BlockSize = blockSize,
                Now = now,
                ExtraDevices = extraDevices,
                InodeTableOffset = inodeTableStart,
                DeviceTableOffset = devtOffset,
                DeviceTableSlotOffset = devtSlotOff,
                CompressionConfiguration = compressionConfiguration,
            };
        }

        /// <summary>
        /// Collects the pre-order node list, binds xattrs, and then binds either the edit plan entries
        /// or the source-inode records by path, and finally flags nodes needing extended mtimes.
        /// <para>收集先序节点列表、绑定 xattr，然后按编辑计划条目或按路径绑定源 inode 记录，
        /// 最后为需要扩展 mtime 的节点打标。</para>
        /// </summary>
        internal void CollectOrderedNodes()
        {
            CollectPreOrder(Root, Ordered);
            BindXattrs(Ordered, Options);
            if (UotanStudio.Firmware.FileSystem.Rebuilding.FileSystemEditPlan.Current is { } editPlan)
            {
                BindEditPlan(editPlan);
            }
            else
            {
                BindSourceInodesByPath();
            }

            foreach (var node in Ordered)
            {
                var timestamp = node.SourceInode?.Mtime ?? (node.Mtime > 0 ? (ulong)node.Mtime : Now);
                node.RequiresExtendedTime = timestamp < Now || timestamp - Now > uint.MaxValue;
            }
        }

        /// <summary>
        /// Overlays the current file system edit plan onto the ordered nodes: identity, source inode,
        /// name, ownership, mode-derived type flags, device ids, and inline xattrs.
        /// <para>把当前文件系统编辑计划覆盖到有序节点上：身份、源 inode、名称、属主、由 mode 推导的
        /// 类型标志、设备号与 inline xattr。</para>
        /// </summary>
        /// <param name="editPlan">The active edit plan snapshot.
        /// <para>当前生效的编辑计划快照。</para></param>
        private void BindEditPlan(UotanStudio.Firmware.FileSystem.Rebuilding.FileSystemEditPlan editPlan)
        {
            foreach (var node in Ordered)
            {
                Options.CancellationToken.ThrowIfCancellationRequested();
                var entry = editPlan.Get(node.FullPath);
                node.OriginalInode = entry.Inode;
                node.SourceInode = Options.SourceInodes?.GetValueOrDefault(entry.Inode);
                if (entry.Metadata?.ModificationTime is { } time)
                {
                    node.SourceInode = node.SourceInode is { } sourceInode
                        ? sourceInode with { Mtime = checked((ulong)time.Seconds), MtimeNanoseconds = time.Nanoseconds }
                        : new ErofsSourceInode(checked((ulong)time.Seconds), time.Nanoseconds, true,
                            Options.Compress, Options.Algorithm, Options.UseCompactedIndexes, false, BlockSize, entry.Path);
                }

                if ((entry.Mode & InodeTypeBits.FormatMask) == InodeTypeBits.RegularFile)
                {
                    node.FullPath = editPlan.ContentSource(node.FullPath);
                    node.Size = new FileInfo(node.FullPath).Length;
                }

                node.Name = UotanStudio.Firmware.FileSystem.Rebuilding.FileSystemEditPlan.Name(entry.Path);
                node.Uid = entry.Uid;
                node.Gid = entry.Gid;
                node.ModeOverride = entry.Mode;
                node.HasModeOverride = true;
                node.IsSymlink = (entry.Mode & InodeTypeBits.FormatMask) == InodeTypeBits.SymbolicLink;
                if (node.IsSymlink)
                {
                    node.SymlinkTarget = entry.SymlinkTarget ?? "";
                    node.Size = Encoding.UTF8.GetByteCount(node.SymlinkTarget);
                }

                node.IsDevice = (entry.Mode & InodeTypeBits.FormatMask) is InodeTypeBits.Fifo or InodeTypeBits.CharDevice or InodeTypeBits.BlockDevice or InodeTypeBits.Socket;
                node.DeviceMode = entry.Mode;
                node.DeviceRdev = entry.DeviceId;
                if (node.IsDevice)
                {
                    node.Size = 0;
                }

                node.XattrIbody = entry.Xattrs is { Count: > 0 } ? ErofsXattr.EncodeInline([.. entry.Xattrs.Select(p => new ErofsXattrEntry { Name = p.Key, Value = p.Value })]) : [];
            }
        }

        /// <summary>
        /// Binds source inode records by image-relative path for nodes without an already bound record.
        /// <para>对尚无源 inode 记录的节点，按镜像相对路径绑定源 inode 记录。</para>
        /// </summary>
        private void BindSourceInodesByPath()
        {
            if (Options.SourceInodesByPath is not { Count: > 0 } byPath)
            {
                return;
            }

            foreach (var node in Ordered)
            {
                if (node.SourceInode is not null)
                {
                    continue;
                }

                var imagePath = BuildImagePath(node);
                if (byPath.TryGetValue(imagePath, out var record))
                {
                    node.SourceInode = record;
                }
            }
        }

        /// <summary>
        /// Collapses duplicate original inodes into hardlink aliases of the first occurrence and
        /// resets the fragment packing cursor.
        /// <para>把重复的原始 inode 合并为首次出现节点的硬链接别名，并重置 fragment 打包游标。</para>
        /// </summary>
        internal void MergeHardlinks()
        {
            var canonicalInodes = new Dictionary<ulong, BuildNode>();
            Ordered.RemoveAll(node =>
            {
                if (node.IsDirectory || node.OriginalInode == 0) return false;
                if (canonicalInodes.TryAdd(node.OriginalInode, node)) return false;
                var canonical = canonicalInodes[node.OriginalInode];
                canonical.LinkCount++;
                Hardlinks.Add((node, canonical));
                return true;
            });
            PackedEnd = 0;
        }

        /// <summary>
        /// Summed size of the payload files collected for this build, used as the denominator of the build progress.
        /// It applies the same predicate as the two places that measure payloads, and it is evaluated before
        /// <see cref="CreatePackedNode"/> and <see cref="PlanMetabox"/> append their synthetic nodes, so every
        /// measured payload belongs to the returned total exactly once.
        /// <para>本次构建收集到的负载文件大小之和，用作构建进度的分母。它与两处负载计量使用同一判定条件，并在
        /// <see cref="CreatePackedNode"/> 与 <see cref="PlanMetabox"/> 追加合成节点之前求值，因此每个被计量的负载
        /// 都恰好属于该总量一次。</para>
        /// </summary>
        /// <returns>The summed payload byte count; zero for a tree without payload files.
        /// <para>负载字节总数；不含负载文件的目录树为零。</para></returns>
        internal long PayloadByteTotal()
        {
            long total = 0;
            foreach (var node in Ordered)
            {
                if (!IsMeasuredPayload(node)) continue;
                total = checked(total + node.Size);
            }

            return total;
        }

        /// <summary>
        /// Reports whether a node's source payload is measured by the build progress: regular files with content,
        /// excluding the synthetic packed and metabox nodes that carry other nodes' bytes.
        /// <para>判断节点的源负载是否纳入构建进度计量：有内容的常规文件，排除承载其他节点字节的合成打包节点与
        /// metabox 节点。</para>
        /// </summary>
        /// <param name="node">Node to classify. <para>待判定的节点。</para></param>
        /// <returns><see langword="true"/> when the node's own bytes are measured.
        /// <para>该节点自身的字节纳入计量时为 <see langword="true"/>。</para></returns>
        private static bool IsMeasuredPayload(BuildNode node)
            => !node.IsDirectory && !node.IsSymlink && !node.IsDevice && !node.IsPacked
                && !node.IsMetaboxContainer && node.Size > 0;

        /// <summary>
        /// Hashes and plans compression for every regular payload in parallel, filling each node's
        /// content hash, compression options, logical cluster size, and pcluster plan. A payload whose compression
        /// plan was accepted is measured here, because this pass has already read its source bytes; the remaining
        /// payloads are measured when they are emitted.
        /// <para>并行地对所有常规负载做哈希与压缩规划，填充每个节点的内容哈希、压缩选项、逻辑簇大小
        /// 与 pcluster 计划。压缩规划被接受的负载在此计量，因为本遍历已经读取了它的源字节；其余负载在写出时计量。</para>
        /// </summary>
        /// <param name="progress">Optional counter receiving each compressed payload's size, so a UI bar follows the
        /// dominant cost of a compressed build instead of the smaller image bytes.
        /// <para>可选计数器，接收每个已压缩规划负载的大小，使界面进度条跟随压缩构建的主要开销而不是较小的镜像字节。</para></param>
        internal void PlanNodePayloads(Core.ExtractionByteCounter? progress = null)
        {
            var staging = EnsureStagedCompression();
            Core.FirmwareParallelism.ForEach(Ordered, node =>
            {
                Options.CancellationToken.ThrowIfCancellationRequested();
                if (node.IsDirectory || node.IsSymlink || node.IsDevice || node.Size <= 0) return;
                if (!node.IsSymlink && !node.IsDevice && Options.Dedup)
                    node.ContentHash = HashFile(node, Options.CancellationToken);
                node.CompressionOptions = node.SourceInode is { Compressed: true } original
                    ? Options.SourceCompressionOptions?.GetValueOrDefault(original.Algorithm) ?? Options : Options;
                node.LogicalClusterSize = node.SourceInode?.LogicalClusterSize ?? BlockSize;
                if (node.SourceInode?.Compressed ?? Options.Compress)
                    node.Pclusters = TryPlanCompressed(node, BlockSize, node.CompressionOptions, staging);
                if (node.Pclusters is not null) progress?.Add(node.Size);
            });
        }

        /// <summary>
        /// Runs the per-node layout pass: directory inline measurement, symlink/inline tails,
        /// compressed cluster bookkeeping, fragment packing, and flat dedup assignment.
        /// <para>逐节点布局遍历：目录 inline 度量、符号链接与 inline 尾部、压缩簇簿记、fragment 打包
        /// 以及平坦数据去重归属。</para>
        /// </summary>
        internal void PlanNodeLayouts()
        {
            foreach (var node in Ordered)
            {
                Options.CancellationToken.ThrowIfCancellationRequested();
                if (node.IsDirectory)
                {
                    PlanDirectoryLayout(node);
                    continue;
                }

                PlanFileLayout(node);
            }
        }

        /// <summary>
        /// Measures one directory node's payload blocks and decides whether its entries stay inline.
        /// <para>度量单个目录节点的负载块数，并决定其目录项是否 inline。</para>
        /// </summary>
        /// <param name="node">Directory node to measure.
        /// <para>待度量的目录节点。</para></param>
        private void PlanDirectoryLayout(BuildNode node)
        {
            var payloadLength = EstimateDirPayloadLength(node);
            if (payloadLength <= BlockSize)
            {
                node.DirBlocks = 1;
                node.DirPayloadSize = payloadLength;
            }
            else
            {
                var layout = MeasureDirLayout(BuildDirEntries(node), BlockSize);
                node.DirBlocks = layout.Blocks;
                node.DirPayloadSize = layout.Size;
            }

            node.DirectoryInline = payloadLength + GetInodeHeaderSize(node) + node.XattrIbody.Length <= BlockSize;
        }

        /// <summary>
        /// Lays out one non-directory node: inline symlink target, compressed cluster counts and
        /// tail packing, fragment packing, inline/flat tails, and flat dedup source binding.
        /// <para>布局单个非目录节点：符号链接 inline 目标、压缩簇计数与尾部打包、fragment 打包、
        /// inline/平坦尾部以及平坦去重来源绑定。</para>
        /// </summary>
        /// <param name="node">File node to lay out; never a directory.
        /// <para>待布局的文件节点；绝不可能是目录。</para></param>
        /// <exception cref="InvalidOperationException">A symlink target's inline inode record exceeds one
        /// block and therefore cannot be read back.
        /// <para>符号链接目标的 inline inode 记录超过一个块，无法被读回。</para></exception>
        private void PlanFileLayout(BuildNode node)
        {
            var isDataFile = !node.IsSymlink && !node.IsDevice;
            if (node.IsSymlink)
            {
                ValidateSymlinkRecordCapacity(node, BlockSize);
                node.InlineData = Encoding.UTF8.GetBytes(node.SymlinkTarget);
                return;
            }

            if (node.Size <= 0)
            {
                return;
            }

            if (node.SourceInode?.Compressed ?? Options.Compress)
            {
                if (node.Pclusters is not null)
                {
                    node.CompressedBlocks = (uint)node.Pclusters.Sum(p => CeilDiv(p.CompressedLength, BlockSize));
                    node.ClusterCount = (uint)CeilDiv(node.Size, node.LogicalClusterSize);
                    node.CompactedIndexes = node.SourceInode?.CompactIndexes ?? Options.UseCompactedIndexes;

                    node.BigPcluster = DeclaredBigPcluster(Options)
                        && (node.CompressionOptions!.Algorithm != ErofsCompressionAlgorithm.Lz4
                            || node.Pclusters.Any(p => p.CompressedLength > BlockSize));
                    var tailLength = node.Pclusters[^1].CompressedLength;
                    if (Options.CompressedTailPacking && DeclaredZtailpacking(Options)
                        && !node.Pclusters[^1].Plain && tailLength < BlockSize
                        && GetInodeRecordSize(node, BlockSize) % BlockSize + tailLength <= BlockSize)
                    {
                        node.InlineCompressedTail = true;
                        node.CompressedBlocks--;
                    }
                    KeepTerminalPclusterResolvable(node, BlockSize);
                    return;
                }
            }

            var wholeBlocks = node.Size / BlockSize;

            if (isDataFile && Options.Fragments && wholeBlocks == 0)
            {
                node.IsFragment = true;
                var hashKey = Options.Dedup && node.ContentHash is not null
                    ? Convert.ToHexString(node.ContentHash)
                    : null;
                if (hashKey is not null && DedupFragments.TryGetValue(hashKey, out var existing))
                {
                    node.FragmentOffset = existing.FragmentOffset;
                    return;
                }

                node.FragmentOffset = AlignFragmentStart(PackedEnd);
                PackedFragments.Add(new PackedFragment { Owner = node, Offset = node.FragmentOffset, Length = node.Size });
                PackedEnd = node.FragmentOffset + node.Size;
                if (hashKey is not null)
                {
                    DedupFragments[hashKey] = node;
                }

                return;
            }

            var remainder = node.Size % BlockSize;
            var inlineFallback = Options.SourceInodes is { Count: > 0 } ? false : Options.TailPacking;
            if (node.SourceInode?.InlineData ?? inlineFallback)
            {
                node.FlatBlocks = wholeBlocks;
                if (remainder > 0)
                {
                    using var fs = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    fs.Position = wholeBlocks * BlockSize;
                    var tail = new byte[remainder];
                    fs.ReadExactly(tail);
                    node.InlineData = tail;
                    if (GetInodeHeaderSize(node) + node.XattrIbody.Length + tail.Length > BlockSize)
                    {
                        node.FlatBlocks++;
                        node.InlineData = [];
                    }
                }
            }
            else
            {
                node.FlatBlocks = remainder > 0 ? wholeBlocks + 1 : wholeBlocks;
            }

            if (isDataFile && Options.Dedup && node.ContentHash is not null && node.FlatBlocks > 0)
            {
                var hashKey = Convert.ToHexString(node.ContentHash);
                if (DedupFlat.TryGetValue(hashKey, out var source) && source.FlatBlocks == node.FlatBlocks)
                {
                    node.DedupSource = source;
                    node.FlatBlocks = source.FlatBlocks;
                }
                else
                {
                    DedupFlat[hashKey] = node;
                }
            }
        }

        /// <summary>
        /// Rewrites the payload's terminal pcluster as an uncompressed cluster when it would otherwise put a
        /// compressed head row on the file's last logical cluster while the inode declares big pclusters.
        /// A kernel derives a head row's physical length from the index row that follows the head
        /// (<c>z_erofs_get_extent_compressedlen</c> in <c>fs/erofs/zmap.c</c>: for a
        /// <c>Z_EROFS_LCLUSTER_TYPE_HEAD1</c> head it skips the <c>1ULL &lt;&lt; lclusterbits</c> shortcut
        /// whenever <c>Z_EROFS_ADVISE_BIG_PCLUSTER_1</c> is declared and then loads <c>m-&gt;lcn + 1</c>), and
        /// that probe is performed even for a pcluster of a single logical cluster whose frame already fills
        /// exactly one block. When the head sits on the last index row there is no following row: the compacted
        /// loader answers <c>-EINVAL</c> because its bound is <c>erofs_iblks(inode)</c>
        /// (<c>compacted_load_cluster_from_disk</c>: <c>if (lcn &gt;= totalidx) return -EINVAL;</c>, and Linux
        /// 5.10 bounds the same probe with <c>DIV_ROUND_UP(inode-&gt;i_size, EROFS_BLKSIZ)</c>), while the full
        /// loader (<c>legacy_load_cluster_from_disk</c>) has no bound at all and decodes the bytes after the
        /// index as a row. Either way the last page fails in <c>z_erofs_read_folio</c> with
        /// <c>failed to read, err [-22]</c>. Only kernels carrying the upstream commit "erofs: refine
        /// z_erofs_get_extent_compressedlen" guard the probe with <c>(lcn &lt;&lt; lclusterbits) &gt;=
        /// i_size</c>; erofs-utils hides the shape with the same guard in <c>lib/zmap.c</c>, so an image that
        /// <c>fsck.erofs</c> extracts byte-exactly can still be unreadable on the 5.10 and 6.1 GKI kernels.
        /// </summary>
        /// <remarks>
        /// An uncompressed head row needs no probe at all: the plain branch fixes the physical length to one
        /// logical cluster (<c>map-&gt;m_plen = 1ULL &lt;&lt; lclusterbits</c>) and
        /// <c>z_erofs_transform_plain</c> in <c>fs/erofs/decompressor.c</c> then copies it verbatim, which is
        /// valid because the tail spans at most one logical cluster. Upstream <c>mkfs.erofs</c> makes exactly
        /// this choice for a final chunk of at most one block regardless of the logical cluster size
        /// (<c>if (!may_inline &amp;&amp; len &lt;= blksz) goto nocompression;</c> in
        /// <c>__z_erofs_compress_one</c>, whose <c>e-&gt;raw</c> extent is then written as
        /// <c>Z_EROFS_LCLUSTER_TYPE_PLAIN</c> with <c>e-&gt;plen = blksz</c>). Where the logical cluster is
        /// larger than one block, that plain window covers the whole logical cluster while the tail itself
        /// occupies one block, which is the same window upstream's own raw tail gets. Because both plans of the
        /// rewrite occupy one block, the row count, the cluster count and the reserved block count stay
        /// identical, so no image size changes. A pcluster whose frame exceeds one block always spans at least
        /// reject it otherwise), so declaring big pclusters for the remaining pclusters stays valid. An inline
        /// compressed tail is left alone because the kernel takes the physical length from
        /// <c>vi-&gt;z_idata_size</c> for it and never reaches the probe.
        /// <para>当尾部 pcluster 会让已声明 big pcluster 的 inode 在文件的最后一个逻辑簇上落下一个压缩头行时，
        /// 把它改写为不压缩的簇。内核的 <c>z_erofs_get_extent_compressedlen</c> 依赖头行之后的索引行推导物理长度：
        /// 只要声明了 <c>Z_EROFS_ADVISE_BIG_PCLUSTER_1</c>，<c>Z_EROFS_LCLUSTER_TYPE_HEAD1</c> 头行就不会走
        /// <c>1ULL &lt;&lt; lclusterbits</c> 捷径，而是加载 <c>m-&gt;lcn + 1</c> 行，哪怕该 pcluster 只占一个逻辑簇、
        /// 其帧恰好占满一个块。头行位于最后一行时其后并没有行：紧凑索引加载器的上界是 <c>erofs_iblks(inode)</c>，
        /// 即 <c>if (lcn &gt;= totalidx) return -EINVAL;</c>（Linux 5.10 用
        /// <c>DIV_ROUND_UP(inode-&gt;i_size, EROFS_BLKSIZ)</c> 设同样的上界）；完整索引加载器
        /// <c>legacy_load_cluster_from_disk</c> 根本没有上界，会把索引之后的字节当作行解码。两种结果都是尾页在
        /// <c>z_erofs_read_folio</c> 里报 <c>failed to read, err [-22]</c>。只有带上游提交 “erofs: refine
        /// z_erofs_get_extent_compressedlen” 的内核才有 <c>(lcn &lt;&lt; lclusterbits) &gt;= i_size</c> 守卫；
        /// erofs-utils 在其 <c>lib/zmap.c</c> 中用同一守卫掩盖了该布局，因此 <c>fsck.erofs</c> 能逐字节解出的镜像
        /// 在 5.10 与 6.1 GKI 内核上仍可能读不出来。</para>
        /// <para>不压缩的头行根本不需要探测：内核对 plain 分支把物理长度钉在一个逻辑簇
        /// （<c>map-&gt;m_plen = 1ULL &lt;&lt; lclusterbits</c>），随后
        /// <c>fs/erofs/decompressor.c</c> 的 <c>z_erofs_transform_plain</c> 原样复制，而尾部至多占一个逻辑簇正好成立。
        /// 上游 <c>mkfs.erofs</c> 对至多一个块的尾部片段正是这样处理，且与逻辑簇大小无关
        /// （<c>__z_erofs_compress_one</c> 里的 <c>if (!may_inline &amp;&amp; len &lt;= blksz) goto nocompression;</c>，
        /// 其 <c>e-&gt;raw</c> extent 随后以 <c>Z_EROFS_LCLUSTER_TYPE_PLAIN</c> 与 <c>e-&gt;plen = blksz</c> 写入）。
        /// 逻辑簇大于一块时，这个 plain 窗口会覆盖整个逻辑簇而尾部本身只占一块，与上游给自己的原样尾部所开的窗口
        /// 一致。改写前后两个规划都只占一个块，
        /// 索引行数、簇数与预留块数都不变，因此镜像大小不变。帧超过一个块的 pcluster 必然跨越至少两个逻辑簇
        /// （两个规划器都会拒绝其他情况），所以其余 pcluster 继续声明 big pcluster 仍然有效。内联压缩尾部保持不变，
        /// 因为内核对它从 <c>vi-&gt;z_idata_size</c> 取得物理长度，不会走到探测。</para>
        /// </remarks>
        /// <param name="node">Compressed file node whose plans are already planned and measured.
        /// <para>已完成 pcluster 规划与计量的压缩文件节点。</para></param>
        /// <param name="blockSize">Filesystem block size.
        /// <para>文件系统块大小。</para></param>
        private static void KeepTerminalPclusterResolvable(BuildNode node, int blockSize)
        {
            var plans = node.Pclusters!;
            var last = plans[^1];
            if (!node.BigPcluster || node.InlineCompressedTail || last.Plain ||
                last.SourceLength > blockSize)
            {
                return;
            }

            var headCluster = last.SourceOffset / node.LogicalClusterSize;
            if (PlanEndCluster(node, plans.Length - 1) != node.ClusterCount ||
                headCluster != node.ClusterCount - 1)
            {
                return;
            }

            plans[^1] = new PclusterPlan
            {
                SourceOffset = last.SourceOffset,
                SourceLength = last.SourceLength,
                CompressedLength = last.SourceLength,
                Plain = true
            };
        }

        /// <summary>
        /// Appends the synthetic packed node covering the shared fragment area when fragments exist.
        /// <para>存在 fragment 时，追加承载共享 fragment 区的合成打包节点。</para>
        /// </summary>
        internal void CreatePackedNode()
        {
            if (PackedFragments.Count > 0)
            {
                PackedNode = new BuildNode
                {
                    IsPacked = true,
                    Size = PackedEnd,
                    FlatBlocks = CeilDiv(PackedEnd, BlockSize),
                    PackedFragments = PackedFragments,
                };
                Ordered.Add(PackedNode);
            }
        }

        /// <summary>
        /// Selects metabox-resident inodes, assigns their slots, and appends the metabox container node.
        /// <para>挑选 metabox 常驻 inode、分配其槽位并追加 metabox 容器节点。</para>
        /// </summary>
        internal void PlanMetabox()
        {
            if (!Options.Metabox)
            {
                return;
            }

            MetaboxResident = [.. Ordered.Where(node => IsMetaboxEligible(node, BlockSize))];
            if (MetaboxResident.Count > 0)
            {
                var metaboxLength = 0L;
                foreach (var resident in MetaboxResident)
                {
                    var recordSize = GetInodeRecordSize(resident, BlockSize);
                    if (metaboxLength % BlockSize + recordSize > BlockSize)
                    {
                        metaboxLength = AlignUp(metaboxLength, BlockSize);
                    }
                    resident.MetaboxSlot = metaboxLength / ErofsSuperblock.CompactInodeSize;
                    resident.IsMetaboxResident = true;
                    metaboxLength = checked(metaboxLength + AlignUp(recordSize, ErofsSuperblock.CompactInodeSize));
                }

                MetaboxNode = new BuildNode
                {
                    IsMetaboxContainer = true,
                    Size = metaboxLength,
                    FlatBlocks = CeilDiv(metaboxLength, BlockSize),
                };
                Ordered.Add(MetaboxNode);
            }
        }

        /// <summary>
        /// Assigns metadata offsets and nids to every node in the ordered list, keeping metabox
        /// residents on their metabox-slot nids.
        /// <para>为有序列表中的每个节点分配元数据偏移与 nid，metabox 常驻节点保留其 metabox 槽位 nid。</para>
        /// </summary>
        /// <returns>The metadata cursor after the last inode record, before block alignment.
        /// <para>最后一条 inode 记录之后、块对齐之前的元数据游标。</para></returns>
        internal long AssignInodeOffsets()
        {
            var metaOff = InodeTableOffset;
            if (MetaboxResident is not null)
            {
                foreach (var resident in MetaboxResident)
                {
                    resident.MetaOffset = -1;
                    resident.Nid = NidMetaboxFlag | checked((uint)resident.MetaboxSlot);
                }
            }

            foreach (var node in Ordered)
            {
                if (node.IsMetaboxResident)
                {
                    continue;
                }

                var recordSize = GetInodeRecordSize(node, BlockSize);
                if (metaOff % BlockSize + recordSize > BlockSize)
                {
                    metaOff = AlignUp(metaOff, BlockSize);
                }

                node.MetaOffset = metaOff;
                node.Nid = (ulong)(metaOff / 32);
                metaOff += AlignUp(recordSize, 32);
            }

            return metaOff;
        }

        /// <summary>
        /// Assigns data block addresses to directories, pcluster plans, and flat nodes; validates the
        /// total block count, aliases hardlink nids, and validates the root nid.
        /// <para>为目录、pcluster 计划与平坦节点分配数据块地址；校验总块数、别名化硬链接 nid 并校验
        /// 根 nid。</para>
        /// </summary>
        /// <param name="metaOff">Metadata cursor returned by <see cref="AssignInodeOffsets"/>.
        /// <para><see cref="AssignInodeOffsets"/> 返回的元数据游标。</para></param>
        /// <returns>The total image block count.
        /// <para>镜像总块数。</para></returns>
        internal long AssignDataAddresses(long metaOff)
        {
            var dataBlock = AlignUp(metaOff, BlockSize) / BlockSize;
            foreach (var node in Ordered)
            {
                if (node.IsDirectory)
                {
                    if (!node.DirectoryInline)
                    {
                        node.DirBlockAddr = dataBlock;
                        dataBlock += node.DirBlocks;
                    }
                }
                else if (node.Pclusters is not null)
                {
                    foreach (var p in node.Pclusters)
                    {
                        p.BlockAddress = (uint)dataBlock;
                        if (!node.InlineCompressedTail || !ReferenceEquals(p, node.Pclusters[^1]))
                            dataBlock += CeilDiv(p.CompressedLength, BlockSize);
                    }
                }
                else if (node.FlatBlocks > 0)
                {
                    if (node.DedupSource is not null)
                    {
                        node.FlatBlockAddr = node.DedupSource.FlatBlockAddr;
                    }
                    else
                    {
                        node.FlatBlockAddr = dataBlock;
                        dataBlock += node.FlatBlocks;
                    }
                }
            }

            var totalBlocks = dataBlock;
            ErofsSuperblock.ValidateBlockCountField(totalBlocks);

            foreach (var (alias, canonical) in Hardlinks) alias.Nid = canonical.Nid;
            ErofsSuperblock.ValidateRootNidField(Root.Nid);
            return totalBlocks;
        }

        /// <summary>
        /// Computes the superblock's incompat feature word. The layout-implied bits stay derived from the
        /// planned layout, so a non-authoritative declaration yields today's word byte for byte; an
        /// authoritative one takes the algorithm baseline from the declaration instead, rejects any layout
        /// the declaration forbids, and keeps every other declared bit so a rebuild neither gains nor loses
        /// a feature silently.
        /// <para>计算超级块的 incompat 特性字。布局隐含位仍由已规划的布局推导，因此非权威声明得到的字与此前
        /// 逐字节一致；权威声明则改从声明读取算法基线，拒绝声明所不允许的布局，并保留其余全部已声明位，使重建
        /// 既不会静默获得也不会静默丢失特性。</para>
        /// </summary>
        /// <returns>The incompat feature mask for the superblock.
        /// <para>超级块使用的 incompat 特性掩码。</para></returns>
        /// <exception cref="NotSupportedException">The planned layout needs a feature the declaration
        /// forbids, or the declaration names a feature the writer cannot re-emit.
        /// <para>已规划布局需要声明不允许的特性，或声明列出了写入方无法重新输出的特性。</para></exception>
        internal uint ComputeFeatureIncompat()
        {
            var featureIncompat = Options.Compress
                ? ErofsSuperblock.IncompatLz4_0Padding | (Options.LegacyCompatibility ? 0u : ErofsSuperblock.IncompatBigPcluster)
                : 0;

            if (ExtraDevices > 0)
            {
                featureIncompat |= ErofsSuperblock.IncompatDeviceTable;
            }

            if (PackedNode is not null)
            {
                if (Options.LegacyCompatibility)
                {
                    throw new NotSupportedException(
                        "LegacyCompatibility images cannot contain fragment or metabox packed nodes.");
                }
                featureIncompat |= ErofsSuperblock.IncompatFragments;
            }
            if (Options.CompressedTailPacking) featureIncompat |= ErofsSuperblock.IncompatZtailpacking;

            if (MetaboxNode is not null)
            {
                if (Options.LegacyCompatibility)
                {
                    throw new NotSupportedException(
                        "LegacyCompatibility images cannot contain fragment or metabox packed nodes.");
                }
                featureIncompat |= ErofsSuperblock.IncompatMetabox;
            }

            return Options.FeaturesAuthoritative ? ApplyDeclaredIncompat(featureIncompat) : featureIncompat;
        }

        /// <summary>
        /// Replaces the derived algorithm baseline with the declared incompat word and validates the
        /// layout-implied bits against it. The writer always right-aligns a compressed frame with leading
        /// zero padding, so <c>lz4_0padding</c> is the single documented bit it may add and the addition is
        /// recorded in <see cref="ErofsOptions.AddedIncompatBits"/> for the caller to report.
        /// <para>用声明的 incompat 字替换推导出的算法基线，并据此校验布局隐含位。写入方总会用前导零填充把压缩帧
        /// 右对齐，因此 <c>lz4_0padding</c> 是唯一允许新增的位，且该新增会记录到
        /// <see cref="ErofsOptions.AddedIncompatBits"/> 供调用方上报。</para>
        /// </summary>
        /// <param name="layoutIncompat">Bits the planned layout requires.
        /// <para>已规划布局所需的位。</para></param>
        /// <returns>The declared word, forced to carry <c>lz4_0padding</c> for compressed output.
        /// <para>声明字，并在压缩输出时强制携带 <c>lz4_0padding</c>。</para></returns>
        private uint ApplyDeclaredIncompat(uint layoutIncompat)
        {
            var declared = Options.Features.IncompatBits;
            var readOnlyNames = ErofsFeatureTable.ReadOnlyIncompatNames(declared);
            if (readOnlyNames.Length > 0)
            {
                throw new NotSupportedException(
                    $"EROFS feature declaration [{ErofsSuperblock.DescribeIncompat(declared)}] names read-only " +
                    $"features '{readOnlyNames}' that the managed writer cannot re-emit.");
            }

            RequireDeclaredBit(layoutIncompat, ErofsSuperblock.IncompatDeviceTable, ErofsFeature.DeviceTable,
                nameof(ErofsOptions.ExtraDeviceCount));
            RequireDeclaredBit(layoutIncompat, ErofsSuperblock.IncompatFragments, ErofsFeature.Fragments,
                nameof(ErofsOptions.Fragments));
            RequireDeclaredBit(layoutIncompat, ErofsSuperblock.IncompatZtailpacking, ErofsFeature.Ztailpacking,
                nameof(ErofsOptions.CompressedTailPacking));
            RequireDeclaredBit(layoutIncompat, ErofsSuperblock.IncompatMetabox, ErofsFeature.Metabox,
                nameof(ErofsOptions.Metabox));

            if (!DeclaredBigPcluster(Options) && RequiresBigPclusterLayout())
            {
                throw new NotSupportedException(
                    $"EROFS feature declaration [{ErofsSuperblock.DescribeIncompat(declared)}] omits " +
                    $"{ErofsFeatureTable.NameOf(ErofsFeature.BigPcluster)} while a planned pcluster spans more than " +
                    $"one block; declare the feature or pin {nameof(ErofsOptions.MaxPclusterSize)} to the block size.");
            }

            var incompat = declared;
            if (Options.Compress && (incompat & ErofsSuperblock.IncompatLz4_0Padding) == 0)
            {
                incompat |= ErofsSuperblock.IncompatLz4_0Padding;
                Options.AddedIncompatBits |= ErofsSuperblock.IncompatLz4_0Padding;
            }

            return incompat;
        }

        /// <summary>
        /// Rejects a layout-implied incompat bit the declaration does not carry, naming the upstream feature
        /// and the layout option that would have to be cleared.
        /// <para>拒绝声明未携带的布局隐含 incompat 位，并指名上游特性以及需要清除的布局选项。</para>
        /// </summary>
        /// <param name="layoutIncompat">Bits the planned layout requires.
        /// <para>已规划布局所需的位。</para></param>
        /// <param name="bit">The layout-implied physical bit.
        /// <para>布局隐含的物理位。</para></param>
        /// <param name="feature">Logical feature used to name the bit in the message.
        /// <para>消息中为该位命名的逻辑特性。</para></param>
        /// <param name="optionName">Layout option whose removal drops the bit.
        /// <para>清除后即可移除该位的布局选项名。</para></param>
        private void RequireDeclaredBit(uint layoutIncompat, uint bit, ErofsFeature feature, string optionName)
        {
            if ((layoutIncompat & bit) == 0 || (Options.Features.IncompatBits & bit) != 0)
            {
                return;
            }

            throw new NotSupportedException(
                $"EROFS layout requires {ErofsFeatureTable.NameOf(feature)} but the declared feature set " +
                $"[{ErofsSuperblock.DescribeIncompat(Options.Features.IncompatBits)}] does not advertise it; " +
                $"clear the {optionName} option or declare the feature.");
        }

        /// <summary>
        /// Reports whether any planned payload would need a pcluster spanning more than one filesystem
        /// block, which is precisely the layout the big-pcluster incompat bit licenses.
        /// <para>判断是否存在需要跨越多个文件系统块的 pcluster 的已规划负载，而这正是 big-pcluster incompat
        /// 位所允许的布局。</para>
        /// </summary>
        /// <returns><see langword="true"/> when a planned frame exceeds one block or its codec is not LZ4.
        /// <para>任一已规划帧超过一个块，或其编解码器不是 LZ4 时为 <see langword="true"/>。</para></returns>
        private bool RequiresBigPclusterLayout()
        {
            foreach (var node in Ordered)
            {
                if (node.Pclusters is null)
                {
                    continue;
                }

                if (node.CompressionOptions!.Algorithm != ErofsCompressionAlgorithm.Lz4)
                {
                    return true;
                }

                foreach (var plan in node.Pclusters)
                {
                    if (plan.CompressedLength > BlockSize)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Fills the superblock structure from the planned layout and options. The compat word is the
        /// derived one constrained by an authoritative declaration, and every derived bit the declaration
        /// omits is recorded in <see cref="ErofsOptions.DroppedCompatBits"/> so the writer never advertises
        /// a compat feature the source lacked.
        /// <para>根据规划完成的布局与选项填充超级块结构。compat 字为受权威声明约束的推导字，声明缺失的每个推导位
        /// 都会记录到 <see cref="ErofsOptions.DroppedCompatBits"/>，使写入方不会 advertise 源镜像缺失的 compat
        /// 特性。</para>
        /// </summary>
        /// <param name="totalBlocks">Total image block count.
        /// <para>镜像总块数。</para></param>
        /// <param name="featureIncompat">Incompat feature mask to advertise.
        /// <para>要声明的 incompat 特性掩码。</para></param>
        /// <returns>The fully populated superblock.
        /// <para>填充完成的超级块。</para></returns>
        internal ErofsSuperblock BuildSuperblock(long totalBlocks, uint featureIncompat)
        {
            var featureCompat = (Options.SuperblockChecksum ? ErofsSuperblock.CompatSbChecksum : 0u)
                | (Options.SourceMtimeFeature ? ErofsSuperblock.CompatMtime : 0u)
                | (Options.SourceXattrFilterFeature ? ErofsSuperblock.CompatXattrFilter : 0u);
            if (Options.FeaturesAuthoritative)
            {
                Options.DroppedCompatBits = featureCompat & ~Options.Features.CompatBits;
                featureCompat &= Options.Features.CompatBits;
            }

            return new ErofsSuperblock
            {
                MagicValue = ErofsSuperblock.Magic,
                BlockSizeBits = (byte)BitOperations.Log2((uint)BlockSize),
                RootNid = (ushort)Root.Nid,
                Inos = (ulong)Ordered.Count,
                BuildTime = Now,
                BuildTimeNsec = Options.BuildTimeNanoseconds,
                BuildTime32 = Options.BuildTimeOffset,
                Blocks = (uint)totalBlocks,
                MetaBlkAddr = 0,
                AvailableComprAlgs = CompressedAlgorithmsField(Options),
                FeatureCompat = featureCompat,
                FeatureIncompat = featureIncompat,
                ExtraDevices = (ushort)ExtraDevices,
                DevtSlotoff = DeviceTableSlotOffset,
                PackedNid = PackedNode?.Nid ?? 0,
                SbExtSlots = MetaboxNode is not null ? (byte)1 : (byte)0,
                MetaBoxNid = MetaboxNode?.Nid ?? 0,
                Uuid = Options.Uuid is { Length: 16 } ? Options.Uuid : Guid.NewGuid().ToByteArray(),
                VolumeName = Options.VolumeName,
                VolumeNameBytes = Options.SourceVolumeNameBytes ?? [],

                DirBlkBits = 0,
            };
        }

        /// <summary>
        /// Writes the superblock buffer, the trailing compression configuration, and the extra device
        /// table when extra devices are present.
        /// <para>写出超级块缓冲、其后的压缩配置，以及存在额外设备时的设备表。</para>
        /// </summary>
        /// <param name="output">Destination stream in image coordinates.
        /// <para>镜像坐标下的目标流。</para></param>
        /// <param name="superblock">Populated superblock to serialize.
        /// <para>待序列化的已填充超级块。</para></param>
        /// <param name="totalBlocks">Total image block count used as the unified device address base.
        /// <para>镜像总块数，作为设备统一地址的基数。</para></param>
        internal void WriteSuperblockAndDevices(Stream output, ErofsSuperblock superblock, long totalBlocks)
        {
            var superblockBuffer = new byte[MetaboxNode is not null
                ? ErofsSuperblock.SuperblockSize + ErofsSuperblock.ExtSlotSize
                : ErofsSuperblock.SuperblockSize];
            superblock.WriteTo(superblockBuffer);

            WriteAt(output, ErofsSuperblock.SuperblockOffset, superblockBuffer, superblockBuffer.Length);
            if (CompressionConfiguration.Length > 0)
            {
                WriteAt(output, ErofsSuperblock.SuperblockOffset + superblockBuffer.Length,
                    CompressionConfiguration, CompressionConfiguration.Length);
            }

            if (ExtraDevices > 0)
            {
                var descriptors = Options.ExtraDeviceImages
                    ?? throw new NotSupportedException("EROFS multi-device output requires ExtraDeviceImages descriptors.");
                var slotBuffer = new byte[ErofsDeviceSlot.Size];
                var uniAddr = (ulong)totalBlocks;
                for (var i = 0; i < ExtraDevices; i++)
                {
                    var descriptor = descriptors[i];
                    var blocks = (ulong)(descriptor.SizeBytes / BlockSize);
                    var tag = new byte[ErofsDeviceSlot.TagSize];
                    System.Text.Encoding.UTF8.GetBytes(descriptor.Tag, tag);
                    var slot = new ErofsDeviceSlot { Tag = tag, Blocks = blocks, UniAddr = uniAddr };
                    slot.WriteTo(slotBuffer);
                    WriteAt(output, DeviceTableOffset + i * ErofsDeviceSlot.Size, slotBuffer, slotBuffer.Length);
                    uniAddr += blocks;
                }
            }
        }

        /// <summary>
        /// Writes each node's data blocks: directory blocks, packed fragments, compressed data,
        /// metabox containers, and flat copies, honouring dedup by skipping alias nodes.
        /// <para>写出每个节点的数据块：目录块、打包 fragment、压缩数据、metabox 容器与平坦复制，
        /// 并依据去重跳过别名节点。</para>
        /// </summary>
        /// <param name="output">Destination stream in image coordinates.
        /// <para>镜像坐标下的目标流。</para></param>
        /// <param name="progress">Optional counter receiving the size of every collected payload that was not
        /// measured by the compression pass, so each payload byte is measured exactly once across the build.
        /// <para>可选计数器，接收未被压缩遍历计量的每个已收集负载的大小，使每个负载字节在整个构建中恰好计量一次。</para></param>
        internal void WriteNodeData(Stream output, Core.ExtractionByteCounter? progress = null)
        {
            foreach (var node in Ordered)
            {
                Options.CancellationToken.ThrowIfCancellationRequested();
                if (node.IsDirectory)
                {
                    if (node.DirBlockAddr >= 0)
                    {
                        WriteDirBlocks(output, node, BlockSize, Options.CancellationToken);
                    }
                }
                else if (node.IsPacked)
                {
                    WritePackedData(output, node, BlockSize, Options.CancellationToken);
                }
                else if (node.Pclusters is not null)
                {
                    WriteCompressedData(output, node, BlockSize, node.CompressionOptions!, StagedCompression!);
                }
                else if (node.IsMetaboxContainer)
                {
                    WriteMetaboxContainer(output, node, BlockSize, Now, MetaboxResident!, Options);
                }
                else if (node.FlatBlockAddr >= 0 && node.FlatBlocks > 0 && node.DedupSource is null)
                {
                    var copyLen = Math.Min(node.FlatBlocks * BlockSize, node.Size);
                    CopyFileRange(output, node, 0, copyLen, node.FlatBlockAddr * BlockSize, Options.CancellationToken);
                }

                if (IsMeasuredPayload(node) && node.Pclusters is null)
                {
                    progress?.Add(node.Size);
                }
            }
        }

        /// <summary>
        /// Writes each non-metabox-resident node's inode record, inline xattrs, and trailing inline
        /// payloads such as directory payloads, fragment headers, and compressed indexes.
        /// <para>写出每个非 metabox 常驻节点的 inode 记录、inline xattr 以及目录负载、fragment 头与
        /// 压缩索引等尾部负载数据。</para>
        /// </summary>
        /// <param name="output">Destination stream in image coordinates.
        /// <para>镜像坐标下的目标流。</para></param>
        internal void WriteInodes(Stream output)
        {
            foreach (var node in Ordered)
            {
                Options.CancellationToken.ThrowIfCancellationRequested();
                if (node.IsMetaboxResident)
                {
                    continue;
                }

                var inode = BuildInode(node, BlockSize, Now, Options);
                var inodeBuffer = new byte[inode.OnDiskSize];
                inode.WriteTo(inodeBuffer);
                WriteAt(output, node.MetaOffset, inodeBuffer, inodeBuffer.Length);

                if (node.XattrIbody.Length > 0)
                {
                    WriteAt(output, node.MetaOffset + inode.OnDiskSize, node.XattrIbody, node.XattrIbody.Length);
                }

                var cursor = node.MetaOffset + inode.OnDiskSize + inode.XattrTotalSize;

                if (node.IsDirectory)
                {
                    if (node.DirectoryInline)
                    {
                        WriteAt(output, cursor, BuildDirPayload(node), (int)EstimateDirPayloadLength(node));
                    }
                }
                else if (node.IsFragment)
                {
                    WriteFragmentHeader(output, node, inode, Options);
                }
                else if (node.Pclusters is not null)
                {
                    WriteCompressedIndexes(output, AlignUp(cursor, 8), node, BlockSize, node.CompressionOptions!);
                }
                else if (node.InlineData.Length > 0)
                {
                    WriteAt(output, cursor, node.InlineData, node.InlineData.Length);
                }
            }
        }

        /// <summary>
        /// Writes the CRC32c superblock checksum into the captured superblock window region when
        /// superblock checksums are enabled. A declaration that omits the compat bit keeps the field zeroed,
        /// so the emitted bytes never carry a checksum the superblock word does not advertise.
        /// <para>启用超级块校验和时，把 CRC32c 超级块校验值写入捕获的超级块窗口区域。声明缺少该 compat 位时该字段
        /// 保持为零，因此写出的字节绝不会带有超级块字未 advertise 的校验和。</para>
        /// </summary>
        /// <param name="output">Destination stream whose captured window holds the superblock bytes.
        /// <para>捕获窗口保存超级块字节的目标流。</para></param>
        internal void WriteSuperblockChecksum(OffsetStream output)
        {
            if (!Options.SuperblockChecksum || !DeclaredSuperblockChecksum(Options))
            {
                return;
            }

            var crcRegion = output.CapturedWindow ?? throw new InvalidOperationException("Superblock checksum window was not captured.");
            var finalCrc = ComputeCrc32C(crcRegion.AsSpan(8), ErofsSuperblockChecksumSeed);
            Span<byte> crcLe = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(crcLe, finalCrc);
            output.Position = ErofsSuperblock.SuperblockOffset + 4;
            output.Write(crcLe);
        }
    }
}