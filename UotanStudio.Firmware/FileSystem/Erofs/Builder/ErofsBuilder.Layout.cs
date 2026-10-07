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
        /// Hashes and plans compression for every regular payload in parallel, filling each node's
        /// content hash, compression options, logical cluster size, and pcluster plan.
        /// <para>并行地对所有常规负载做哈希与压缩规划，填充每个节点的内容哈希、压缩选项、逻辑簇大小
        /// 与 pcluster 计划。</para>
        /// </summary>
        internal void PlanNodePayloads()
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
        private void PlanFileLayout(BuildNode node)
        {
            var isDataFile = !node.IsSymlink && !node.IsDevice;
            if (node.IsSymlink)
            {
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

                    node.BigPcluster = !Options.LegacyCompatibility
                        && (node.CompressionOptions!.Algorithm != ErofsCompressionAlgorithm.Lz4
                            || node.Pclusters.Any(p => p.CompressedLength > BlockSize));
                    var tailLength = node.Pclusters[^1].CompressedLength;
                    if (Options.CompressedTailPacking && !node.Pclusters[^1].Plain && tailLength < BlockSize
                        && GetInodeRecordSize(node, BlockSize) % BlockSize + tailLength <= BlockSize)
                    {
                        node.InlineCompressedTail = true;
                        node.CompressedBlocks--;
                    }
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
        /// Computes the superblock's incompat feature bits, rejecting fragment/metabox nodes under
        /// legacy compatibility.
        /// <para>计算超级块的 incompat 特性位，并在 legacy 兼容模式下拒绝 fragment/metabox 节点。</para>
        /// </summary>
        /// <returns>The incompat feature mask for the superblock.
        /// <para>超级块使用的 incompat 特性掩码。</para></returns>
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

            return featureIncompat;
        }

        /// <summary>
        /// Fills the superblock structure from the planned layout and options.
        /// <para>根据规划完成的布局与选项填充超级块结构。</para>
        /// </summary>
        /// <param name="totalBlocks">Total image block count.
        /// <para>镜像总块数。</para></param>
        /// <param name="featureIncompat">Incompat feature mask to advertise.
        /// <para>要声明的 incompat 特性掩码。</para></param>
        /// <returns>The fully populated superblock.
        /// <para>填充完成的超级块。</para></returns>
        internal ErofsSuperblock BuildSuperblock(long totalBlocks, uint featureIncompat)
        {
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
                FeatureCompat = (Options.SuperblockChecksum ? ErofsSuperblock.CompatSbChecksum : 0u)
                    | (Options.SourceMtimeFeature ? ErofsSuperblock.CompatMtime : 0u)
                    | (Options.SourceXattrFilterFeature ? ErofsSuperblock.CompatXattrFilter : 0u),
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
        internal void WriteNodeData(Stream output)
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
                    WriteMetaboxContainer(output, node, BlockSize, Now, MetaboxResident!, Options.LegacyCompatibility);
                }
                else if (node.FlatBlockAddr >= 0 && node.FlatBlocks > 0 && node.DedupSource is null)
                {
                    var copyLen = Math.Min(node.FlatBlocks * BlockSize, node.Size);
                    CopyFileRange(output, node, 0, copyLen, node.FlatBlockAddr * BlockSize, Options.CancellationToken);
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

                var inode = BuildInode(node, BlockSize, Now, Options.LegacyCompatibility);
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
        /// superblock checksums are enabled.
        /// <para>启用超级块校验和时，把 CRC32c 超级块校验值写入捕获的超级块窗口区域。</para>
        /// </summary>
        /// <param name="output">Destination stream whose captured window holds the superblock bytes.
        /// <para>捕获窗口保存超级块字节的目标流。</para></param>
        internal void WriteSuperblockChecksum(OffsetStream output)
        {
            if (!Options.SuperblockChecksum)
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