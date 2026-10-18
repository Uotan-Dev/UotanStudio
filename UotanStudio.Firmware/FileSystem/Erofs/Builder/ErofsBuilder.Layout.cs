using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes;
using UotanStudio.Hash.Digests;
using ErofsInode = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsInode;

namespace UotanStudio.Firmware.FileSystem.Erofs.Builder;

/// <summary>
/// EROFS builder part that orchestrates the image layout phases extracted from
/// <see cref="BuildImage"/>: layout origin computation, edit plan binding,
/// hardlink merging, payload planning, inode/data offset assignment, and the final write-out.
/// <para>EROFS 构建器的组成部分，承载从 <see cref="BuildImage"/> 拆出的镜像布局阶段：
/// 布局起点计算、编辑计划绑定、硬链接合并、负载规划、inode 与数据偏移分配以及最终写出。</para>
/// </summary>
public sealed partial class ErofsBuilder
{
    /// <summary>
    /// Shared mutable context threaded through the image build phases; it carries the values that
    /// were locals of the former monolithic <see cref="BuildImage"/> body.
    /// <para>贯穿镜像构建各阶段共享的上下文；承载原先单体 <see cref="BuildImage"/> 方法体
    /// 中的局部状态。</para>
    /// </summary>
    private sealed class ImageBuildContext(ErofsInode root, ErofsOptions options)
    {
        public ErofsInode Root { get; } = root;

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

        /// <summary>Block count the extra devices contribute, so <c>s_blocks</c> spans the whole logical
        /// filesystem rather than the primary image alone. erofs-utils seeds its running total with the primary
        /// block count and adds each device's blocks, and the kernel and the size-basis report read the result.
        /// <para>额外设备贡献的块数，使 <c>s_blocks</c> 覆盖整个逻辑文件系统而不只是主镜像。erofs-utils 以主镜像
        /// 块数作为累计初值并逐设备相加，内核与容量基线报告都会读取该结果。</para>
        /// </summary>
        public long ExtraDeviceBlocks => ExtraDevices == 0 || Options.ExtraDeviceImages is not { } images
            ? 0
            : images.Take(ExtraDevices).Sum(image => (long)(image.SizeBytes / BlockSize));

        /// <summary>Image offset where the inode metadata table starts.
        /// <para>inode 元数据表在镜像中的起始偏移。</para></summary>
        public long InodeTableOffset { get; init; }

        /// <summary>
        /// Whether this build places the inode metadata in a zone of its own. The layout is inherited from
        /// the preserved source image's <c>s_meta_blkaddr</c>; a fresh build keeps the metadata at the head
        /// of the image because it has no recorded zone to reproduce.
        /// <para>本次构建是否把 inode 元数据放进独立区块。该布局继承自保留式源镜像的
        /// <c>s_meta_blkaddr</c>；全新构建没有可复现的已记录区块，因此把元数据留在镜像头部之后。</para>
        /// </summary>
        internal bool Metazone => Options.SourceMetaBlkAddr != 0;

        /// <summary>Image offset of the extra device table; zero when there are no extra devices.
        /// <para>额外设备表的镜像偏移；无额外设备时为 0。</para></summary>
        public long DeviceTableOffset { get; init; }

        /// <summary>
        /// Absolute byte offset where the separate metadata zone starts, or zero while the inode metadata stays at the head of the image. A preserved rebuild of a source with a nonzero <c>s_meta_blkaddr</c> places the inode table and its index tables here and publishes the block number back through the superblock.
        /// <para>独立元数据区的起始绝对字节偏移；inode 元数据留在镜像头部时为零。对声明了非零 <c>s_meta_blkaddr</c> 的源做保留式重建时，inode 表及其索引表置于此处，并通过超级块发布该块号。</para>
        /// </summary>
        public long MetaZoneByteOffset { get; private set; }

        /// <summary>s_devt_slotoff value: the device table offset in device-slot units.
        /// <para>s_devt_slotoff 值：以设备槽为单位的设备表偏移。</para></summary>
        public ushort DeviceTableSlotOffset { get; init; }

        /// <summary>Compression configuration bytes written right after the superblock (or metabox slot).
        /// <para>写在超级块（或 metabox 槽）之后的压缩配置字节。</para></summary>
        public byte[] CompressionConfiguration { get; init; } = [];

        /// <summary>Pre-order list of all collected nodes; packed and metabox nodes are appended later.
        /// <para>全部收集节点的先序列表；打包节点与 metabox 节点稍后追加。</para></summary>
        public List<ErofsInode> Ordered { get; } = [];

        /// <summary>Total image block count, published after address assignment and consumed by the
        /// superblock writer to decide whether the wide 48-bit fields are required.
        /// <para>镜像总块数，在地址分配后公开，并由超级块写入方据此判断是否需要 48 位宽字段。</para></summary>
        public long TotalBlocks { get; set; }

        /// <summary>Whether any planned inode stores its compressed index as the variable-size extent array.
        /// The extent form is only valid under a superblock that declares <c>48bit</c>, so this flag is what
        /// makes the derived incompat word advertise that bit.
        /// <para>是否存在已规划 inode 把压缩索引存成可变长度 extent 数组。extent 形态只在声明 <c>48bit</c> 的
        /// 超级块下有效，因此该标志就是推导 incompat 字时置上该位的依据。</para></summary>
        public bool HasExtentIndexes { get; set; }

        /// <summary>Long-prefix records the image carries, merged from every node that registered one; empty
        /// when every xattr name matched a built-in short prefix.
        /// <para>镜像携带的长前缀记录，由所有登记过记录的节点合并而来；全部 xattr 名称命中内置短前缀时为空表。</para></summary>
        public List<(byte BaseIndex, string Infix)> XattrPrefixRecords { get; } = [];

        /// <summary>Serialized long-prefix table bytes, or <see langword="null"/> while no node registered a
        /// record. <para>序列化后的长前缀表字节；尚无节点登记记录时为 <see langword="null"/>。</para></summary>
        public byte[]? XattrPrefixTable { get; private set; }

        /// <summary>Image byte offset the serialized prefix table occupies, or <c>-1</c> while there is none.
        /// <para>序列化后的前缀表所在镜像字节偏移；无前缀表时为 <c>-1</c>。</para></summary>
        public long XattrPrefixByteOffset { get; set; } = -1;

        /// <summary>Blocks reserved at the start of the data area for the prefix table.
        /// <para>前缀表在数据区起始处预留的块数。</para></summary>
        public long XattrPrefixBlocks { get; set; }

        /// <summary>Shared xattr entries the image carries, indexed by the id the ibody slots name.
        /// <para>镜像携带的共享 xattr 条目，按 ibody 槽位所指名的 id 索引。</para></summary>
        public List<byte[]> SharedXattrs { get; } = [];

        /// <summary>Block address of the shared xattr area, or <c>-1</c> while the image carries none.
        /// <para>共享 xattr 区的块地址；镜像未携带时为 <c>-1</c>。</para></summary>
        public long SharedXattrBlockAddr { get; set; } = -1;

        /// <summary>Blocks reserved for the shared xattr area.
        /// <para>共享 xattr 区预留的块数。</para></summary>
        public long SharedXattrBlocks { get; set; }

        /// <summary>Byte offset of the shared xattr area inside the metabox container, or <c>-1</c> when the
        /// area lives in a data block instead. <para>共享 xattr 区在 metabox 容器内的字节偏移；该区位于
        /// 数据块中时为 <c>-1</c>。</para></summary>
        public long SharedXattrMetaboxOffset { get; set; } = -1;

        /// <summary>Whether a chunk-based inode's start block outgrew the 4-byte chunk index, which forces
        /// both the wide chunk form and the superblock's own <c>48bit</c> feature.
        /// <para>是否存在某个 chunk-based inode 的首块超出 4 字节 chunk 索引的表达范围，这会同时强制宽
        /// chunk 形态与超级块自身的 <c>48bit</c> 特性。</para></summary>
        public bool ChunkIndexRequires48Bit { get; set; }

        /// <summary>Whether the planned layout contains at least one chunk-based inode, which is what the
        /// <c>chunked_file</c> incompat bit advertises. The writer derives the bit from the plan instead of from
        /// an option, and a declaration that carries it without any such inode fails loudly.
        /// <para>已规划布局是否至少包含一个 chunk-based inode，这正是 <c>chunked_file</c> incompat 位所
        /// advertise 的内容。写入方从布局而非选项推导该位，而声明携带该位却没有任何此类 inode 时会显式失败。</para></summary>
        public bool HasChunkBasedLayout { get; set; }

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

        public List<(ErofsInode Alias, ErofsInode Canonical)> Hardlinks { get; } = [];

        public List<ErofsPackedFragment> PackedFragments { get; } = [];

        /// <summary>End offset (exclusive) of the fragment packing area.
        /// <para>fragment 打包区的结束偏移（不含）。</para></summary>
        public long PackedEnd { get; set; }

        public Dictionary<string, ErofsInode> DedupFlat { get; } = new();

        public Dictionary<string, ErofsInode> DedupFragments { get; } = new();

        /// <summary>Synthetic node backing the shared fragment packing area; null when unused.
        /// <para>承载共享 fragment 打包区的合成节点；未使用时为 null。</para></summary>
        public ErofsInode? PackedNode { get; private set; }

        /// <summary>Nodes chosen as metabox-resident inodes; non-null once metabox planning ran.
        /// <para>被选为 metabox 常驻 inode 的节点；metabox 规划执行后非 null。</para></summary>
        public List<ErofsInode>? MetaboxResident { get; private set; }

        /// <summary>Synthetic node backing the metabox container; null when metabox is unused.
        /// <para>承载 metabox 容器的合成节点；未启用 metabox 时为 null。</para></summary>
        public ErofsInode? MetaboxNode { get; private set; }

        /// <summary>Logical byte length of the assembled metabox container body, or zero before
        /// <see cref="PlanMetabox"/> measured it.
        /// <para>装配完成的 metabox 容器本体的逻辑字节长度；<see cref="PlanMetabox"/> 度量之前为零。</para></summary>
        public long MetaboxBodyLength { get; private set; }

        /// <summary>Scratch file holding the assembled metabox container body once a build encodes it.
        /// The container is a synthetic node with no source file of its own, while the compression planners
        /// read a node's payload from <see cref="ErofsInode.FullPath"/>, so the body is materialized here and
        /// the container temporarily names this path. Null while the container stays verbatim.
        /// <para>本次构建对 metabox 容器本体编码后承载其装配结果的暂存文件。容器是没有自身源文件的合成节点，
        /// 而压缩规划器按 <see cref="ErofsInode.FullPath"/> 读取节点负载，因此把本体落到这里并让容器临时指向该
        /// 路径。容器按明文存放时为 null。</para>
        /// </summary>
        public string? MetaboxBodyPath { get; private set; }

        /// <summary>Records the scratch file an encoded metabox container body was materialized into.
        /// <para>记录编码后的 metabox 容器本体所落入的暂存文件。</para>
        /// </summary>
        /// <param name="path">Path the body was written to.
        /// <para>本体写入的路径。</para></param>
        public void SetMetaboxBodyPath(string path) => MetaboxBodyPath = path;

        /// <summary>
        /// Deletes the metabox container body scratch file, if one was created. It is a build-time handoff
        /// between the address pass and the write pass and carries no durable state, so it is removed on every
        /// exit path including a failed build.
        /// <para>删除 metabox 容器本体的暂存文件（若已创建）。它只是地址分配与写出阶段之间的构建期交接物，
        /// 不承载持久状态，因此在包括构建失败在内的所有退出路径上都会被删除。</para>
        /// </summary>
        public void DeleteMetaboxBodyFile()
        {
            if (MetaboxBodyPath is not { } path)
            {
                return;
            }

            MetaboxBodyPath = null;
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        /// <summary>Creates the build context: captures the superblock checksum window and computes the layout origin (extra device table, compression configuration, and inode table start offsets).
        /// <para>创建构建上下文：捕获超级块校验和窗口并计算布局起点（额外设备表、压缩配置与 inode 表 的起始偏移）。</para></summary>
        /// <param name="root">Root of the collected node tree.<para>收集到的节点树根节点。</para></param>
        /// <param name="options">Validated build options.<para>经校验的构建选项。</para></param>
        /// <param name="output">Offset-wrapped destination stream the window is captured on.
        /// <para>捕获窗口所用的偏移包装输出流。</para></param>
        /// <returns>The initialized build context.<para>初始化完成的构建上下文。</para></returns>
        public static ImageBuildContext Create(ErofsInode root, ErofsOptions options, OffsetStream output)
        {
            var blockSize = options.BlockSize;
            var now = options.BuildTime ?? (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            output.CaptureWindow(ErofsSuperblock.SuperblockOffset,
                blockSize > ErofsSuperblock.SuperblockOffset ? blockSize - ErofsSuperblock.SuperblockOffset : blockSize);

            var extraDevices = options.ExtraDeviceCount;
            var compressionConfiguration = BuildCompressionConfiguration(options);
            var compressionConfigurationOffset = InodeTableStart
                + ResolveExtendedSlotCount(options, options.Metabox) * ErofsSuperblock.ExtSlotSize;
            long inodeTableStart;
            long devtOffset;
            ushort devtSlotOff;
            if (extraDevices > 0)
            {
                devtOffset = AlignUp(compressionConfigurationOffset + compressionConfiguration.Length, blockSize);
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
        /// Resolves how many 16-byte extended superblock slots the emitted superblock carries
        /// (<c>s_sb_extslots</c>). The recorded source count is the authority, because the compression
        /// configuration area starts right behind that many slots; a recorded count is never silently lowered,
        /// and one slot is kept whenever the build carries a metabox container, which is exactly when it
        /// declares the metabox feature and therefore needs <c>s_metabox_nid</c> to live inside the first
        /// extended slot. The layout origin is computed before any container exists, so it passes the options'
        /// metabox request instead; without a recorded count both callers reproduce the repository's existing
        /// default of one slot for a metabox build and none otherwise.
        /// <para>解析写出的超级块携带多少 16 字节扩展槽位（<c>s_sb_extslots</c>）。记录下来的源计数是权威，
        /// 因为压缩配置区紧跟在同样多的槽位之后；记录的计数绝不会被静默降低，而当构建携带 metabox 容器时至少保留
        /// 一个槽位，因为这正是它声明 metabox 特性、从而需要让 <c>s_metabox_nid</c> 位于第一个扩展槽内的时刻。
        /// 布局起点在任何容器存在之前就已算出，因此那里传入选项的 metabox 请求；未记录计数时两个调用点都复现仓库
        /// 既有默认：metabox 构建一个槽位，其余零个。</para>
        /// </summary>
        /// <param name="options">Validated build options carrying the recorded count.
        /// <para>携带已记录计数的经校验构建选项。</para></param>
        /// <param name="metaboxDeclared">Whether this build carries the metabox container at the call site, or
        /// requests the metabox feature while the layout origin is computed.
        /// <para>该调用点上本次构建是否携带 metabox 容器，或在计算布局起点时是否请求 metabox 特性。</para></param>
        /// <returns>The slot count to publish and to reserve in front of the compression configuration.
        /// <para>要发布的槽位数量，同时也是压缩配置之前预留的长度。</para></returns>
        private static byte ResolveExtendedSlotCount(ErofsOptions options, bool metaboxDeclared)
        {
            var recorded = options.SourceSbExtSlots ?? (byte)0;
            var required = metaboxDeclared ? (byte)1 : (byte)0;
            return Math.Max(recorded, required);
        }

        /// <summary>
        /// Resolves the emitted <c>s_dirblkbits</c> from the recorded source value. The value is the log2 of the
        /// directory block size, and this writer packs directory payloads into filesystem blocks, so only a
        /// recorded geometry equal to the filesystem block size can be honoured; any other recorded geometry is
        /// refused loudly instead of being replaced by a default, because publishing the recorded value would
        /// describe blocks the payload does not follow and publishing zero would silently change the geometry.
        /// <para>由已记录的源取值解析写出的 <c>s_dirblkbits</c>。该值是目录块大小的 log2，而本写入方把目录负载
        /// 打包进文件系统块，因此只有与文件系统块大小相等的已记录几何才能兑现；其它任何已记录几何都会显式拒绝，
        /// 而不是替换为默认值，因为发布记录值会描述负载并未遵循的块，而发布零会静默改变几何。</para>
        /// </summary>
        /// <returns>The recorded log2 when it names the filesystem block size, otherwise zero for a source that
        /// recorded none. <para>已记录值指名文件系统块大小时返回该值，源未记录时返回零。</para></returns>
        /// <exception cref="FileSystemUnsupportedFeatureException">The recorded directory block size differs from
        /// the filesystem block size this build lays directories out with.
        /// <para>已记录的目录块大小与本构建布局目录所用的文件系统块大小不同。</para></exception>
        private byte ResolveDirBlkBits()
        {
            if (Options.SourceDirBlkBits is not { } recorded || recorded == 0)
            {
                return 0;
            }

            var recordedBlockSize = recorded < 31 ? 1L << recorded : 0L;
            if (recordedBlockSize != BlockSize)
            {
                throw new FileSystemUnsupportedFeatureException(
                    $"EROFS preserved rebuild: the source recorded s_dirblkbits={recorded} "
                    + $"({recordedBlockSize}-byte directory blocks), but directory payloads are packed into "
                    + $"{BlockSize}-byte filesystem blocks, so the recorded directory geometry cannot be "
                    + "reproduced; clear the recorded value to rebuild at the filesystem block size.");
            }

            return recorded;
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
            if (Rebuilding.FileSystemEditPlan.Current is { } editPlan)
            {
                BindEditPlan(editPlan);
            }
            else
            {
                BindSourceInodesByPath();
            }

            foreach (var node in Ordered)
            {
                var timestamp = node.SourceInode?.CTime ?? (node.CTime is > 0 and <= long.MaxValue ? node.CTime : Now);
                var nanoseconds = node.SourceInode?.CTimeNsec ?? node.CTimeNsec;
                node.RequiresExtendedTime = NeedsExtendedInodeTime(timestamp, Now, nanoseconds);
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
        private void BindEditPlan(Rebuilding.FileSystemEditPlan editPlan)
        {
            foreach (var node in Ordered)
            {
                Options.CancellationToken.ThrowIfCancellationRequested();
                var entry = editPlan.Get(node.FullPath);
                node.OriginalInode = entry.Inode;
                node.SourceInode = Options.SourceInodes?.GetValueOrDefault(entry.Inode);
                node.SecondaryAlgorithm = node.SourceInode?.SecondaryAlgorithm;
                if (entry.Metadata?.ModificationTime is { } time)
                {
                    if (node.SourceInode is { } sourceInode)
                    {
                        var copy = sourceInode.Clone();
                        copy.CTime = checked((ulong)time.Seconds);
                        copy.CTimeNsec = time.Nanoseconds;
                        node.SourceInode = copy;
                    }
                    else
                    {
                        node.SourceInode = CreateSyntheticSourceInode(entry.Path, time.Seconds, time.Nanoseconds);
                    }
                }
                node.SecondaryAlgorithm = node.SourceInode?.SecondaryAlgorithm;

                if ((entry.Mode & InodeTypeBits.FormatMask) == InodeTypeBits.RegularFile)
                {
                    node.FullPath = editPlan.ContentSource(node.FullPath);
                    node.Size = ToInodeSize(new FileInfo(node.FullPath).Length);
                }

                node.Name = Rebuilding.FileSystemEditPlan.Name(entry.Path);
                node.Uid = entry.Uid;
                node.Gid = entry.Gid;
                node.ModeOverride = entry.Mode;
                node.HasModeOverride = true;
                node.IsSymlink = (entry.Mode & InodeTypeBits.FormatMask) == InodeTypeBits.SymbolicLink;
                if (node.IsSymlink)
                {
                    node.SymlinkTarget = entry.SymlinkTarget ?? "";
                    node.Size = (ulong)Encoding.UTF8.GetByteCount(node.SymlinkTarget);
                }

                node.IsDevice = (entry.Mode & InodeTypeBits.FormatMask) is InodeTypeBits.Fifo or InodeTypeBits.CharDevice or InodeTypeBits.BlockDevice or InodeTypeBits.Socket;
                node.DeviceMode = entry.Mode;
                node.DeviceRdev = entry.DeviceId;
                if (node.IsDevice)
                {
                    node.Size = 0UL;
                }

                if (entry.Xattrs is { Count: > 0 })
                {
                    List<ErofsXattrEntry> planEntries =
                        [.. entry.Xattrs.Select(p => new ErofsXattrEntry { Name = p.Key, Value = p.Value })];
                    BindXattrEncoding(node,
                        ErofsXattr.Encode(planEntries, PrefixSeed(), NameFilterRequested),
                        planEntries);
                }
                else if (node.SourceInode is { XattrIbody.Length: > 0 } sourceInode)
                {
                    InheritSourceXattrIbody(node, sourceInode);
                }
                else
                {
                    node.XattrIbody = [];
                    node.XattrEntries = null;
                }
            }
        }

        /// <summary>
        /// Gives a planned node the attribute set the source image recorded for it. The body is carried
        /// verbatim because it is index-relative to the source long-prefix table and may reference a shared
        /// attribute id rather than list entries; the prefix table and ishare prefix are themselves replayed,
        /// so re-encoding would corrupt a body that is already valid. The entries are decoded from the body so
        /// the shared-attribute pass still sees the set, and a shared body keeps its id instead of being
        /// re-shared.
        /// <para>把源镜像为该节点记录的属性集交给规划后的节点。该体按原样携带，因为它相对于源长前缀表索引，且可能
        /// 引用共享属性 id 而非列出条目；前缀表与 ishare 前缀本身已被重放，因此重新编码会破坏本已有效的体。条目由该体
        /// 解码得出，使共享属性遍历仍能看到该集合；共享体则保留其 id 而不再被重新共享。</para>
        /// </summary>
        /// <param name="node">Planned node to extend. <para>待扩充的规划节点。</para></param>
        /// <param name="sourceInode">Source inode carrying the recorded body.
        /// <para>携带已记录体的源 inode。</para></param>
        private void InheritSourceXattrIbody(ErofsInode node, ErofsInode sourceInode)
        {
            node.XattrIbody = [.. sourceInode.XattrIbody];
            node.XattrICount = sourceInode.XattrICount;
            node.SharedXattrId = sourceInode.SharedXattrId;
            node.XattrEntries = sourceInode.XattrEntries is { Count: > 0 } resolved
                ? [.. resolved]
                : sourceInode.SharedXattrId >= 0
                    ? null
                    : [.. ErofsXattr.ParseInline(node.XattrIbody, node.XattrICount, Options.SourceXattrPrefixes)];
        }

        /// <summary>
        /// Builds the source strategy an edit plan entry needs when it carries a modification time but the
        /// source image recorded no inode for it: an extended inode whose payload follows the options'
        /// compression choice, with no fragment placement.
        /// <para>为携带修改时间但源镜像未记录 inode 的编辑计划条目构建源策略：一个扩展 inode，其负载按选项的
        /// 压缩选择存放，不采用 fragment 放置。</para>
        /// </summary>
        /// <param name="path">Image-relative path recorded as the synthetic source path.
        /// <para>记录为合称源路径的镜像相对路径。</para></param>
        /// <param name="seconds">Modification time in Unix epoch seconds.
        /// <para>以 Unix 纪元秒表示的修改时间。</para></param>
        /// <param name="nanoseconds">Sub-second modification time.
        /// <para>亚秒修改时间。</para></param>
        /// <returns>The synthetic source node. <para>合称源节点。</para></returns>
        private ErofsInode CreateSyntheticSourceInode(string path, long seconds, uint nanoseconds)
        {
            var layout = Options.Compress
                ? Options.UseCompactedIndexes ? ErofsDataLayout.CompactIndexes : ErofsDataLayout.Compressed
                : ErofsDataLayout.Flat;
            return new ErofsInode
            {
                Layer = ErofsInodeLayer.Source,
                IFormat = ErofsInode.FlagExtended,
                CTime = checked((ulong)seconds),
                CTimeNsec = nanoseconds,
                DataLayout = layout,
                Idata = layout == ErofsDataLayout.Flat
                    ? new ErofsFlatIdata()
                    : new ErofsCompressedIdata
                    {
                        CompactedIndexes = layout == ErofsDataLayout.CompactIndexes,
                        LogicalClusterSize = BlockSize,
                    },
                SourcePath = path,
                SourceCompressed = Options.Compress,
                SourceAlgorithm = Options.Algorithm,
                SourceFragmentShape = ErofsFragmentShape.None,
            };
        }

        /// <summary>
        /// Path a captured document publishes for the image root. It is the key a mounted filesystem and the
        /// source parser's entry enumeration use, while <see cref="BuildImagePath"/> names the root with the
        /// empty string, so the two have to be read as one key.
        /// <para>采集文档为镜像根发布的路径。它是挂载后的文件系统与源解析器条目枚举所用的键，而
        /// <see cref="BuildImagePath"/> 以空字符串命名根，因此两者必须按同一个键读取。</para>
        /// </summary>
        private const string CapturedRootPath = ".";

        /// <summary>
        /// Binds source inode records by image-relative path for nodes without an already bound record. The
        /// root is also matched through <see cref="CapturedRootPath"/>, because that is the key its record is
        /// published under; without the alias the root record never binds and every dimension it carries,
        /// including <c>i_ino</c>, keeps the writer's default instead of the source's value.
        /// <para>对尚无源 inode 记录的节点，按镜像相对路径绑定源 inode 记录。根也经
        /// <see cref="CapturedRootPath"/> 匹配，因为其记录就发布在该键之下；缺少该别名时根记录永远绑定不上，
        /// 它携带的每个维度（含 <c>i_ino</c>）都会保留写入方默认值而非源镜像的取值。</para>
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
                if (!byPath.TryGetValue(imagePath, out var record) && imagePath.Length == 0)
                {
                    byPath.TryGetValue(CapturedRootPath, out record);
                }

                if (record is not null)
                {
                    node.SourceInode = record;
                    node.SecondaryAlgorithm = record.SecondaryAlgorithm;
                    InheritSourceXattrIbody(node, record);
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
            var canonicalInodes = new Dictionary<ulong, ErofsInode>();
            Ordered.RemoveAll(node =>
            {
                if (node.IsDirectory || node.OriginalInode == 0) return false;
                if (canonicalInodes.TryAdd(node.OriginalInode, node)) return false;
                var canonical = canonicalInodes[node.OriginalInode];
                canonical.Nlink++;
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
                total = checked(total + (long)node.Size);
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
        private static bool IsMeasuredPayload(ErofsInode node)
            => !node.IsDirectory && !node.IsSymlink && !node.IsDevice && !node.IsPacked
                && !node.IsMetaboxContainer && node.Size > 0;

        /// <summary>
        /// Whether this build reuses whole files by content hash. That is the repository's existing pass and
        /// stays gated on <see cref="ErofsOptions.Dedup"/>, but only while
        /// <see cref="ErofsOptions.DedupeMode"/> selects the whole-file comparison: the two modes compare
        /// different objects, so running both would make the reused set depend on the pass order.
        /// <para>本次构建是否按内容哈希复用整文件。这是仓库既有的处理并仍由 <see cref="ErofsOptions.Dedup"/>
        /// 门控，但仅当 <see cref="ErofsOptions.DedupeMode"/> 选择整文件比较时成立：两种模式比较的对象不同，
        /// 同时运行会让复用集合取决于处理顺序。</para>
        /// </summary>
        private bool WholeFileDedupEnabled
            => Options.Dedup && Options.DedupeMode == ErofsDedupeMode.WholeFile;

        /// <summary>
        /// Whether this build reuses encoded pclusters by frame hash. The dimension is expressed by
        /// <see cref="ErofsOptions.DedupeMode"/> alone, so switching it on never changes a build that leaves
        /// the mode at its default.
        /// <para>本次构建是否按帧哈希复用编码后的 pcluster。该维度仅由
        /// <see cref="ErofsOptions.DedupeMode"/> 表达，因此启用它绝不会改变把模式留在默认值的构建。</para>
        /// </summary>
        private bool PclusterDedupEnabled
            => Options.Compress && Options.DedupeMode == ErofsDedupeMode.Pcluster;

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
            var hints = ErofsCompressHints.Compile(Options.CompressHints, Options);
            using var lz4States = new ErofsLz4StatePool();
            Core.FirmwareParallelism.ForEach(Ordered, node =>
            {
                Options.CancellationToken.ThrowIfCancellationRequested();
                if (node.IsDirectory || node.IsSymlink || node.IsDevice || node.Size <= 0) return;
                if (!node.IsSymlink && !node.IsDevice && WholeFileDedupEnabled)
                    node.ContentHash = HashFile(node, Options.CancellationToken);
                node.CompressionOptions = node.SourceInode is { SourceCompressed: true } original
                    ? Options.SourceCompressionOptions?.GetValueOrDefault(original.SourceAlgorithm) ?? Options : Options;
                node.LogicalClusterSize = node.SourceInode?.LogicalClusterSize ?? BlockSize;
                ErofsCompressHints.Match hint = default;
                var hinted = !hints.IsEmpty && hints.TryMatch(BuildImagePath(node), out hint);
                var chunkBased = Options.ChunkSize > 0
                    || node.SourceInode?.DataLayout == ErofsDataLayout.ChunkBased;
                if (!chunkBased && (!hinted || !hint.Disabled) && (node.SourceInode?.SourceCompressed ?? Options.Compress))
                {
                    var nodeOptions = hinted
                        ? node.CompressionOptions.WithCompressHint(hint.PclusterSize, hint.Algorithm)
                        : node.CompressionOptions;
                    node.CompressionOptions = nodeOptions;
                    node.Pclusters = TryPlanCompressed(node, BlockSize, nodeOptions, staging, lz4States);
                }
                if (node.Pclusters is not null) progress?.Add((int)node.Size);
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

                node.InodeFormat = Options.InodeFormat;

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
        private void PlanDirectoryLayout(ErofsInode node)
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
        /// Plans the chunk-based layout (<c>EROFS_INODE_CHUNK_BASED</c>) for a regular file whose source
        /// inode recorded a chunk size, or for a fresh file when the build requested one through
        /// <see cref="ErofsOptions.ChunkSize"/>. The chunk geometry and the two high bits of the recorded chunk
        /// format word (<c>EROFS_CHUNK_FORMAT_INDEXES</c> and <c>EROFS_CHUNK_FORMAT_48BIT</c>) come from the
        /// source inode first and from the address range or the option otherwise, so a recorded 8-byte index
        /// form is replayed instead of being re-derived. A payload that stays on the primary image occupies
        /// ordinary contiguous blocks; what changes is the addressing record, so the plan reserves one chunk
        /// index entry per chunk and lets the inode's <c>u</c> field carry the chunk size instead of a block or
        /// cluster count. A payload assigned to an external device keeps the source's device-relative start
        /// blocks and reserves no primary-image blocks at all.
        /// <para>为源 inode 记录了 chunk 大小的常规文件规划 chunk-based 布局
        /// （<c>EROFS_INODE_CHUNK_BASED</c>），构建通过 <see cref="ErofsOptions.ChunkSize"/> 请求时也用于全新
        /// 文件。chunk 几何与记录下来的 chunk 格式字的两个高位（<c>EROFS_CHUNK_FORMAT_INDEXES</c> 与
        /// <c>EROFS_CHUNK_FORMAT_48BIT</c>）优先取自源 inode，否则由地址范围或该选项推导，因此记录了 8 字节索引
        /// 形态的源会被重放而不是重新推导。留在主镜像上的负载占用普通连续块，改变的是寻址记录：为每个 chunk 预留
        /// 一条索引表项，并让 inode 的 <c>u</c> 字段携带 chunk 大小而非块数或簇数。分配到外部设备的负载保持源
        /// 记录的设备内相对首块，且完全不在主镜像上预留块。</para>
        /// </summary>
        /// <param name="node">File node to plan; never a directory.
        /// <para>待规划的文件节点；绝不可能是目录。</para></param>
        /// <returns><see langword="true"/> when the node was planned as chunk-based.
        /// <para>该节点已按 chunk-based 规划时为 <see langword="true"/>。</para></returns>
        /// <exception cref="NotSupportedException">The file would need more chunk index entries than the
        /// metadata area beside its inode record can hold.
        /// <para>该文件所需的 chunk 索引表项超出其 inode 记录旁元数据区可容纳的数量。</para></exception>
        /// <exception cref="FileSystemUnsupportedFeatureException">The requested chunk size is not a power of
        /// two no smaller than the block size, the option names an unusable blob device, or the payload
        /// belongs to an external device without a source chunk index to reuse.
        /// <para>请求的 chunk 大小不是不小于块大小的 2 的幂、选项指定了不可用的 blob 设备，或负载属于外部设备
        /// 但没有可复用的源 chunk 索引。</para></exception>
        private bool PlanChunkLayout(ErofsInode node)
        {
            var chunkBased = node.DataLayout == ErofsDataLayout.ChunkBased
                || node.SourceInode?.DataLayout == ErofsDataLayout.ChunkBased;
            var recordedFormat = node.SourceInode?.ChunkFormat ?? (ushort)0;
            var chunkBits = chunkBased ? node.ChunkFormatBlkBits : 0;
            if (chunkBased && node.SourceInode is { } chunkSource)
            {
                chunkBits = chunkSource.ChunkFormatBlkBits;
            }

            if (!chunkBased && Options.ChunkSize > 0)
            {
                chunkBits = ChunkBitsFromOption(Options.ChunkSize);
                chunkBased = true;
            }

            if (!chunkBased || node.Pclusters is not null || node.IsSymlink || node.IsDevice)
            {
                return false;
            }

            var deviceId = ResolveChunkDevice(node);
            var chunkSize = (long)BlockSize << chunkBits;
            var chunkCount = checked(((long)node.Size - 1) / chunkSize + 1);
            if (chunkCount > MaxChunkIndexEntries)
            {
                throw new NotSupportedException(
                    $"EROFS chunk-based file {node.FullPath} needs {chunkCount} chunk index entries, above the "
                    + $"limit of {MaxChunkIndexEntries}.");
            }

            var wideFormNeeded = deviceId != 0 || Options.ExtraDeviceCount > 0
                || node.UsesChunkIndexes || (node.SourceInode?.UsesChunkIndexes ?? false);
            var usesIndexes = Options.ChunkIndexKind switch
            {
                ErofsChunkIndexKind.Indexes => true,
                ErofsChunkIndexKind.BlockMap => false,
                _ => wideFormNeeded || (recordedFormat & ErofsInode.ChunkFormatIndexes) != 0,
            };
            if (Options.ChunkIndexKind == ErofsChunkIndexKind.BlockMap && wideFormNeeded)
            {
                throw new FileSystemUnsupportedFeatureException(
                    $"EROFS 4-byte chunk block maps were forced, but '{node.FullPath}' needs the 8-byte chunk "
                    + "index entries because " + (deviceId != 0
                        ? $"its payload lives on device {deviceId}"
                        : "its 48-bit addresses or device-shaped layout cannot be expressed by the 4-byte form")
                    + ".");
            }

            node.DeviceId = deviceId;
            if (deviceId != 0)
            {
                var sourceIndex = node.SourceInode?.ChunkIndex;
                if (sourceIndex is null || sourceIndex.Length != chunkCount)
                {
                    throw new FileSystemUnsupportedFeatureException(
                        $"EROFS chunk-based inode '{node.FullPath}' places its payload on device {deviceId}, but no "
                        + "source chunk index with the matching entry count is available to reuse its relative "
                        + "start blocks.");
                }

                node.ChunkIndex = (ulong[])sourceIndex.Clone();
                node.FlatBlocks = 0;
            }
            else
            {
                node.ChunkIndex = new ulong[chunkCount];
                node.FlatBlocks = CeilDiv((long)node.Size, BlockSize);
            }

            var chunkIdata = (ErofsChunkIdata)node.Idata;
            chunkIdata.ChunkFormat = (ushort)(chunkBits
                | (usesIndexes ? ErofsInode.ChunkFormatIndexes : 0)
                | (recordedFormat & ErofsInode.ChunkFormat48Bit));
            chunkIdata.DeviceId = deviceId;
            HasChunkBasedLayout = true;
            return true;
        }

        /// <summary>
        /// Resolves the device that receives a chunk-based node's payload: a preserved source keeps the
        /// device it recorded, a fresh build takes the single configured blob device, and zero keeps the
        /// payload on the primary image.
        /// <para>解析接收 chunk-based 节点负载的设备：保留式源保持其记录的设备，全新构建取配置的单一 blob 设备，
        /// 零则把负载留在主镜像上。</para>
        /// </summary>
        /// <param name="node">Chunk-based node whose payload device is being resolved.
        /// <para>正在解析负载设备的 chunk-based 节点。</para></param>
        /// <returns>The device id written into every index entry of the node.
        /// <para>写入该节点每条索引表项的设备号。</para></returns>
        /// <exception cref="FileSystemUnsupportedFeatureException">The option names more than one blob device,
        /// which the format cannot express, or names an index outside the declared device table.
        /// <para>选项指定了多个 blob 设备（该格式无法表达），或指定了已声明设备表之外的下标。</para></exception>
        private uint ResolveChunkDevice(ErofsInode node)
        {
            if (node.SourceInode is { DeviceId: > 0 } source)
            {
                if (source.DeviceId > Options.ExtraDeviceCount)
                {
                    throw new FileSystemUnsupportedFeatureException(
                        $"EROFS chunk-based inode '{node.FullPath}' records its payload on device "
                        + $"{source.DeviceId}, but only {Options.ExtraDeviceCount} extra devices are declared, so "
                        + "the rebuild cannot address that device.");
                }

                return source.DeviceId;
            }

            if (Options.BlobDeviceIds is not { Count: > 0 } blobDevices)
            {
                return 0;
            }

            if (blobDevices.Count > 1)
            {
                throw new FileSystemUnsupportedFeatureException(
                    $"EROFS blob chunk data lives on one device per image, but {blobDevices.Count} blob device "
                    + "indices were requested.");
            }

            var deviceId = blobDevices[0];
            if (deviceId <= 0 || deviceId > Options.ExtraDeviceCount)
            {
                throw new FileSystemUnsupportedFeatureException(
                    $"EROFS blob device index {deviceId} is outside the {Options.ExtraDeviceCount} declared extra "
                    + "devices.");
            }

            return (uint)deviceId;
        }

        /// <summary>
        /// Converts the requested chunk size into the log2 block count stored in the chunk format word.
        /// <para>把请求的 chunk 大小转换成 chunk 格式字中存储的块数 log2。</para>
        /// </summary>
        /// <param name="chunkSize">Requested chunk size in bytes.
        /// <para>请求的 chunk 大小（字节）。</para></param>
        /// <returns>The chunk size expressed in blocks, as a power of two.
        /// <para>以块数表示的 2 的幂 chunk 大小。</para></returns>
        /// <exception cref="FileSystemUnsupportedFeatureException">The value is not a power of two or is
        /// smaller than one filesystem block.
        /// <para>该值不是 2 的幂或小于一个文件系统块。</para></exception>
        private int ChunkBitsFromOption(int chunkSize)
        {
            if (chunkSize < BlockSize || (chunkSize & (chunkSize - 1)) != 0)
            {
                throw new FileSystemUnsupportedFeatureException(
                    $"EROFS chunk size {chunkSize} must be a power of two no smaller than the {BlockSize}-byte "
                    + "filesystem block size.");
            }

            var bits = 0;
            for (var size = chunkSize; size > BlockSize; size >>= 1)
            {
                bits++;
            }

            return bits;
        }

        /// <summary>
        /// Lays out one non-directory node: inline symlink target, chunk-based addressing, compressed
        /// cluster counts, the recorded compacted 2-byte index form and tail packing, fragment packing,
        /// inline/flat tails, and flat dedup source binding.
        /// <para>布局单个非目录节点：符号链接 inline 目标、chunk-based 寻址、压缩簇计数、已记录的紧凑 2 字节
        /// 索引形态与尾部打包、fragment 打包、inline/平坦尾部以及平坦去重来源绑定。</para>
        /// </summary>
        /// <param name="node">File node to lay out; never a directory.
        /// <para>待布局的文件节点；绝不可能是目录。</para></param>
        /// <exception cref="InvalidOperationException">A symlink target's inline inode record exceeds one
        /// block and therefore cannot be read back.
        /// <para>符号链接目标的 inline inode 记录超过一个块，无法被读回。</para></exception>
        private void PlanFileLayout(ErofsInode node)
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

            if (isDataFile && TakesWholeFileFragment(node))
            {
                PlaceWholeFileFragment(node);
                return;
            }

            if (isDataFile && Options.Fragments && !Options.Metabox
                && node.Size <= (ulong)Options.MaxPclusterSize)
            {
                PlaceWholeFileFragment(node);
                return;
            }

            if (PlanChunkLayout(node))
            {
                return;
            }

            if (node.SourceInode?.SourceCompressed ?? Options.Compress)
            {
                if (node.Pclusters is not null)
                {
                    node.CompressedBlocks = (uint)node.Pclusters.Sum(p => CeilDiv(p.CompressedLength, BlockSize));
                    node.ClusterCount = (uint)CeilDiv((long)node.Size, node.LogicalClusterSize);
                    node.CompactedIndexes = node.SourceInode is { } compactSource
                        ? compactSource.DataLayout == ErofsDataLayout.CompactIndexes
                        : Options.UseCompactedIndexes;
                    var recorded2B = node.SourceInode?.Compacted2B ?? false;
                    if (node.CompactedIndexes && (recorded2B || Options.UseCompacted2BIndexes))
                    {
                        if (node.LogicalClusterSize > 4096)
                        {
                            throw new FileSystemUnsupportedFeatureException(
                                $"EROFS compacted 2-byte indexes need logical clusters of at most 4096 bytes, " +
                                $"but '{node.FullPath}' uses {node.LogicalClusterSize}.");
                        }
                        node.Compacted2B = true;
                    }

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
                    if (PlanTailFragment(node))
                    {
                        return;
                    }

                    KeepTerminalPclusterResolvable(node, BlockSize);
                    PlanExtentIndexes(node);
                    return;
                }
            }

            var wholeBlocks = (long)node.Size / BlockSize;

            var remainder = (long)node.Size % BlockSize;
            var inlineFallback = Options.SourceInodes is { Count: > 0 } ? false : Options.TailPacking;
            if (node.SourceInode is { } inlineSource
                ? inlineSource.DataLayout == ErofsDataLayout.Inline
                : inlineFallback)
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

            if (isDataFile && WholeFileDedupEnabled && node.ContentHash is not null && node.FlatBlocks > 0)
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
        /// Decides whether one compressed inode stores its index as the variable-size extent array, and
        /// records the decided form on the node. Extents are only written for an image that declares
        /// <c>48bit</c> and for an inode without inline data, and the legacy full index form is kept when the
        /// extent metadata would occupy more bytes than that form without any benefit. A request that the
        /// declaration cannot back fails loudly instead of degrading, and a preserved source that recorded
        /// extents keeps them so the strategy check can prove the rebuild preserved them.
        /// <para>判断单个压缩 inode 是否把索引存成可变长度 extent 数组，并把决定的形态记录到节点上。只有声明
        /// <c>48bit</c> 的镜像、且对没有 inline 数据的 inode 才写出 extent；当 extent 元数据比传统完整索引占用更多
        /// 字节且没有收益时保持完整索引形态。声明无法支撑的请求会显式失败而不降级；记录了 extent 的保留式源会保持
        /// 该形态，使策略检查能够证明重建保留了它。</para>
        /// </summary>
        /// <param name="node">Compressed node whose pclusters are already planned.
        /// <para>已完成 pcluster 规划的压缩节点。</para></param>
        /// <exception cref="FileSystemUnsupportedFeatureException">The extent form was requested while the
        /// node carries inline data or while an authoritative declaration omits <c>48bit</c>.
        /// <para>节点携带 inline 数据时请求了 extent 形态，或权威声明缺少 <c>48bit</c> 时为抛出。</para></exception>
        private void PlanExtentIndexes(ErofsInode node)
        {
            if (node.TailFragment)
            {
                return;
            }

            var request = Options.UseExtentIndexes;
            if (request == ErofsTriStateFeature.Disabled)
            {
                return;
            }

            var pclusters = node.Pclusters!;
            if (node.InlineCompressedTail || node.InlineData.Length > 0)
            {
                if (request == ErofsTriStateFeature.Enabled)
                {
                    throw new FileSystemUnsupportedFeatureException(
                        $"EROFS extent indexes need an inode without inline data, but '{node.FullPath}' stores "
                        + "an inline tail.");
                }

                return;
            }

            var sourceExtents = node.SourceInode?.Idata is ErofsCompressedIdata { HasExtents: true };
            if (request == ErofsTriStateFeature.Auto && !sourceExtents)
            {
                return;
            }

            if (Options.FeaturesAuthoritative && !Options.Features.Contains(ErofsFeature.FortyEightBit))
            {
                throw new FileSystemUnsupportedFeatureException(
                    $"EROFS extent indexes of '{node.FullPath}' require the 48bit incompat bit, but the declared "
                    + $"feature set [{ErofsSuperblock.DescribeIncompat(Options.Features.IncompatBits)}] does not "
                    + "advertise it; declare the feature or clear the UseExtentIndexes option.");
            }

            var indexBase = checked((int)AlignUp(GetInodeHeaderSize(node) + node.XattrIbody.Length, 8));
            var (recordSize, _) = SelectExtentGeometry(pclusters, node.Size, BlockSize, false);
            var metadataSize = ZErofsMapHeader.ExtentMetadataSize(indexBase, recordSize, pclusters.Length);
            var fullIndexSize = CeilDiv((long)node.Size, BlockSize) * ZErofsLclusterIndex.Size
                + ZErofsMapHeader.Size;
            if (!ExtentDataUnaligned(pclusters, BlockSize) && metadataSize > fullIndexSize)
            {
                return;
            }

            node.CompactedIndexes = false;
            node.Compacted2B = false;
            node.ZAdvise = ZErofsMapHeader.ExtentAdvise(recordSize);
            node.ZExtents = (ulong)pclusters.Length;
            HasExtentIndexes = true;
        }

        /// <summary>
        /// Decides whether a file is stored whole in the packed fragment inode. A recorded source shape wins
        /// for a preserved rebuild at any size, because the file was already in that shape and its content
        /// identity is what the packed area carries; a source that recorded a tail fragment stays out of this
        /// path since its full clusters live in the file's own blocks. Otherwise the option pair decides and
        /// the shape stays limited to a file shorter than one block.
        /// <para>判断文件是否整份存放在 packed fragment inode 里。保留式重建在任意大小下都以已记录的源形态为准，
        /// 因为该文件本来就是这个形态，packed 区承载的就是其内容身份；记录为尾部 fragment 的源不走这条路，因为
        /// 它完整的簇仍在文件自身的块里。其余情况由选项对决定，且形态仍限于不足一块的文件。</para>
        /// </summary>
        /// <param name="node">Data file node. <para>数据文件节点。</para></param>
        /// <returns><see langword="true"/> when the payload goes to the packed inode.
        /// <para>负载应进入 packed inode 时为 <see langword="true"/>。</para></returns>
        private bool TakesWholeFileFragment(ErofsInode node)
            => node.SourceInode?.SourceFragmentShape == ErofsFragmentShape.WholeFile
                || (node.Size < (ulong)BlockSize
                    && node.SourceInode?.SourceFragmentShape != ErofsFragmentShape.Tail
                    && Options.Fragments && Options.AllFilesAsFragments);

        /// <summary>
        /// Places a whole sub-block file in the shared fragment area, reusing an existing entry when content
        /// dedup is on and identical bytes are already packed. The emitted record keeps
        /// <c>h_clusterbits</c> bit 7 set and carries <c>h_fragmentoff</c>, which marks a file packed whole.
        /// <para>把整份不足一块的文件放入共享 fragment 区；内容去重开启且相同字节已被打包时复用已有条目。
        /// 写出的记录保持 <c>h_clusterbits</c> 第 7 位置位并携带 <c>h_fragmentoff</c>，即标记整文件打包的方式。</para>
        /// </summary>
        /// <param name="node">Node whose payload is packed into the fragment area.
        /// <para>负载被打包进 fragment 区的节点。</para></param>
        private void PlaceWholeFileFragment(ErofsInode node)
        {
            node.IsFragment = true;
            node.TailFragment = false;

            node.Pclusters = null;

            var hashKey = WholeFileDedupEnabled && node.ContentHash is not null
                ? Convert.ToHexString(node.ContentHash)
                : null;
            if (hashKey is not null && DedupFragments.TryGetValue(hashKey, out var existing))
            {
                node.FragmentOffset = existing.FragmentOffset;
                return;
            }

            node.FragmentOffset = AlignFragmentStart(PackedEnd);
            PackedFragments.Add(new ErofsPackedFragment
            {
                Owner = node,
                Offset = node.FragmentOffset,
                SourceOffset = 0,
                Length = (long)node.Size
            });
            PackedEnd = node.FragmentOffset + (long)node.Size;
            if (hashKey is not null)
            {
                DedupFragments[hashKey] = node;
            }
        }

        /// <summary>
        /// Moves a compressed inode's final planned pcluster into the shared fragment area and replaces it
        /// with the index entry that names it. The path runs only when the final window is the last one of
        /// the file and its encoded size stayed below the physical cluster budget, so a final window that
        /// filled its whole budget stays a pcluster. A file whose moving window starts at offset zero is the
        /// whole-file case, so it is routed to <see cref="PlaceWholeFileFragment"/> instead. A plain final
        /// window is left alone because a window that has to be stored uncompressed is never packed, and a
        /// metabox build never packs its residents.
        /// <para>把压缩 inode 的末尾已规划 pcluster 移入共享 fragment 区，并换成指名它的索引条目。仅当该窗口是
        /// 文件最后一个、且其编码尺寸仍低于物理簇预算时才走这条路，填满整个预算的末尾窗口仍是 pcluster。
        /// 移动窗口自偏移零开始即整文件情况，改交给 <see cref="PlaceWholeFileFragment"/>。原样末尾窗口保持不变，
        /// 因为必须不压缩存放的窗口从不打包；metabox 构建的常驻记录同样从不打包。</para>
        /// </summary>
        /// <param name="node">Compressed node whose pclusters were already planned.
        /// <para>已规划 pcluster 的压缩节点。</para></param>
        /// <returns><see langword="true"/> when the node became a whole-file fragment, which ends its layout
        /// because it carries no index rows. <para>节点成为整文件 fragment 时为 <see langword="true"/>，
        /// 它会结束该节点的布局，因为它不携带索引行。</para></returns>
        private bool PlanTailFragment(ErofsInode node)
        {
            if (!Options.Fragments || Options.Metabox || node.TailFragment || node.IsFragment
                || node.InlineCompressedTail)
            {
                return false;
            }

            var pclusters = node.Pclusters!;
            var tail = pclusters[^1];

            var packable = tail.Plain
                ? tail.SourceLength <= Options.MaxPclusterSize
                : tail.CompressedLength < Options.MaxPclusterSize;
            if (!packable)
            {
                return false;
            }

            var offset = PackedEnd;
            PackedFragments.Add(new ErofsPackedFragment
            {
                Owner = node,
                Offset = offset,
                SourceOffset = tail.SourceOffset,
                Length = tail.SourceLength
            });
            PackedEnd = offset + tail.SourceLength;

            if (tail.SourceOffset == 0)
            {
                node.IsFragment = true;
                node.FragmentOffset = offset;
                node.Pclusters = null;
                return true;
            }

            var plans = new ErofsPclusterPlan[pclusters.Length];
            Array.Copy(pclusters, plans, pclusters.Length - 1);
            plans[^1] = new ErofsPclusterPlan
            {
                SourceOffset = tail.SourceOffset,
                SourceLength = tail.SourceLength,
                CompressedLength = 0,
                Fragment = true
            };
            node.Pclusters = plans;
            node.FragmentOffset = offset;
            node.TailFragment = true;
            node.CompressedBlocks = (uint)plans.Sum(p => CeilDiv(p.CompressedLength, BlockSize));
            node.ClusterCount = (uint)CeilDiv((long)node.Size, node.LogicalClusterSize);
            return false;
        }

        /// <summary>
        /// Materializes the metabox container body into a scratch file and plans its pclusters, so the
        /// container travels the same compression pipeline as a regular file. Upstream arrives at the same
        /// shape from the other direction: <c>lib/metabox.c</c> assembles the container into a temporary file
        /// and hands that descriptor to <c>erofs_mkfs_build_special_from_fd</c>, which runs the ordinary
        /// <c>erofs_write_compressed_file</c> path over it. Nothing about a metabox container therefore needs
        /// slot addressing once its bytes exist as a stream — the slots are only the verbatim layout of the
        /// same payload.
        /// <para>把 metabox 容器本体落到暂存文件并规划其 pcluster，使容器与常规文件走同一条压缩管线。上游从
        /// 另一方向得到同一形态：<c>lib/metabox.c</c> 把容器装配进临时文件并把这个描述符交给
        /// <c>erofs_mkfs_build_special_from_fd</c>，由它套用普通的 <c>erofs_write_compressed_file</c>
        /// 路径。因此容器字节一旦以流的形式存在，就完全不需要槽位寻址——槽位只是同一负载在明文形态下的摆放
        /// 方式。</para>
        /// <para>The body is assembled here rather than during the payload pass because a resident's serialized
        /// record embeds the data block assigned to it, so the container's payload is only defined once the
        /// address walk has passed every resident. That is exactly the invariant this call relies on, and the
        /// container is always last in the ordered list because <see cref="PlanMetabox"/> appends it after every
        /// real node.</para>
        /// <para>本体在此处而非负载阶段装配，因为常驻节点的序列化记录内嵌分配给它自身的数据块，容器负载只有
        /// 在地址遍历走过所有常驻节点之后才有定义。这正是本次调用所依赖的不变式，而容器因
        /// <see cref="PlanMetabox"/> 在全部真实节点之后追加而始终位于有序列表末尾。</para>
        /// </summary>
        /// <param name="node">Synthetic metabox container node to plan.
        /// <para>待规划的合成 metabox 容器节点。</para></param>
        /// <param name="staging">Staging file receiving every accepted frame, so the write pass copies frames
        /// instead of compressing them a second time.
        /// <para>接收所有被接受帧的暂存文件，使写出阶段复制帧而不再压缩第二次。</para></param>
        /// <returns>The scratch file path holding the assembled body, which the caller records on the context so
        /// the write pass can address it. <para>承载装配结果的暂存文件路径，调用方将其记录到上下文上供写出阶段
        /// 寻址。</para></returns>
        /// <exception cref="FileSystemCorruptedException">The container body does not compress at all on this
        /// image, so the encoded form would occupy at least as many blocks as the verbatim one.
        /// <para>容器本体在本镜像上完全无法压缩，即编码形态占用的块数不少于明文形态。</para></exception>
        private string PlanMetaboxContainerCompression(ErofsInode node, Core.CompressionPayloadStaging staging)
        {
            var containerOptions = Options.SourceMetaboxAlgorithm is { } algorithm
                && Options.SourceCompressionOptions?.GetValueOrDefault(algorithm) is { } recovered
                    ? recovered
                    : Options;
            var body = BuildMetaboxContainerBody(BlockSize, Now, MetaboxResident!, MetaboxBodyLength,
                SharedXattrMetaboxOffset, SharedXattrs, containerOptions);
            var path = Path.Combine(Path.GetTempPath(), $"uotan-metabox-{Guid.NewGuid():N}.tmp");
            try
            {
                using (var scratch = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    scratch.Write(body, 0, body.Length);
                }

                node.FullPath = path;
                node.CompressionOptions = containerOptions;
                node.LogicalClusterSize = BlockSize;
                using var lz4States = new ErofsLz4StatePool();
                node.Pclusters = TryPlanCompressed(node, BlockSize, containerOptions, staging, lz4States);
            }
            catch
            {
                File.Delete(path);
                throw;
            }

            if (node.Pclusters is null)
            {
                File.Delete(path);
                node.FullPath = string.Empty;
                throw new FileSystemCorruptedException(
                    $"The metabox container body of {MetaboxBodyLength} bytes does not compress on this image, "
                    + "so the rebuild cannot reproduce the source's compressed container faithfully; rebuild "
                    + "with the container left verbatim, or with a codec and pcluster size that let it shrink.");
            }

            node.CompressedBlocks = (uint)node.Pclusters.Sum(p => CeilDiv(p.CompressedLength, BlockSize));
            node.ClusterCount = (uint)CeilDiv(MetaboxBodyLength, node.LogicalClusterSize);
            node.CompactedIndexes = Options.UseCompactedIndexes;
            node.BigPcluster = DeclaredBigPcluster(containerOptions)
                && (containerOptions.Algorithm != ErofsCompressionAlgorithm.Lz4
                    || node.Pclusters.Any(p => p.CompressedLength > BlockSize));
            KeepTerminalPclusterResolvable(node, BlockSize);
            return path;
        }

        /// <summary>
        /// Rewrites the payload's terminal pcluster as an uncompressed cluster when it would otherwise put a
        /// compressed head row on the file's last logical cluster while the inode declares big pclusters.
        /// A reader derives a head row's physical length from the index row that follows the head, and that
        /// probe runs even for a single-logical-cluster pcluster whose frame already fills exactly one block.
        /// When the head sits on the last index row there is no following row, so the tail page fails to read;
        /// the uncompressed form avoids the probe entirely because its physical length is fixed to one logical
        /// cluster.
        /// <para>当尾部 pcluster 会让已声明 big pcluster 的 inode 在文件的最后一个逻辑簇上落下压缩头行时，
        /// 把它改写为不压缩的簇。读取方依赖头行之后的索引行推导物理长度，且即使该 pcluster 只占一个逻辑簇、
        /// 帧恰好占满一块也会探测；头行位于最后一行时其后没有行，尾页会读取失败。不压缩形态把物理长度固定为
        /// 一个逻辑簇，完全避免该探测。</para>
        /// </summary>
        /// <remarks>
        /// Both plans of the rewrite occupy one block, so the row count, the cluster count and the reserved
        /// block count stay identical and the image size does not change. A pcluster whose frame exceeds one
        /// block always spans at least two logical clusters, so declaring big pclusters for the remaining
        /// pclusters stays valid. An inline compressed tail is left alone because its physical length is taken
        /// from the inode's inline-data size and never reaches the probe.
        /// <para>改写前后两个规划都只占一个块，因此索引行数、簇数与预留块数不变，镜像大小不变。帧超过一个块的
        /// pcluster 必然跨越至少两个逻辑簇，所以其余 pcluster 继续声明 big pcluster 仍然有效。内联压缩尾部保持
        /// 不变，因为其物理长度取自 inode 的 inline 数据大小，不会走到探测。</para>
        /// </remarks>
        /// <param name="node">Compressed file node whose plans are already planned and measured.
        /// <para>已完成 pcluster 规划与计量的压缩文件节点。</para></param>
        /// <param name="blockSize">Filesystem block size.
        /// <para>文件系统块大小。</para></param>
        private static void KeepTerminalPclusterResolvable(ErofsInode node, int blockSize)
        {
            if (node.TailFragment)
            {
                return;
            }

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

            plans[^1] = new ErofsPclusterPlan
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
            if (PackedFragments.Count == 0)
            {
                if (Options.Fragments)
                {
                    throw new FileSystemUnsupportedFeatureException(
                        "EROFS fragments were requested but no node qualified for the packed fragment inode, so "
                        + "declaring EROFS_FEATURE_INCOMPAT_FRAGMENTS would advertise a structure the image does "
                        + "not carry; enable AllFilesAsFragments, or include a file whose final compressed window "
                        + "fits one pcluster.");
                }

                return;
            }

            PackedNode = new ErofsInode
            {
                IsPacked = true,
                Size = (ulong)PackedEnd,
                FlatBlocks = CeilDiv(PackedEnd, BlockSize),
                PackedFragments = PackedFragments,
            };
            Ordered.Add(PackedNode);
        }

        /// <summary>
        /// Selects metabox-resident inodes, assigns their slots, and appends the metabox container node.
        /// <para>挑选 metabox 常驻 inode、分配其槽位并追加 metabox 容器节点。</para>
        /// </summary>
        /// <remarks>
        /// The container node is appended last so every resident precedes it in the ordered list. The
        /// address pass relies on that order: a resident's serialized record embeds the data block assigned
        /// to it, so the container body can only be assembled once the residents' addresses are known, which
        /// is exactly when the walk reaches the container.
        /// <para>容器节点追加在最后，因此每个常驻节点在有序列表中都位于它之前。地址分配阶段依赖该顺序：常驻
        /// 节点的序列化记录内嵌分配给它自身的数据块，所以容器本体只有在常驻节点地址已知之后才能装配，而那正是
        /// 遍历走到容器节点的时刻。</para>
        /// <para>The container keeps <c>i_size</c> equal to its logical length whether or not its body is
        /// encoded: a reader derives the compressed cluster count from that length, so publishing the encoded
        /// length there would make the container decode truncated. What an encoded container gives up instead
        /// is <see cref="ErofsInode.FlatBlocks"/>, because its reserved blocks then come from the pcluster plan
        /// the address pass assigns.</para>
        /// <para>无论本体是否编码，容器都把 <c>i_size</c> 保持为其逻辑长度：读取方由该长度推导压缩簇数量，
        /// 因此在那里发布编码后长度会让容器解码被截断。编码后的容器放弃的是
        /// <see cref="ErofsInode.FlatBlocks"/>，因为此时它预留的块来自地址分配阶段所分配的 pcluster 规划。</para>
        /// </remarks>
        internal void PlanMetabox()
        {
            if (!Options.Metabox)
            {
                return;
            }

            MetaboxResident = [.. Ordered.Where(node => IsMetaboxEligible(node, BlockSize))];
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

            if (SharedXattrs.Count > 0)
            {
                metaboxLength = AlignUp(metaboxLength, BlockSize);
                SharedXattrMetaboxOffset = metaboxLength;
                metaboxLength = checked(metaboxLength + SharedXattrs.Sum(entry => AlignUp(entry.Length, ErofsXattr.EntryAlignment)));
            }

            if (MetaboxResident.Count > 0 || Options.MetaboxRequired || SharedXattrs.Count > 0)
            {
                MetaboxBodyLength = metaboxLength;
                MetaboxNode = new ErofsInode
                {
                    IsMetaboxContainer = true,
                    Size = (ulong)metaboxLength,
                    FlatBlocks = Options.MetaboxContainerCompressed ? 0 : CeilDiv(metaboxLength, BlockSize),
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
        /// Gives every regular file an attribute recording the SHA-256 fingerprint of its content. The
        /// attribute's name is the configured prefix verbatim and its value is <c>"sha256:"</c> followed by
        /// the raw 32-byte digest.
        /// <para>为每个常规文件添加记录其内容 SHA-256 指纹的属性。属性名就是配置的前缀文本，
        /// 值为 <c>"sha256:"</c> 加 32 字节原始摘要。</para>
        /// </summary>
        /// <param name="options">Validated options carrying the fingerprint prefix.
        /// <para>携带指纹前缀的经校验选项。</para></param>
        internal void PlanInodeFingerprints(ErofsOptions options)
        {
            if (options.IshareXattrPrefix is not { Length: > 0 } prefix)
            {
                return;
            }

            foreach (var node in Ordered)
            {
                if (node.IsDirectory || node.IsSymlink || node.IsDevice || node.IsPacked
                    || node.IsMetaboxContainer || node.Size <= 0)
                {
                    continue;
                }

                var digest = node.ContentHash ?? HashFile(node, options.CancellationToken);
                node.ContentHash = digest;
                var value = new byte[FingerprintValuePrefix.Length + digest.Length];
                FingerprintValuePrefix.CopyTo(value, 0);
                digest.CopyTo(value, FingerprintValuePrefix.Length);

                var entries = node.XattrEntries is { Count: > 0 } existing ? new List<ErofsXattrEntry>(existing) : [];
                entries.RemoveAll(entry => string.Equals(entry.Name, prefix, StringComparison.Ordinal));
                entries.Add(new ErofsXattrEntry { Name = prefix, Value = value });
                BindXattrEncoding(node,
                    ErofsXattr.Encode(entries, PrefixSeed(), NameFilterRequested), entries);
            }
        }

        private static readonly byte[] FingerprintValuePrefix = "sha256:"u8.ToArray();

        /// <summary>
        /// Collects the xattr sets that repeat across nodes into one shared area and rewrites those nodes'
        /// ibodies to name the shared slot instead of carrying the entries inline. Sharing is what the
        /// <c>ishare_xattrs</c> compat bit advertises, and the ibody form it needs is the 12-byte header
        /// followed by one 4-byte id, which the layout counts as a single inline entry.
        /// <para>把跨节点重复的 xattr 集合收进一个共享区，并把这些节点的 ibody 改写为指名共享槽位而不是
        /// 内联携带条目。共享正是 <c>ishare_xattrs</c> compat 位所声明的内容，它所需的 ibody 形态是 12 字节
        /// 头加一个 4 字节 id，布局把它计为一条 inline 条目。</para>
        /// </summary>
        internal void PlanSharedXattrs()
        {
            if (!SharingRequested)
            {
                return;
            }

            var sharedIds = new Dictionary<string, (uint Id, uint Filter)>(StringComparer.Ordinal);
            var cursor = 0L;
            foreach (var node in Ordered)
            {
                if (node.XattrEntries is not { Count: > 0 } entries)
                {
                    continue;
                }

                var key = string.Join('\u0000', entries.Select(entry => entry.Name + '=' + Convert.ToHexString(entry.Value)));
                if (!sharedIds.TryGetValue(key, out var shared))
                {
                    var encoding = ErofsXattr.Encode(entries, XattrPrefixRecords, NameFilterRequested);
                    var record = encoding.Ibody[ErofsXattr.HeaderSize..];
                    shared = (checked((uint)(cursor / ErofsXattr.SharedIdUnitSize)), encoding.NameFilter);
                    SharedXattrs.Add(record);
                    sharedIds[key] = shared;
                    cursor += AlignUp(record.Length, ErofsXattr.EntryAlignment);
                }

                node.SharedXattrId = checked((int)shared.Id);
                node.XattrIbody = ErofsXattr.EncodeSharedIbody(shared.Id, shared.Filter);
            }
        }

        /// <summary>
        /// Reports whether any planned inode's header carries a non-zero name filter. The compat
        /// <c>xattr_filter</c> bit is published whenever <c>h_name_filter</c> is non-zero, and there is no
        /// all-ones special case: an all-ones word is exactly what an empty name set produces. Every
        /// bound ibody here carries at least one entry, so its word is <c>~bits</c> with <c>bits != 0</c> and
        /// can never be all ones, which makes the <see cref="uint.MaxValue"/> exclusion below defensive only.
        /// <para>判断是否有任一已规划 inode 的头部携带非零名称过滤器。只要 <c>h_name_filter</c> 非零就发布
        /// compat <c>xattr_filter</c> 位，没有全 1 特例：全 1 恰恰是空名称集产生的字。
        /// 此处每个已绑定的 ibody 至少携带一个条目，因此其字为 <c>~bits</c> 且 <c>bits != 0</c>，不可能是全 1，
        /// 下面的 <see cref="uint.MaxValue"/> 排除因此只是防御性的。</para>
        /// </summary>
        /// <returns><see langword="true"/> when at least one inode's filter has a bit to skip on.
        /// <para>至少一个 inode 的过滤器有可跳过的位时为 <see langword="true"/>。</para></returns>
        internal bool AnyNameFilter()
        {
            if (!NameFilterRequested)
            {
                return false;
            }

            foreach (var node in Ordered)
            {
                if (node.XattrIbody.Length >= ErofsXattr.HeaderSize
                    && BinaryPrimitives.ReadUInt32LittleEndian(node.XattrIbody) != 0
                    && BinaryPrimitives.ReadUInt32LittleEndian(node.XattrIbody) != uint.MaxValue)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Merges the long-prefix records every node registered into one image-level table and serializes it.
        /// Records are matched on the exact (base index, infix) pair, so several nodes sharing a long prefix
        /// contribute one table row and every name index they encoded stays valid.
        /// <para>把每个节点登记的长前缀记录合并为一张镜像级表并序列化。记录按（base index、infix）精确
        /// 匹配，因此共用同一长前缀的多个节点只贡献一行，它们编码出的名称索引都保持有效。</para>
        /// </summary>
        internal void PlanXattrPrefixTable()
        {
            var table = PrefixSeed();
            for (var pass = 0; pass < MaxPrefixTablePasses; pass++)
            {
                var grew = false;
                foreach (var (_, entries) in EnumerableNodes())
                {
                    var encoded = ErofsXattr.Encode(entries, table, NameFilterRequested);
                    if (encoded.PrefixTable.Count > table.Count)
                    {
                        table = [.. encoded.PrefixTable];
                        grew = true;
                    }
                }

                if (!grew)
                {
                    break;
                }
            }

            foreach (var (node, entries) in EnumerableNodes())
            {
                BindXattrEncoding(node, ErofsXattr.Encode(entries, table, NameFilterRequested), entries);
            }

            XattrPrefixRecords.Clear();
            XattrPrefixRecords.AddRange(table);
            if (XattrPrefixRecords.Count > 0)
            {
                XattrPrefixTable = ErofsXattr.EncodePrefixTable(XattrPrefixRecords);
            }
        }

        /// <summary>
        /// Enumerates every node that carries inline xattrs together with the entries its ibody currently
        /// holds, so the prefix-table passes can re-encode it without touching nodes that have none.
        /// <para>枚举每个携带 inline xattr 的节点及其 ibody 当前持有的条目，使前缀表各轮可以重新编码它们，
        /// 而不触碰没有任何 xattr 的节点。</para>
        /// </summary>
        /// <returns>Node and entry pairs, in the order the ibodies were bound.
        /// <para>节点与条目对，按 ibody 绑定的顺序。</para></returns>
        private IEnumerable<(ErofsInode Node, List<ErofsXattrEntry> Entries)> EnumerableNodes()
        {
            foreach (var node in Ordered)
            {
                if (node.XattrEntries is { Count: > 0 } entries)
                {
                    yield return (node, entries);
                }
            }
        }

        private const int MaxPrefixTablePasses = 8;

        /// <summary>
        /// Records every encode starts from: the inherited source records, the configured name prefixes
        /// and the fingerprint prefix, all registered verbatim rather than through the automatic splitter.
        /// Doing it here keeps the index the superblock's <c>ishare_xattr_prefix_id</c> names stable,
        /// because each configured prefix occupies a slot before any node registers a record of its own.
        /// <para>每次编码的起始记录集合：继承的源记录、配置的名称前缀与指纹前缀，全部逐字注册，
        /// 而非经自动拆分器。在此注册可让超级块 <c>ishare_xattr_prefix_id</c> 所指名的索引保持稳定，
        /// 因为每个配置的前缀在任何节点登记自己的记录之前就已占据一个槽位。</para>
        /// </summary>
        /// <returns>The seed records in table order.<para>按表序的起始记录。</para></returns>
        private List<(byte BaseIndex, string Infix)> PrefixSeed()
        {
            var seed = new List<(byte BaseIndex, string Infix)>(Options.SourceXattrPrefixes ?? []);
            if (Options.XattrNamePrefixes is { Count: > 0 } configured)
            {
                foreach (var prefixText in configured)
                {
                    AddPrefixRecord(seed, prefixText);
                }
            }

            if (Options.IshareXattrPrefix is { Length: > 0 } prefix)
            {
                AddPrefixRecord(seed, prefix);
            }

            return seed;
        }

        /// <summary>
        /// Whether this build writes per-inode xattr name filters. A caller may ask for them directly, and
        /// an authoritative declaration of <c>xattr_filter</c> asks for them too, so the declared bit is
        /// backed by the words it advertises rather than dropped.
        /// <para>本次构建是否写出逐 inode 的 xattr 名称过滤器。调用方可以直接要求，权威声明
        /// <c>xattr_filter</c> 同样算作要求，使该位有其 advertise 的字作支撑而不是被丢弃。</para>
        /// </summary>
        private bool NameFilterRequested
            => Options.XattrNameFilter
                || Options.SourceXattrFilterFeature
                || (Options.FeaturesAuthoritative && Options.Features.Contains(ErofsFeature.XattrFilter));

        /// <summary>
        /// Whether this build shares repeated xattr sets. As with the name filter, declaring
        /// <c>ishare_xattrs</c> asks for the sharing that backs it.
        /// <para>本次构建是否共享重复的 xattr 集合。与名称过滤器同理，声明 <c>ishare_xattrs</c> 即要求
        /// 支撑它的共享化。</para>
        /// </summary>
        private bool SharingRequested
            => Options.IshareXattrs
                || (Options.FeaturesAuthoritative && Options.Features.Contains(ErofsFeature.SharedInodeXattrs));

        /// <summary>
        /// Refuses to publish a preserved rebuild whose planned storage strategy differs from the strategy
        /// the source image recorded. A rebuilt image that silently re-lays-out a compressed inode as plain
        /// blocks, or that drops a compacted-index or tail-packing decision, is still readable but no longer
        /// the image the source described, so the divergence is an error rather than a quiet downgrade. The
        /// check runs only when the options carry source-inode records, which is exactly the preserved-rebuild
        /// path; a fresh directory build has no source strategy to compare against and is unaffected.
        /// <para>拒绝发布存储策略与源镜像所记录策略不同的保留式重建。把压缩 inode 静默重排为明文块、或丢掉紧凑
        /// 索引与尾部内联决策的重建镜像仍可读取，但已不是源所描述的那张镜像，因此这种偏离属于错误而非静默降级。
        /// 仅当选项携带源 inode 记录（即保留式重建路径）时检查；全新目录构建没有可比对的源策略，不受影响。</para>
        /// <para>The metabox container is checked separately and before the per-inode loop, because it is a
        /// synthetic node that carries no source record of its own: what can be replayed is not read from
        /// <c>SourceInodes</c> but from <see cref="ErofsOptions.SourceMetaboxAlgorithm"/> plus this build's own
        /// codec set, which is exactly what <see cref="ErofsOptions.MetaboxContainerCompressed"/> resolves. A
        /// source whose container was encoded is therefore accepted only when this build encodes it too, and
        /// otherwise keeps failing loudly rather than publishing a verbatim container under the recorded
        /// feature.</para>
        /// <para>metabox 容器单独检查，且先于逐 inode 循环，因为它是不携带自身源记录的合成节点：可重放与否不由
        /// <c>SourceInodes</c> 读出，而由 <see cref="ErofsOptions.SourceMetaboxAlgorithm"/> 与本次构建的编解码器
        /// 集合共同决定，这正是 <see cref="ErofsOptions.MetaboxContainerCompressed"/> 所解析的内容。因此源容器被
        /// 编码过的镜像只在本次构建同样编码它时才被接受，否则继续保持显式失败，而不是在已记录特性下发布明文
        /// 容器。</para>
        /// <para>Four recorded dimensions are reported instead of refused, because the rebuild stays valid without
        /// them and the caller decides what to do with the deviation: a dot-omitted directory, a nlink-one advise
        /// bit on a record that cannot carry it, an interlaced pcluster layout no planned frame reproduced, and a
        /// chunk index whose hole entries were replaced by contiguous chunks. Each becomes one build warning naming
        /// the count and a bounded path sample through <see cref="ErofsOptions.AddBuildWarning"/>, so the
        /// deviation is recorded rather than silent.</para>
        /// <para>有四个已记录维度不是被拒绝而是被上报，因为缺少它们重建仍然有效，偏离的处置交给调用方：省略点项的
        /// 目录、无法承载该位的记录上的 nlink-one advise 位、没有任何已规划帧复现的交错 pcluster 布局，以及其空洞
        /// 表项被连续 chunk 取代的 chunk 索引。每一项都经 <see cref="ErofsOptions.AddBuildWarning"/> 生成一条
        /// 构建告警，给出数量与有界的路径样本，使该偏离被记录而非静默发生。</para>
        /// </summary>
        /// <exception cref="FileSystemUnsupportedFeatureException">A preserved inode's planned storage
        /// strategy differs from its recorded source strategy, the source recorded a structure the planned
        /// layout cannot express, or the source metabox container was encoded with a codec this build cannot
        /// re-encode. <para>某条保留 inode 的已规划存储策略与其记录的源策略不同，源记录的某个结构已规划布局无法
        /// 表达，或源 metabox 容器所用的编解码器本次构建无法重新编码。</para></exception>
        internal void VerifyPreservedStorageStrategies()
        {
            if (Options.SourceInodes is not { Count: > 0 })
            {
                return;
            }

            if (Options.Metabox && Options.SourceMetaboxCompressed && !Options.MetaboxContainerCompressed)
            {
                throw new FileSystemUnsupportedFeatureException(
                    "EROFS preserved rebuild: the source metabox container is compressed (mkfs.erofs "
                    + "-m<size>:<algorithm>) but this build cannot re-encode it, so the rebuild would "
                    + "silently change the metabox payload form; the compressed container layout is "
                    + "not modelled for this codec.");
            }

            List<string> dotOmittedDirectories = [];
            List<string> nlinkAdviceNotReproduced = [];
            List<string> interlacedNotReproduced = [];
            List<string> chunkHolesRegenerated = [];

            foreach (var node in Ordered)
            {
                Options.CancellationToken.ThrowIfCancellationRequested();
                if (node.SourceInode is not { } source || node.IsPacked || node.IsMetaboxContainer)
                {
                    continue;
                }

                var path = BuildImagePath(node);

                var sourceWholeFileFragment = source.SourceFragmentShape == ErofsFragmentShape.WholeFile;
                var sourceCompressed = source.IsCompressed && !sourceWholeFileFragment;
                var plannedCompressed = node.Pclusters is not null;
                if (plannedCompressed != sourceCompressed)
                {
                    throw new FileSystemUnsupportedFeatureException(
                        $"EROFS preserved inode '{path}' recorded compressed={sourceCompressed} but the planned " +
                        $"layout is compressed={plannedCompressed}, so the rebuild would silently change its storage " +
                        "strategy.");
                }

                var sourceFragment = source.SourceFragmentShape;
                if (sourceFragment == ErofsFragmentShape.WholeFile && !node.IsFragment)
                {
                    throw new FileSystemUnsupportedFeatureException(
                        $"EROFS preserved inode '{path}' recorded a whole-file fragment but the planned layout has "
                        + "none, so the rebuild would silently move its payload out of the packed inode.");
                }

                if (sourceFragment == ErofsFragmentShape.Tail && !node.TailFragment)
                {
                    throw new FileSystemUnsupportedFeatureException(
                        $"EROFS preserved inode '{path}' recorded a tail fragment but the planned layout has none, "
                        + "so the rebuild would silently relocate the tail into the file's own blocks.");
                }

                if (node.TailFragment && sourceFragment != ErofsFragmentShape.Tail)
                {
                    throw new FileSystemUnsupportedFeatureException(
                        $"EROFS preserved inode '{path}' is planned as a tail fragment but the source recorded "
                        + $"fragment shape {sourceFragment}, so the rebuild would invent a packed fragment the "
                        + "source image did not describe.");
                }

                if (node.IsFragment && sourceFragment != ErofsFragmentShape.WholeFile)
                {
                    throw new FileSystemUnsupportedFeatureException(
                        $"EROFS preserved inode '{path}' is planned as a whole-file fragment but the source recorded "
                        + $"fragment shape {sourceFragment}, so the rebuild would invent a packed fragment the "
                        + "source image did not describe.");
                }

                if ((source.ZAdvise & ZErofsMapHeader.AdviseInterlacedPcluster) != 0)
                {
                    if (Options.InterlacedPlainPclusters)
                    {
                        node.InterlacedPlainPclusters = true;
                    }
                    else
                    {
                        throw new FileSystemUnsupportedFeatureException(
                            $"EROFS preserved inode '{path}' records the interlaced pcluster layout, but this "
                            + "build cannot reproduce it: the layout is only available while fragments are "
                            + "enabled, the dedupe dimension is not forced to whole-file comparison, and the "
                            + "compressed data is not laid out unaligned.");
                    }
                }

                if (Options.InodeFormat != ErofsInodeFormat.Auto)
                {
                    var sourceExtended = source.IsExtended;
                    var plannedExtended = GetInodeHeaderSize(node) == ErofsInode.ExtendedSize;
                    if (plannedExtended != sourceExtended)
                    {
                        throw new FileSystemUnsupportedFeatureException(
                            $"EROFS preserved inode '{path}' recorded an extended={sourceExtended} inode record but the "
                            + $"forced layout emits an extended={plannedExtended} one, so the rebuild would silently "
                            + "change its record width.");
                    }
                }

                var sourceChunkBased = source.DataLayout == ErofsDataLayout.ChunkBased;
                if (sourceChunkBased && node.ChunkIndex is null)
                {
                    throw new FileSystemUnsupportedFeatureException(
                        $"EROFS preserved inode '{path}' recorded a chunk-based layout with {source.ChunkFormatBlkBits} chunk " +
                        "bits but the planned layout placed it without a chunk index.");
                }

                if (sourceChunkBased && Options.ChunkIndexKind != ErofsChunkIndexKind.Auto
                    && node.UsesChunkIndexes != source.UsesChunkIndexes)
                {
                    throw new FileSystemUnsupportedFeatureException(
                        $"EROFS preserved inode '{path}' recorded 8-byte chunk index entries={source.UsesChunkIndexes} but "
                        + $"the forced layout emits 8-byte chunk index entries={node.UsesChunkIndexes}, so the rebuild "
                        + "would silently change its index form.");
                }

                if (source.SourceIFormatRecorded
                    && (source.IFormat & ErofsInode.AdviseBitNlinkOneOrDotOmitted) != 0)
                {
                    if (node.IsDirectory)
                    {
                        dotOmittedDirectories.Add(path);
                    }
                    else if (node.Nlink != 1 || GetInodeHeaderSize(node) == ErofsInode.ExtendedSize)
                    {
                        nlinkAdviceNotReproduced.Add(path);
                    }
                }

                if ((source.ZAdvise & ZErofsMapHeader.AdviseInterlacedPcluster) != 0
                    && node.Pclusters?.Any(pcluster => pcluster.InterlacedOffset > 0) != true)
                {
                    interlacedNotReproduced.Add(path);
                }

                if (sourceChunkBased && node.DeviceId == 0 && node.ChunkIndex is not null
                    && source.ChunkIndex is { } recordedIndex
                    && recordedIndex.Contains(ChunkIndexHoleAddress(source.Chunk48Bit)))
                {
                    chunkHolesRegenerated.Add(path);
                }

                if (!plannedCompressed)
                {
                    continue;
                }

                var sourceCompactIndexes = source.Idata is ErofsCompressedIdata sourceCompressedState
                    ? sourceCompressedState.CompactedIndexes
                    : source.DataLayout == ErofsDataLayout.CompactIndexes;
                if (node.CompactedIndexes != sourceCompactIndexes)
                {
                    throw new FileSystemUnsupportedFeatureException(
                        $"EROFS preserved inode '{path}' recorded compacted indexes={sourceCompactIndexes} but the " +
                        $"planned layout uses compacted indexes={node.CompactedIndexes}.");
                }

                var sourceInlineTail = source.DataLayout == ErofsDataLayout.Inline;
                var plannedTail = node.InlineData.Length > 0 || node.InlineCompressedTail;
                if (plannedTail != sourceInlineTail)
                {
                    throw new FileSystemUnsupportedFeatureException(
                        $"EROFS preserved inode '{path}' recorded an inline tail={sourceInlineTail} but the planned " +
                        $"layout has an inline tail={plannedTail}.");
                }

                var sourceExtents = source.Idata is ErofsCompressedIdata { HasExtents: true };
                if (sourceExtents && !UsesExtentIndexes(node))
                {
                    throw new FileSystemUnsupportedFeatureException(
                        $"EROFS preserved inode '{path}' recorded an extent index but the planned layout does not "
                        + "use one, so the rebuild would silently change its index form.");
                }
            }

            ReportUnreproducedDimension(dotOmittedDirectories,
                "recorded the dot-omitted directory form, but every directory payload this writer emits carries "
                + "its '.' and '..' entries");
            ReportUnreproducedDimension(nlinkAdviceNotReproduced,
                "recorded the nlink-one inode advice bit on a record that cannot carry it (a link count other than "
                + "one or an extended inode record)");
            ReportUnreproducedDimension(interlacedNotReproduced,
                "recorded the interlaced pcluster layout, but no planned plain frame of theirs could be stored "
                + "with the byte rotation");
            ReportUnreproducedDimension(chunkHolesRegenerated,
                "recorded hole entries in their chunk index table, but a payload on the primary image is laid out "
                + "as contiguous chunks");
        }

        /// <summary>
        /// Records one dimension the rebuild cannot reproduce as a build warning naming the inode count and a
        /// bounded sample of paths. The warning is the degradation record the preserved rebuild owes for a shape
        /// it had to deviate from, so a caller never has to infer the deviation from the emitted bytes.
        /// <para>把重建无法复现的某个维度记录为构建告警，给出 inode 数量与有界的路径样本。该告警正是保留式重建
        /// 为其不得不偏离的形态所欠下的降级记录，调用方无需从写出的字节反推该偏离。</para>
        /// </summary>
        /// <param name="paths">Image paths whose recorded dimension could not be reproduced.
        /// <para>已记录维度无法复现的镜像路径。</para></param>
        /// <param name="explanation">Clause naming the recorded dimension and why it is unavailable.
        /// <para>说明已记录维度及其不可复现原因的从句。</para></param>
        private void ReportUnreproducedDimension(List<string> paths, string explanation)
        {
            if (paths.Count == 0)
            {
                return;
            }

            var sample = string.Join(", ", paths.Take(3).Select(path => $"'{path}'"));
            Options.AddBuildWarning(
                $"EROFS preserved rebuild: {paths.Count} inode(s) {explanation}, so the recorded shape is not "
                + $"reproduced (first: {sample}).");
        }

        /// <summary>
        /// Returns the chunk index entry value that stores a hole for an inode of the given addressing width, so
        /// a recorded index can be searched for entries the contiguous rebuild cannot keep.
        /// <para>返回给定寻址宽度的 inode 用作空洞标记的 chunk 索引表项取值，使重建能在已记录索引中查找连续布局
        /// 无法保留的表项。</para>
        /// </summary>
        /// <param name="wide48Bit">Whether the recorded chunk format uses 48-bit start blocks.
        /// <para>已记录的 chunk 格式是否使用 48 位首块。</para></param>
        /// <returns><c>0xFFFFFFFF</c> for the 32-bit form, <c>0xFFFFFFFFFFFF</c> for the 48-bit form.
        /// <para>32 位形态为 <c>0xFFFFFFFF</c>，48 位形态为 <c>0xFFFFFFFFFFFF</c>。</para></returns>
        private static ulong ChunkIndexHoleAddress(bool wide48Bit)
            => wide48Bit ? (1UL << 48) - 1 : uint.MaxValue;

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
            var dataBlock = (Metazone
                ? AlignUp(InodeTableOffset, BlockSize)
                : AlignUp(metaOff, BlockSize)) / BlockSize;

            Dictionary<(string Hash, int Length), uint>? pclusterFrames =
                PclusterDedupEnabled ? new Dictionary<(string, int), uint>() : null;
            long savedByDeduplication = 0;

            if (XattrPrefixTable is { Length: > 0 } prefixTable)
            {
                XattrPrefixByteOffset = dataBlock * BlockSize;
                XattrPrefixBlocks = CeilDiv(prefixTable.Length, BlockSize);
                dataBlock += XattrPrefixBlocks;
            }

            if (SharedXattrs.Count > 0 && SharedXattrMetaboxOffset < 0)
            {
                var areaLength = SharedXattrs.Sum(entry => AlignUp(entry.Length, ErofsXattr.EntryAlignment));
                SharedXattrBlockAddr = dataBlock;
                SharedXattrBlocks = CeilDiv(areaLength, BlockSize);
                dataBlock += SharedXattrBlocks;
            }

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
                else if (node.IsMetaboxContainer && Options.MetaboxContainerCompressed)
                {
                    SetMetaboxBodyPath(PlanMetaboxContainerCompression(node, EnsureStagedCompression()));
                    foreach (var p in node.Pclusters!)
                    {
                        p.BlockAddress = (uint)dataBlock;
                        dataBlock += CeilDiv(p.CompressedLength, BlockSize);
                    }
                }
                else if (node.Pclusters is not null)
                {
                    foreach (var p in node.Pclusters)
                    {
                        if (p.Fragment)
                        {
                            p.BlockAddress = (uint)((ulong)node.FragmentOffset >> 32);
                            continue;
                        }

                        if (pclusterFrames is not null)
                        {
                            var frameKey = PclusterFrameKey(p);
                            if (TryReusePclusterFrame(p, frameKey, pclusterFrames, out var reused))
                            {
                                p.BlockAddress = reused;
                                savedByDeduplication += p.CompressedLength;
                                continue;
                            }

                            p.BlockAddress = (uint)dataBlock;
                            if (frameKey is not null)
                            {
                                pclusterFrames.TryAdd((frameKey, p.CompressedLength), p.BlockAddress);
                            }
                        }
                        else
                        {
                            p.BlockAddress = (uint)dataBlock;
                        }

                        p.InterlacedOffset = InterlacedRotation(p);
                        if (p.InterlacedOffset != 0)
                        {
                            node.InterlacedPlainPclusters = true;
                        }


                        if (!node.InlineCompressedTail || !ReferenceEquals(p, node.Pclusters[^1]))
                            dataBlock += CeilDiv(p.CompressedLength, BlockSize);
                    }
                }
                else if (node.ChunkIndex is not null && node.DeviceId != 0)
                {
                    if (node.Chunk48Bit || ChunkIndexExceeds32Bit(node.ChunkIndex!))
                    {
                        ChunkIndexRequires48Bit = true;
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

                    if (node.ChunkIndex is not null)
                    {
                        var chunkBits = node.ChunkFormatBlkBits;
                        var chunkBlocks = 1L << chunkBits;
                        var chunkCount = node.ChunkIndex.Length;
                        for (var i = 0; i < chunkCount; i++)
                        {
                            node.ChunkIndex[i] = checked((ulong)(node.FlatBlockAddr + i * chunkBlocks));
                        }

                        var consumedChunks = chunkCount - 1;
                        var lastChunkBytes = (long)node.Size - consumedChunks * ((long)BlockSize << chunkBits);
                        var lastChunkBlocks = Math.Min(chunkBlocks, CeilDiv(lastChunkBytes, BlockSize));
                        dataBlock = node.FlatBlockAddr + consumedChunks * chunkBlocks + lastChunkBlocks;

                        if (node.Chunk48Bit || ChunkIndexExceeds32Bit(node.ChunkIndex!))
                        {
                            ChunkIndexRequires48Bit = true;
                        }
                    }
                }
            }

            var totalBlocks = dataBlock;

            if (Metazone)
            {
                var metadataLength = metaOff - InodeTableOffset;
                MetaZoneByteOffset = checked(totalBlocks * BlockSize);
                foreach (var node in Ordered)
                {
                    if (!node.IsMetaboxResident)
                    {
                        node.MetaOffset = checked(node.MetaOffset + MetaZoneByteOffset);
                    }
                }

                totalBlocks = checked(totalBlocks + CeilDiv(metadataLength, BlockSize));
            }

            ErofsSuperblock.ValidateBlockCountField(totalBlocks);

            if (savedByDeduplication > 0)
            {
                Options.SavedByDeduplication = savedByDeduplication;
            }

            foreach (var (alias, canonical) in Hardlinks) alias.Nid = canonical.Nid;
            ErofsSuperblock.ValidateRootNidField(Root.Nid);
            if (Options.FeaturesAuthoritative)
            {
                var has48Bit = Options.Features.Contains(ErofsFeature.FortyEightBit);
                ErofsSuperblock.ValidateBlockCountField(totalBlocks, has48Bit);
                ErofsSuperblock.ValidateRootNidField(Root.Nid, has48Bit);
            }
            return totalBlocks;
        }

        /// <summary>
        /// Reports whether any entry of a planned chunk index table needs the 48-bit addressing form. A recorded
        /// <c>EROFS_CHUNK_FORMAT_48BIT</c> word and a start block past the 32-bit block-map window are the two
        /// reasons the same superblock bit has to be advertised, and a device-resident index is inspected too
        /// because its entries are replayed verbatim from the source layout.
        /// <para>判断已规划的 chunk 索引表中是否有任何表项需要 48 位寻址形态。记录的
        /// <c>EROFS_CHUNK_FORMAT_48BIT</c> 字与超出 32 位块映射窗口的首块，是必须声明同一超级块位的两个原因；
        /// 位于外部设备的索引同样会被检查，因为其表项按源布局逐字重放。</para>
        /// </summary>
        /// <param name="index">Planned index entries. <para>已规划索引表项。</para></param>
        /// <returns><see langword="true"/> when any entry widens the address.
        /// <para>任一表项加宽地址时为 <see langword="true"/>。</para></returns>
        private static bool ChunkIndexExceeds32Bit(ulong[] index)
        {
            foreach (var startBlock in index)
            {
                if (startBlock > uint.MaxValue)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Hashes one planned pcluster's accepted encoding so a later frame with the same bytes can share its
        /// physical extent. Only the encoded frames take part: a plain pcluster is stored verbatim from its
        /// source file and has no staged encoding to compare, and a fragment entry's bytes live in the packed
        /// inode, where whole-file fragment reuse already applies.
        /// <para>对单个已规划 pcluster 的已接受编码求哈希，使后续字节相同的帧可以共享其物理区间。只有编码帧
        /// 参与：原样 pcluster 自其源文件逐字存放、没有可比对的暂存编码，而 fragment 条目的字节位于 packed
        /// inode，那里已经由整文件 fragment 复用覆盖。</para>
        /// </summary>
        /// <summary>
        /// Reports the byte rotation one planned plain frame is stored with, or zero when it is stored
        /// verbatim. A rotation is only available for a plain frame whose physical address and physical
        /// length are both whole blocks, which is the layout erofs-utils interlaces; a rotated frame also has
        /// to fit inside the single block that holds it, so a frame longer than one block is never rotated.
        /// <para>给出单个已规划原样帧所按的字节旋转量，帧原样存放时为 0。只有物理地址与物理长度都是整数个块的
        /// 原样帧才可旋转，这正是 erofs-utils 为之交错的布局；旋转后的帧还必须能容纳进承载它的单个块内，
        /// 因此超过一块的帧绝不旋转。</para>
        /// </summary>
        /// <param name="pcluster">Planned pcluster whose address has just been assigned.
        /// <para>刚分配好地址的已规划 pcluster。</para></param>
        /// <returns>The rotation in bytes, or <c>0</c> when the frame is stored verbatim.
        /// <para>以字节计的旋转量；帧原样存放时为 <c>0</c>。</para></returns>
        private int InterlacedRotation(ErofsPclusterPlan pcluster)
        {
            if (!Options.InterlacedPlainPclusters || !pcluster.Plain || pcluster.Fragment)
            {
                return 0;
            }

            if (pcluster.SourceLength > BlockSize
                || pcluster.CompressedLength % BlockSize != 0
                || pcluster.BlockAddress % (BlockSize / ErofsSuperblock.CompactInodeSize) != 0)
            {
                return 0;
            }

            return checked((int)(pcluster.SourceOffset % BlockSize));
        }

        /// <param name="pcluster">Planned pcluster of one compressed node.
        /// <para>某个压缩节点的已规划 pcluster。</para></param>
        /// <returns>A hexadecimal digest of the staged frame, or <see langword="null"/> when the entry has no
        /// staged encoding and therefore cannot be compared.
        /// <para>暂存帧的十六进制摘要；条目没有暂存编码因而无法比较时为 <see langword="null"/>。</para></returns>
        private string? PclusterFrameKey(ErofsPclusterPlan pcluster)
        {
            if (pcluster.Fragment || pcluster.Plain || pcluster.StagedOffset < 0
                || pcluster.CompressedLength <= 0)
            {
                return null;
            }

            var staging = StagedCompression;
            if (staging is null)
            {
                return null;
            }

            var frame = ArrayPool<byte>.Shared.Rent(pcluster.CompressedLength);
            try
            {
                staging.Read(pcluster.StagedOffset, frame.AsSpan(0, pcluster.CompressedLength));
                return Convert.ToHexString(Sha256.ComputeHash(frame.AsSpan(0, pcluster.CompressedLength)));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frame);
            }
        }

        /// <summary>
        /// Looks one planned pcluster's encoded frame up in the reuse map and reports the physical block a
        /// previous frame with identical bytes already occupies. The comparison requires the encoded length to
        /// match as well as the digest, because the block region reserved for a frame is derived from that
        /// length and two frames can only share an extent when both agree.
        /// <para>在复用映射中查找单个已规划 pcluster 的编码帧，并给出字节完全相同的前一帧已占用的物理块。
        /// 比较要求编码长度与摘要同时匹配，因为为帧预留的块区间由长度推出，只有两者一致时两帧才能共享区间。</para>
        /// </summary>
        /// <param name="pcluster">Planned pcluster being assigned an address.
        /// <para>正在分配地址的已规划 pcluster。</para></param>
        /// <param name="frameKey">Digest of that pcluster's staged frame, or <see langword="null"/> when the
        /// entry has none and therefore cannot be compared.
        /// <para>该 pcluster 暂存帧的摘要；条目没有暂存帧因而无法比较时为 <see langword="null"/>。</para></param>
        /// <param name="frames">Digest-to-block map built by earlier entries of this pass.
        /// <para>由本阶段更早条目建立的摘要到块的映射。</para></param>
        /// <param name="blockAddress">The reusable block address when the method returns true.
        /// <para>方法返回 true 时可复用的块地址。</para></param>
        /// <returns><see langword="true"/> when an earlier frame with the same digest and length exists.
        /// <para>存在摘要与长度都相同的更早帧时为 <see langword="true"/>。</para></returns>
        private static bool TryReusePclusterFrame(
            ErofsPclusterPlan pcluster,
            string? frameKey,
            Dictionary<(string Hash, int Length), uint> frames,
            out uint blockAddress)
        {
            blockAddress = 0;
            return frameKey is not null
                && frames.TryGetValue((frameKey, pcluster.CompressedLength), out blockAddress);
        }

        /// <summary>
        /// Computes the superblock's incompat feature word. The layout-implied bits are derived from the
        /// planned layout, so a non-authoritative build publishes exactly the bits its pcluster limit,
        /// extra devices, compressed tail packing, fragments and metabox require; an authoritative one takes
        /// the compression baseline from the declaration instead, rejects any layout the declaration forbids,
        /// and keeps every other declared bit so a rebuild neither gains nor loses a feature silently.
        /// <para>计算超级块的 incompat 特性字。布局隐含位由已规划的布局推导，因此非权威构建只发布其 pcluster
        /// 上限、额外设备、压缩尾打包、fragments 与 metabox 所要求的位；权威声明则改从声明读取压缩基线，
        /// 拒绝声明所不允许的布局，并保留其余全部已声明位，使重建既不会静默获得也不会静默丢失特性。</para>
        /// </summary>
        /// <returns>The incompat feature mask for the superblock.
        /// <para>超级块使用的 incompat 特性掩码。</para></returns>
        /// <exception cref="NotSupportedException">The planned layout needs a feature the declaration
        /// forbids, or the declaration names a feature the writer cannot re-emit.
        /// <para>已规划布局需要声明不允许的特性，或声明列出了写入方无法重新输出的特性。</para></exception>
        internal uint ComputeFeatureIncompat()
        {
            var featureIncompat = Options.DerivedIncompatBaseline()
                | (DeclaredFortyEightBit(Options, TotalBlocks) || ChunkIndexRequires48Bit || HasExtentIndexes
                    ? ErofsSuperblock.Incompat48Bit
                    : 0u)
                | (XattrPrefixTable is { Length: > 0 } ? ErofsSuperblock.IncompatXattrPrefixes : 0u)
                | (HasChunkBasedLayout ? ErofsSuperblock.IncompatChunkedFile : 0u);

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
            RequireDeclaredBit(layoutIncompat, ErofsSuperblock.IncompatDeviceTable, ErofsFeature.DeviceTable,
                nameof(ErofsOptions.ExtraDeviceCount));
            RequireDeclaredBit(layoutIncompat, ErofsSuperblock.IncompatFragments, ErofsFeature.Fragments,
                nameof(ErofsOptions.Fragments));
            RequireDeclaredBit(layoutIncompat, ErofsSuperblock.IncompatZtailpacking, ErofsFeature.Ztailpacking,
                nameof(ErofsOptions.CompressedTailPacking));
            RequireDeclaredBit(layoutIncompat, ErofsSuperblock.IncompatMetabox, ErofsFeature.Metabox,
                nameof(ErofsOptions.Metabox));
            RequireDeclaredBit(layoutIncompat, ErofsSuperblock.IncompatChunkedFile, ErofsFeature.ChunkedFile,
                nameof(ErofsOptions.ChunkSize));

            if (!DeclaredBigPcluster(Options) && RequiresBigPclusterLayout())
            {
                throw new NotSupportedException(
                    $"EROFS feature declaration [{ErofsSuperblock.DescribeIncompat(declared)}] omits " +
                    $"{ErofsFeatureTable.NameOf(ErofsFeature.BigPcluster)} while a planned pcluster spans more than " +
                    $"one block; declare the feature or pin {nameof(ErofsOptions.MaxPclusterSize)} to the block size.");
            }

            RequireBackedIncompat(declared, ErofsSuperblock.IncompatDeviceTable, ErofsFeature.DeviceTable,
                ExtraDevices > 0, $"{nameof(ErofsOptions.ExtraDeviceCount)} above zero",
                "device_table/compr_head2");
            RequireBackedIncompat(declared, ErofsSuperblock.IncompatFragments, ErofsFeature.Fragments,
                PackedNode is not null, "the fragment layout (Fragments with ErofsFragments=tails)",
                ErofsFeatureTable.NameOf(ErofsFeature.Fragments));
            RequireBackedIncompat(declared, ErofsSuperblock.IncompatMetabox, ErofsFeature.Metabox,
                MetaboxNode is not null, "the metabox layout (Metabox with ErofsMetabox=forced)",
                ErofsFeatureTable.NameOf(ErofsFeature.Metabox));
            RequireBackedIncompat(declared, ErofsSuperblock.IncompatChunkedFile, ErofsFeature.ChunkedFile,
                HasChunkBasedLayout, "the chunk-based layout",
                ErofsFeatureTable.NameOf(ErofsFeature.ChunkedFile));

            RequireBackedIncompat(declared, ErofsSuperblock.IncompatXattrPrefixes, ErofsFeature.XattrPrefixes,
                XattrPrefixTable is { Length: > 0 },
                "a non-empty xattr prefix table (SourceXattrPrefixes, XattrNamePrefixes or IshareXattrPrefix)",
                ErofsFeatureTable.NameOf(ErofsFeature.XattrPrefixes));

            var wideFieldsPlanned = ChunkIndexRequires48Bit || HasExtentIndexes || TotalBlocks > uint.MaxValue;
            RequireBackedIncompat(declared, ErofsSuperblock.Incompat48Bit, ErofsFeature.FortyEightBit,
                wideFieldsPlanned,
                "a layout needing 48-bit block counts (extent indexes via UseExtentIndexes, a chunk index "
                    + "past uint.MaxValue, or more than 4 GiB of blocks)",
                ErofsFeatureTable.NameOf(ErofsFeature.FortyEightBit));

            RequireBackedCompat(ErofsSuperblock.CompatIshareXattrs, SharedXattrs.Count > 0);
            RequireBackedCompat(ErofsSuperblock.CompatPlainXattrPfx, XattrPrefixTable is { Length: > 0 });
            RequireBackedCompat(ErofsSuperblock.CompatSharedEaInMetabox, SharedXattrMetaboxOffset >= 0);
            RequireBackedCompat(ErofsSuperblock.CompatXattrFilter, AnyNameFilter());

            var incompat = declared;
            if (Options.Compress && (incompat & ErofsSuperblock.IncompatLz4_0Padding) == 0)
            {
                incompat |= ErofsSuperblock.IncompatLz4_0Padding;
                Options.AddedIncompatBits |= ErofsSuperblock.IncompatLz4_0Padding;
            }

            return incompat;
        }

        /// <summary>
        /// Drops a declared compat bit whose structure the planned layout did not produce, recording it in
        /// <see cref="ErofsOptions.DroppedCompatBits"/> so the emitted word never advertises a feature this
        /// build does not carry. Unlike an incompat bit, a compat bit is only an advertisement: a reader that
        /// misses it can still mount the image, so dropping it keeps the image valid where failing would
        /// only refuse a build the caller asked for.
        /// <para>丢弃已声明但规划布局未产出对应结构的 compat 位，并记入
        /// <see cref="ErofsOptions.DroppedCompatBits"/>，使写出的字不会 advertise 本次构建未携带的特性。
        /// 与 incompat 位不同，compat 位只是声明：缺少它的实现仍能挂载镜像，因此丢弃它可保持镜像有效，
        /// 而直接失败只会拒绝调用方要求的构建。</para>
        /// </summary>
        /// <param name="bit">The declared compat bit to check.
        /// <para>待检查的已声明 compat 位。</para></param>
        /// <param name="produced">Whether the planned layout produced the structure behind the bit.
        /// <para>已规划布局是否产出了该位背后的结构。</para></param>
        private void RequireBackedCompat(uint bit, bool produced)
        {
            if (produced || (Options.Features.CompatBits & bit) == 0)
            {
                return;
            }

            Options.DroppedCompatBits |= bit;
        }

        /// <summary>
        /// Rejects a layout-implied incompat bit the declaration does not carry, naming the feature
        /// and the layout option that would have to be cleared.
        /// <para>拒绝声明未携带的布局隐含 incompat 位，并指名相关特性以及需要清除的布局选项。</para>
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
        /// Rejects a declared incompat bit for which the planned layout carries no structure, the opposite
        /// direction of <see cref="RequireDeclaredBit"/>. A published bit without its backing structure makes
        /// the superblock advertise a layout the image does not contain: <c>s_packed_nid</c> or
        /// <c>s_metabox_nid</c> stays zero and a reader honouring the bit looks up a nonexistent inode. The
        /// rejection names both ways out — produce the structure, or clear the bit through the
        /// <c>ErofsFeatures</c> escape hatch.
        /// <para>拒绝规划布局里没有任何结构支撑的已声明 incompat 位，即
        /// <see cref="RequireDeclaredBit"/> 的反方向。带位而无结构会让超级块声明镜像里并不存在的布局：
        /// <c>s_packed_nid</c> 或 <c>s_metabox_nid</c> 保持为 0，遵循该位的读取方会去访问一个不存在的 inode。
        /// 拒绝信息同时给出两条出路——产出该结构，或经 <c>ErofsFeatures</c> 逃生串清除该位。</para>
        /// </summary>
        /// <param name="declared">The declared incompat word. <para>已声明的 incompat 字。</para></param>
        /// <param name="bit">The physical bit to test. <para>待检查的物理位。</para></param>
        /// <param name="feature">Logical feature naming the bit in the escape hatch.
        /// <para>在逃生串中为该位命名的逻辑特性。</para></param>
        /// <param name="produced"><see langword="true"/> when the layout emitted the structure.
        /// <para>布局已发出该结构时为 <see langword="true"/>。</para></param>
        /// <param name="layoutOption">The option that would produce the structure.
        /// <para>能够产出该结构的选项。</para></param>
        /// <param name="names">Display name text for the bit in this context.
        /// <para>该位在本上下文下的显示名称文本。</para></param>
        /// <exception cref="NotSupportedException">The bit is declared while no structure backs it.
        /// <para>该位已声明但没有任何结构支撑时抛出。</para></exception>
        private void RequireBackedIncompat(uint declared, uint bit, ErofsFeature feature, bool produced,
            string layoutOption, string names)
        {
            if ((declared & bit) == 0 || produced)
            {
                return;
            }

            throw new NotSupportedException(
                $"EROFS feature declaration [{ErofsSuperblock.DescribeIncompat(declared)}] carries {names} but the " +
                $"planned layout produced no structure behind it; enable {layoutOption} or clear the bit with " +
                $"ErofsFeatures=!{ErofsFeatureTable.NameOf(feature)}.");
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

                if (UsesExtentIndexes(node))
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
        /// Resolves the on-disk <c>ishare_xattr_prefix_id</c> from the configured fingerprint prefix: it is
        /// that record's plain table index, without the long-prefix flag. The superblock reader rejects any
        /// stored value that is not below <c>s_xattr_prefix_count</c>, so writing the flagged byte would make
        /// the image unmountable. A prefix missing from the settled table is a programming error rather than a
        /// silent zero, because every fingerprinted inode already encoded a name index against that table.
        /// <para>由配置的指纹前缀解析落盘的 <c>ishare_xattr_prefix_id</c>：它是该记录的表索引，不含长前缀
        /// 标志。超级块读取方会拒绝任何不小于
        /// <c>s_xattr_prefix_count</c> 的落盘值，因此写入带标志的字节会让镜像无法挂载。前缀不在已定型的表内属
        /// 编程错误而非静默归零，因为每个打了指纹的 inode 都已针对该表编码了名称索引。</para>
        /// </summary>
        /// <returns>The record's plain table index when a prefix is configured, or the <c>0</c> sentinel when
        /// none is. The two cases are told apart on disk by <c>s_xattr_prefix_count</c>, not by the id: an
        /// index of zero is a legal table index, so a reader must treat a non-empty prefix table as the signal
        /// that the stored id names a record. The value never carries <see cref="ErofsXattr.LongPrefixFlag"/>.
        /// <para>配置了前缀时返回该记录的纯表索引，未配置时返回 <c>0</c> 哨兵值。两种情况在磁盘上由
        /// <c>s_xattr_prefix_count</c> 区分而非由 id 区分：索引 0 是合法的表索引，因此读取方必须以「前缀表非空」
        /// 作为「落盘 id 指名了一条记录」的信号。该值绝不携带 <see cref="ErofsXattr.LongPrefixFlag"/>。</para></returns>
        /// <exception cref="InvalidOperationException">The configured prefix is absent from the settled
        /// table, or its index does not fit the superblock's single id byte.
        /// <para>配置的前缀不在已定型的表内，或其索引装不进超级块的单字节 id。</para></exception>
        private byte ResolveIshareXattrPrefixIndex()
        {
            if (Options.IshareXattrPrefix is not { Length: > 0 } prefix)
            {
                return 0;
            }

            for (var i = 0; i < XattrPrefixRecords.Count; i++)
            {
                if (ErofsXattr.PrefixText(XattrPrefixRecords[i]) == prefix)
                {
                    if (i > ErofsXattr.LongPrefixMask)
                    {
                        throw new InvalidOperationException(
                            $"EROFS fingerprint prefix '{prefix}' settled at table index {i}, which does not fit " +
                            "the superblock's single id byte.");
                    }

                    return checked((byte)i);
                }
            }

            throw new InvalidOperationException(
                $"EROFS fingerprint prefix '{prefix}' was configured but no xattr name registered it.");
        }

        /// <summary>
        /// Fills the superblock structure from the planned layout and options. The compat word is the
        /// derived one constrained by an authoritative declaration, and every derived bit the declaration
        /// omits is recorded in <see cref="ErofsOptions.DroppedCompatBits"/> so the writer never advertises
        /// a compat feature the source lacked. The incidental geometry fields follow the recorded source
        /// dimensions instead of fixed defaults: <c>s_sb_extslots</c> is the recorded extended-slot count raised
        /// to whatever the metabox container needs, and <c>s_dirblkbits</c> is the recorded directory geometry
        /// when this writer lays directories out with it.
        /// <para>根据规划完成的布局与选项填充超级块结构。compat 字为受权威声明约束的推导字，声明缺失的每个推导位
        /// 都会记录到 <see cref="ErofsOptions.DroppedCompatBits"/>，使写入方不会 advertise 源镜像缺失的 compat
        /// 特性。incidental 几何字段跟随已记录的源维度而不是固定默认值：<c>s_sb_extslots</c> 为已记录的扩展槽数量
        /// 并按 metabox 容器的需要抬高，<c>s_dirblkbits</c> 在本写入方按该几何布局目录时取已记录的目录几何。</para>
        /// </summary>
        /// <param name="totalBlocks">Total image block count.
        /// <para>镜像总块数。</para></param>
        /// <param name="featureIncompat">Incompat feature mask to advertise.
        /// <para>要声明的 incompat 特性掩码。</para></param>
        /// <returns>The fully populated superblock.
        /// <para>填充完成的超级块。</para></returns>
        /// <exception cref="FileSystemUnsupportedFeatureException">The recorded directory block size differs from
        /// the filesystem block size this build lays directories out with, or the recorded slot count cannot be
        /// honoured. <para>已记录的目录块大小与本构建布局目录所用的文件系统块大小不同，或已记录的槽位数量无法
        /// 兑现。</para></exception>
        internal ErofsSuperblock BuildSuperblock(long totalBlocks, uint featureIncompat)
        {
            var featureCompat = (Options.SuperblockChecksum ? ErofsSuperblock.CompatSbChecksum : 0u)
                | (Options.SourceMtimeFeature ? ErofsSuperblock.CompatMtime : 0u);

            if (AnyNameFilter())
            {
                featureCompat |= ErofsSuperblock.CompatXattrFilter;
            }

            var isharePrefixIndex = ResolveIshareXattrPrefixIndex();

            if (XattrPrefixTable is { Length: > 0 })
            {
                featureCompat |= ErofsSuperblock.CompatPlainXattrPfx;
            }

            if (SharedXattrs.Count > 0)
            {
                featureCompat |= ErofsSuperblock.CompatIshareXattrs;
            }

            if (SharedXattrs.Count > 0 && SharedXattrMetaboxOffset >= 0)
            {
                featureCompat |= ErofsSuperblock.CompatSharedEaInMetabox;
            }
            if (Options.FeaturesAuthoritative)
            {
                Options.DroppedCompatBits |= featureCompat & ~Options.Features.CompatBits;
                featureCompat &= Options.Features.CompatBits;
            }

            var has48Bit = (featureIncompat & ErofsSuperblock.Incompat48Bit) != 0;
            var superblockBlocks = totalBlocks + ExtraDeviceBlocks;
            return new ErofsSuperblock
            {
                MagicValue = ErofsSuperblock.Magic,
                BlockSizeBits = (byte)BitOperations.Log2((uint)BlockSize),
                RootNid = (ushort)Root.Nid,
                BlocksHi = has48Bit ? checked((ushort)(superblockBlocks >> 32)) : (ushort)0,
                RootNid8b = Root.Nid,
                Inos = (ulong)Ordered.Count,
                BuildTime = Now,
                BuildTimeNsec = Options.BuildTimeNanoseconds,
                BuildTime32 = Options.BuildTimeOffset,
                Blocks = has48Bit
                    ? (uint)(superblockBlocks & uint.MaxValue)
                    : (uint)superblockBlocks,
                MetaBlkAddr = (uint)(MetaZoneByteOffset / BlockSize),
                AvailableComprAlgs = CompressedAlgorithmsField(Options),
                FeatureCompat = featureCompat,
                FeatureIncompat = featureIncompat,
                ExtraDevices = (ushort)ExtraDevices,
                DevtSlotoff = DeviceTableSlotOffset,
                PackedNid = PackedNode?.Nid ?? 0,
                SbExtSlots = ResolveExtendedSlotCount(Options, MetaboxNode is not null),
                MetaBoxNid = MetaboxNode?.Nid ?? 0,
                Uuid = Options.Uuid is { Length: 16 } ? Options.Uuid : Guid.NewGuid().ToByteArray(),
                VolumeName = Options.VolumeName,
                VolumeNameBytes = Options.SourceVolumeNameBytes ?? [],

                DirBlkBits = ResolveDirBlkBits(),
                XattrPrefixCount = XattrPrefixTable is { Length: > 0 } ? (byte)XattrPrefixRecords.Count : (byte)0,
                XattrPrefixStart = XattrPrefixByteOffset >= 0 ? (uint)(XattrPrefixByteOffset >> 2) : 0u,
                IshareXattrPrefixId = isharePrefixIndex,
                XattrBlkAddr = SharedXattrs.Count > 0
                    ? checked((uint)(SharedXattrMetaboxOffset >= 0
                        ? SharedXattrMetaboxOffset / BlockSize
                        : SharedXattrBlockAddr))
                    : 0u,
            };
        }

        /// <summary>
        /// Writes the superblock buffer, the trailing compression configuration, and the extra device
        /// table when extra devices are present. The superblock buffer spans the published extended slots so the
        /// compression configuration starts exactly where a reader looks for it, and every device slot carries
        /// its recorded tag bytes verbatim and the unified start block the recorded chain licenses.
        /// <para>写出超级块缓冲、其后的压缩配置，以及存在额外设备时的设备表。超级块缓冲覆盖已发布的扩展槽，
        /// 使压缩配置恰好起于读取方查找它的位置；每个设备槽逐字携带其已记录 tag 字节，以及已记录链所许可的
        /// 统一首块。</para>
        /// </summary>
        /// <param name="output">Destination stream in image coordinates.
        /// <para>镜像坐标下的目标流。</para></param>
        /// <param name="superblock">Populated superblock to serialize.
        /// <para>待序列化的已填充超级块。</para></param>
        /// <param name="totalBlocks">Total image block count used as the unified device address base.
        /// <para>镜像总块数，作为设备统一地址的基数。</para></param>
        /// <exception cref="FileSystemCorruptedException">A recorded device tag is wider than its slot.
        /// <para>已记录的设备 tag 宽于其槽位。</para></exception>
        internal void WriteSuperblockAndDevices(Stream output, ErofsSuperblock superblock, long totalBlocks)
        {
            var superblockBuffer = new byte[ErofsSuperblock.SuperblockSize
                + ResolveExtendedSlotCount(Options, MetaboxNode is not null)
                    * ErofsSuperblock.ExtSlotSize];
            superblock.WriteTo(superblockBuffer);

            WriteAt(output, ErofsSuperblock.SuperblockOffset, superblockBuffer, superblockBuffer.Length);
            if (CompressionConfiguration.Length > 0)
            {
                WriteAt(output, ErofsSuperblock.SuperblockOffset + superblockBuffer.Length,
                    CompressionConfiguration, CompressionConfiguration.Length);
            }

            if (XattrPrefixTable is { Length: > 0 } prefixTable && XattrPrefixByteOffset >= 0)
            {
                WriteAt(output, XattrPrefixByteOffset, prefixTable, prefixTable.Length);
            }

            if (SharedXattrs.Count > 0 && SharedXattrMetaboxOffset < 0)
            {
                var cursor = SharedXattrBlockAddr * BlockSize;
                foreach (var entry in SharedXattrs)
                {
                    WriteAt(output, cursor, entry, entry.Length);
                    cursor += AlignUp(entry.Length, ErofsXattr.EntryAlignment);
                }
            }

            if (ExtraDevices > 0)
            {
                var descriptors = Options.ExtraDeviceImages
                    ?? throw new NotSupportedException("EROFS multi-device output requires ExtraDeviceImages descriptors.");
                var slotBuffer = new byte[ErofsDeviceSlot.Size];
                var uniAddresses = ResolveDeviceUniAddresses(totalBlocks, descriptors);
                for (var i = 0; i < ExtraDevices; i++)
                {
                    var descriptor = descriptors[i];
                    var blocks = (ulong)(descriptor.SizeBytes / BlockSize);
                    var slot = new ErofsDeviceSlot
                    {
                        Tag = DeviceSlotTagBytes(descriptor, i),
                        Blocks = blocks,
                        UniAddr = uniAddresses[i],
                    };
                    slot.WriteTo(slotBuffer);
                    WriteAt(output, DeviceTableOffset + i * ErofsDeviceSlot.Size, slotBuffer, slotBuffer.Length);
                }
            }
        }

        /// <summary>
        /// Returns the 64-byte tag written into one extra device slot. Recorded raw bytes are replayed verbatim
        /// and only padded with the zeros the slot already reserves, because the field is defined to carry a
        /// digest or another binary identity rather than text; a descriptor without recorded bytes keeps the
        /// repository's UTF-8 text path so a profile that predates the raw-byte dimension stays rebuildable.
        /// <para>返回写入单个额外设备槽的 64 字节 tag。已记录的原始字节按原样重放，只用槽位本就预留的零填充，
        /// 因为该字段定义为承载摘要或其它二进制身份而不是文本；未记录原始字节的描述符保持仓库既有的 UTF-8 文本
        /// 路径，使早于该原始字节维度的档案仍可重建。</para>
        /// </summary>
        /// <param name="descriptor">Validated descriptor of one extra device.
        /// <para>单个额外设备的经校验描述符。</para></param>
        /// <param name="deviceIndex">Zero-based index used to name the slot in an error.
        /// <para>用于在错误中指名槽位的零基下标。</para></param>
        /// <returns>The zero-padded slot contents. <para>补零后的槽位内容。</para></returns>
        /// <exception cref="FileSystemCorruptedException">The recorded bytes are wider than the slot, which a
        /// silent truncation would turn into a different identity.
        /// <para>记录的字节宽于该槽位，静默截断会把它变成另一个身份。</para></exception>
        private static byte[] DeviceSlotTagBytes(ErofsDeviceSpec descriptor, int deviceIndex)
        {
            var tag = new byte[ErofsDeviceSlot.TagSize];
            if (descriptor.TagBytes is not { } recorded)
            {
                Encoding.UTF8.GetBytes(descriptor.Tag, tag);
                return tag;
            }

            if (recorded.Length > ErofsDeviceSlot.TagSize)
            {
                throw new FileSystemCorruptedException(
                    $"EROFS extra device slot {deviceIndex + 1} records {recorded.Length} tag bytes, above the "
                    + $"{ErofsDeviceSlot.TagSize}-byte tag field.");
            }

            recorded.AsSpan().CopyTo(tag);
            return tag;
        }

        /// <summary>
        /// Resolves the unified starting block written into every extra device slot. A recorded device chain is
        /// replayed as it was captured while it stays addressable in the regenerated image, so a tool that
        /// resolves a device window from the published addresses keeps working; a chain the new layout cannot
        /// reproduce is re-based onto the regenerated address space and the deviation is recorded as a build
        /// warning, so a device is never renumbered silently.
        /// <para>解析写入每个额外设备槽的统一首块。记录下来的设备链只要在重建镜像中仍可寻址就按捕获值重放，
        /// 因此依据已发布地址解析设备窗口的工具仍然可用；新布局无法复现的链会重新落到重建出的地址空间上，并把该
        /// 偏离记录为构建告警，因此设备绝不会被静默重新编号。</para>
        /// </summary>
        /// <param name="totalBlocks">Block count of the primary image the device windows follow.
        /// <para>设备窗口紧随其后的主镜像块数。</para></param>
        /// <param name="descriptors">One descriptor per declared extra device, in slot order.
        /// <para>按槽位顺序的每个已声明额外设备一个描述符。</para></param>
        /// <returns>The unified starting block of every slot, either the recorded or the re-based value.
        /// <para>每个槽位的统一首块，取记录值或重落值。</para></returns>
        private ulong[] ResolveDeviceUniAddresses(long totalBlocks, IReadOnlyList<ErofsDeviceSpec> descriptors)
        {
            var resolved = new ulong[ExtraDevices];
            var derived = (ulong)totalBlocks;
            for (var i = 0; i < ExtraDevices; i++)
            {
                resolved[i] = derived;
                derived += (ulong)(descriptors[i].SizeBytes / BlockSize);
            }

            if (Options.SourceDeviceTable is not { } recorded || recorded.Count < ExtraDevices)
            {
                return resolved;
            }

            const ulong AddressLimit = 1UL << 48;
            var reproducible = recorded[0].UniAddr >= (ulong)totalBlocks && recorded[0].UniAddr < AddressLimit;
            for (var i = 1; reproducible && i < ExtraDevices; i++)
            {
                var expected = recorded[i - 1].UniAddr + (ulong)(descriptors[i - 1].SizeBytes / BlockSize);
                reproducible = expected < AddressLimit && recorded[i].UniAddr == expected;
            }

            if (reproducible)
            {
                for (var i = 0; i < ExtraDevices; i++)
                {
                    resolved[i] = recorded[i].UniAddr;
                }

                return resolved;
            }

            Options.AddBuildWarning(
                $"EROFS preserved rebuild: the recorded unified start block of device 1 ({recorded[0].UniAddr}) "
                + $"cannot be replayed because the regenerated primary image occupies {totalBlocks} blocks and the "
                + $"device chain has to stay contiguous; the device addresses were re-based to block {resolved[0]}.");
            return resolved;
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
                    WriteMetaboxContainer(output, node, BlockSize, Now, MetaboxResident!, MetaboxBodyLength,
                        SharedXattrMetaboxOffset, SharedXattrs, Options);
                }
                else if (node.FlatBlockAddr >= 0 && node.FlatBlocks > 0 && node.DedupSource is null)
                {
                    var copyLen = Math.Min(node.FlatBlocks * BlockSize, (long)node.Size);
                    CopyFileRange(output, node, 0, copyLen, node.FlatBlockAddr * BlockSize, Options.CancellationToken);
                }

                if (IsMeasuredPayload(node) && node.Pclusters is null)
                {
                    progress?.Add((int)node.Size);
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
            var rented = ArrayPool<byte>.Shared.Rent(BlockSize);
            try
            {
                foreach (var node in Ordered)
                {
                    Options.CancellationToken.ThrowIfCancellationRequested();
                    if (node.IsMetaboxResident)
                    {
                        continue;
                    }

                    BuildInode(node, BlockSize, Now, Options);
                    var inodeBuffer = AcquireInodeBuffer(rented, node.OnDiskSize);
                    node.WriteTo(inodeBuffer);
                    WriteAt(output, node.MetaOffset, inodeBuffer, node.OnDiskSize);

                    if (node.XattrIbody.Length > 0)
                    {
                        WriteAt(output, node.MetaOffset + node.OnDiskSize, node.XattrIbody, node.XattrIbody.Length);
                    }

                    var cursor = node.MetaOffset + node.OnDiskSize + node.XattrISize;

                    if (node.IsDirectory)
                    {
                        if (node.DirectoryInline)
                        {
                            WriteAt(output, cursor, BuildDirPayload(node), (int)EstimateDirPayloadLength(node));
                        }
                    }
                    else if (node.IsFragment)
                    {
                        WriteFragmentHeader(output, node);
                    }
                    else if (node.ChunkIndex is not null)
                    {
                        WriteChunkIndexTable(output, node);
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
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        /// <summary>
        /// Returns the inode record destination, reusing one pooled buffer across the walk and falling back to a
        /// right-sized allocation only for a record that outgrows it.
        /// <para>返回 inode 记录的写入目标：整个遍历复用一个池化缓冲，仅在记录超出该缓冲时才退化为按尺寸精确分配。</para>
        /// </summary>
        /// <param name="rented">Buffer obtained from <see cref="ArrayPool{T}.Shared"/> sized at least <c>BlockSize</c>.
        /// <para>从 <see cref="ArrayPool{T}.Shared"/> 取得、至少为 <c>BlockSize</c> 的缓冲。</para></param>
        /// <param name="required">Bytes the inode record occupies.
        /// <para>该 inode 记录占用的字节数。</para></param>
        /// <returns>The buffer to write the record into, whose usable prefix is <paramref name="required"/> bytes.
        /// <para>用于写入该记录的缓冲，其可用前缀为 <paramref name="required"/> 字节。</para></returns>
        private static byte[] AcquireInodeBuffer(byte[] rented, int required)
            => required <= rented.Length ? rented : new byte[required];

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