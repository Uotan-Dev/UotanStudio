using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace UotanStudio.Firmware.Erofs;

/// <summary>
/// 从目录打包 erofs 镜像的构建器，磁盘布局对齐 erofs-utils mkfs.erofs 与内核 erofs_fs.h：
/// 超级块位于偏移 1024（128 字节槽位，结构体 112 字节）；inode 表紧随其后，每个 inode 记录
/// 占 OnDiskSize(32/64B) + inline xattr + 内联数据，记录按 32 字节对齐推进，
/// nid = 距 inode 表起点的 32 字节槽数。目录为“dirent 数组 + 紧随名字区”的紧凑布局
/// （de[0].nameoff = 名字区起点，名字按 UTF-8 字节序递增，与 fsck 校验一致）；目录数据
/// 单块内联（tail-packing），多块写入数据区。文件支持 flat 与 LZ4 压缩（legacy full
/// 8B 索引，lcluster = 块大小）。
/// <para>A directory-to-image EROFS builder whose on-disk layout follows erofs-utils mkfs.erofs and
/// the kernel erofs_fs.h: the superblock lives at offset 1024 (128-byte slot, 112-byte struct);
/// the inode table follows it, each inode record occupying OnDiskSize (32/64B) + inline xattrs +
/// inline data, advanced on 32-byte boundaries, with nid = the 32-byte slot count from the inode
/// table start. Directories use the compact "dirent array + contiguous name area" layout
/// (de[0].nameoff anchors the name area; names are strictly increasing UTF-8 byte sequences, as
/// fsck checks); single-block directory payloads are inlined (tail-packing), larger ones go to
/// the data area. Files support flat and LZ4 compression (legacy full 8-byte indexes,
/// lcluster = block size).</para>
/// <para>Layout is planned in a first pass and the image is streamed out in a second pass:
/// the output stream is <see cref="Stream.SetLength(long)"/>-preallocated and each region
/// (superblock, data area, inodes, compressed index tables and directory blocks) is seeked
/// to and written individually, so no full in-memory image array is ever built.
/// 布局在第一遍扫描中规划，第二遍将镜像流式写出：先对输出流
/// <see cref="Stream.SetLength(long)"/> 预分配，再逐个定位写入超级块、数据区、
/// inode、压缩索引表与目录块，全程不构造完整镜像数组。</para>
/// </summary>
public sealed class ErofsBuilder
{
    /// <summary>inode 表起点：超级块 128 字节槽位之后。Inode table start: right after the 128-byte superblock slot.</summary>
    private const long InodeTableStart = ErofsSuperblock.SuperblockOffset + ErofsSuperblock.SuperblockSize;

    /// <summary>dirent nid 的 METABOX 标记位（<c>EROFS_DIRENT_NID_METABOX_BIT = 63</c>）：
    /// 置位表示低 63 位是 metabox 容器内的 inode 槽位索引，而非经典 inode 表槽号。
    /// METABOX marker bit of a dirent nid (<c>EROFS_DIRENT_NID_METABOX_BIT = 63</c>): when set, the
    /// low 63 bits are a slot index inside the metabox container rather than a classic inode-table slot.</summary>
    private const ulong NidMetaboxFlag = 1UL << 63;

    // ---- Linux stat struct for lstat P/Invoke (x64/arm64 layout). ----
    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxStat
    {
        public ulong st_dev;
        public ulong st_ino;
        public ulong st_nlink;
        public uint st_mode;
        public int st_uid;
        public int st_gid;
        public int __pad0;
        public ulong st_rdev;
        public long st_size;
        public long st_blksize;
        public long st_blocks;
        public long st_atime;
        public long st_atime_nsec;
        public long st_mtime;
        public long st_mtime_nsec;
        public long st_ctime;
        public long st_ctime_nsec;
        public long __unused0;
        public long __unused1;
        public long __unused2;
    }

    [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
    private static extern int LStat(string path, out LinuxStat buf);

    /// <summary>
    /// 将目录打包为 erofs 镜像字节。
    /// <para>Packs a directory into erofs image bytes.</para>
    /// </summary>
    public byte[] Build(string directoryPath, ErofsOptions? options = null)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"uotan-erofs-{Guid.NewGuid():N}.tmp");
        try
        {
            Build(directoryPath, temporaryPath, options);
            using var image = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (image.Length > Array.MaxLength)
            {
                throw new InvalidOperationException($"Image size {image.Length} exceeds the maximum byte-array length.");
            }

            var result = new byte[(int)image.Length];
            image.ReadExactly(result);
            return result;
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { /* Cleanup must not replace the primary failure. */ }
        }
    }

    /// <summary>
    /// 将目录原子打包到指定路径。构建使用目标同目录的临时文件，成功刷新后再覆盖目标。
    /// <para>Atomically packs a directory to a path by building and flushing a temporary file
    /// in the destination directory before replacing the destination.</para>
    /// </summary>
    public void Build(string directoryPath, string outputPath, ErofsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(fullOutputPath)!;
        var temporaryPath = Path.Combine(outputDirectory, $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");
        // Collect before creating a destination parent that may be inside the source tree.
        var effective = ValidateAndCollect(directoryPath, out var root, options, fullOutputPath, temporaryPath);
        Directory.CreateDirectory(outputDirectory);

        try
        {
            using (var temporary = new FileStream(
                       temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
                       FileOptions.SequentialScan))
            {
                BuildImage(root, effective, temporary, 0, freshDestination: true);
                temporary.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullOutputPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { /* Cleanup must not replace the primary failure. */ }
        }
    }

    /// <summary>
    /// 将目录打包为 erofs 镜像并从输出流当前 Position 写入。可寻址流保留此前前缀；
    /// 仅可写的不可寻址流通过临时文件顺序复制。
    /// <para>Packs a directory starting at the output stream's current position. Seekable streams
    /// retain their prefix; writable non-seekable streams are supported through file staging.</para>
    /// </summary>
    public void Build(string directoryPath, Stream output, ErofsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanWrite)
        {
            throw new ArgumentException("The output stream must be writable.", nameof(output));
        }

        var excludedPaths = output is FileStream fileStream
            ? new[] { Path.GetFullPath(fileStream.Name) }
            : [];
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"uotan-erofs-{Guid.NewGuid():N}.tmp");
        var stagingExclusions = excludedPaths.Append(Path.GetFullPath(temporaryPath)).ToArray();
        var effective = ValidateAndCollect(directoryPath, out var root, options, stagingExclusions);
        if (output.CanSeek && SupportsResizeWithoutMutation(output))
        {
            var baseOffset = output.Position;
            BuildImage(root, effective, output, baseOffset, freshDestination: false);
            return;
        }

        try
        {
            using (var staging = new FileStream(
                       temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
                       FileOptions.SequentialScan))
            {
                BuildImage(root, effective, staging, 0, freshDestination: true);
                staging.Position = 0;
                staging.CopyTo(output);
            }
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { /* Cleanup must not replace the primary failure. */ }
        }
    }

    private static bool SupportsResizeWithoutMutation(Stream output)
    {
        var position = output.Position;
        try
        {
            output.SetLength(output.Length);
            return true;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        finally
        {
            output.Position = position;
        }
    }

    private static ErofsOptions ValidateAndCollect(
        string directoryPath,
        out BuildNode root,
        ErofsOptions? options,
        params string[] excludedPaths)
    {
        ArgumentNullException.ThrowIfNull(directoryPath);
        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"Input path is not a directory: '{directoryPath}'.");
        }

        var effective = options ?? new ErofsOptions();
        effective.Validate();
        var exclusions = excludedPaths.Length == 0
            ? null
            : new HashSet<string>(
                excludedPaths.Select(Path.GetFullPath),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        root = CollectNodes(directoryPath, exclusions);
        return effective;
    }

    private static BuildNode CollectNodes(string directoryPath, HashSet<string>? excludedPaths)
    {
        var rootFull = Path.GetFullPath(directoryPath);
        return CollectNode(rootFull, isRoot: true, excludedPaths);
    }

    /// <summary>
    /// 读取 Unix 元数据（uid/gid/mtime/permissions/dev）：Linux 通过 lstat 获取全部字段；
    /// 其他平台使用回退默认值（permissions = 0，uid/gid = 0，mtime = 0）。
    /// <para>Reads Unix metadata (uid/gid/mtime/permissions/dev): Linux uses lstat for all fields;
    /// other platforms fall back to defaults (permissions = 0, uid/gid = 0, mtime = 0).</para>
    /// </summary>
    private static void ReadUnixMetadata(
        string fullPath,
        out uint uid,
        out uint gid,
        out long mtime,
        out ushort permissions,
        out bool isDevice,
        out uint deviceRdev,
        out ushort deviceMode)
    {
        uid = 0;
        gid = 0;
        mtime = 0; // 0 = use build time in BuildInode
        permissions = 0;
        isDevice = false;
        deviceRdev = 0;
        deviceMode = 0;

        // 通过 lstat 读取全部 Unix 元数据（仅 Linux）。Read all Unix metadata via lstat (Linux only).
        if (!OperatingSystem.IsLinux()) return;

        try
        {
            var buf = new LinuxStat();
            if (LStat(fullPath, out buf) != 0) return;

            uid = (uint)buf.st_uid;
            gid = (uint)buf.st_gid;
            mtime = buf.st_mtime;
            permissions = (ushort)(buf.st_mode & 0xFFF);

            var fileType = buf.st_mode & 0xF000;
            if (fileType == 0x2000 || fileType == 0x6000) // S_IFCHR or S_IFBLK
            {
                isDevice = true;
                deviceRdev = (uint)buf.st_rdev;
                deviceMode = (ushort)buf.st_mode;
            }
        }
        catch
        {
            // lstat 失败时保持默认值。Keep defaults when lstat fails.
        }
    }

    private static BuildNode CollectNode(string fullPath, bool isRoot, HashSet<string>? excludedPaths)
    {
        var attributes = File.GetAttributes(fullPath);
        var name = isRoot ? string.Empty : Path.GetFileName(fullPath);

        // 符号链接：读取目标并内联存储。Symlinks: read target and store inline.
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            string target = string.Empty;
            try { target = new FileInfo(fullPath).LinkTarget ?? string.Empty; } catch { /* broken symlink */ }
            return new BuildNode
            {
                Name = name,
                IsDirectory = false,
                FullPath = fullPath,
                IsSymlink = true,
                SymlinkTarget = target,
                Size = Encoding.UTF8.GetByteCount(target),
            };
        }

        // 读取 Unix 元数据。Read Unix metadata.
        ReadUnixMetadata(
            fullPath,
            out var uid,
            out var gid,
            out var mtime,
            out var _permissions,
            out var isDevice,
            out var deviceRdev,
            out var deviceMode);

        // 字符/块设备：DataLayoutFlat，设备号写入 i_u。Char/block devices: flat layout, rdev in i_u.
        if (isDevice)
        {
            return new BuildNode
            {
                Name = name,
                IsDirectory = false,
                FullPath = fullPath,
                IsDevice = true,
                DeviceRdev = deviceRdev,
                DeviceMode = deviceMode,
                Uid = uid,
                Gid = gid,
                Mtime = mtime,
            };
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            var node = new BuildNode
            {
                Name = name,
                IsDirectory = true,
                FullPath = fullPath,
                Uid = uid,
                Gid = gid,
                Mtime = mtime,
            };
            foreach (var childPath in Directory.EnumerateFileSystemEntries(fullPath))
            {
                var childFullPath = Path.GetFullPath(childPath);
                if (excludedPaths?.Contains(childFullPath) == true)
                {
                    continue;
                }

                node.Children.Add(CollectNode(childFullPath, isRoot: false, excludedPaths));
            }

            return node;
        }

        var info = new FileInfo(fullPath);
        return new BuildNode
        {
            Name = name,
            IsDirectory = false,
            FullPath = fullPath,
            Size = info.Length,
            Uid = uid,
            Gid = gid,
            Mtime = mtime,
        };
    }

    private static void CollectPreOrder(BuildNode node, List<BuildNode> ordered)
    {
        ordered.Add(node);
        foreach (var child in node.Children)
        {
            child.Parent = node;
            CollectPreOrder(child, ordered);
        }
    }

    /// <summary>
    /// 把 <see cref="ErofsOptions.FileXattrs"/> 中按相对路径列出的 xattr 编码为 inline ibody
    /// 挂到对应节点。未配置注入或路径不匹配时节点保持无 xattr。
    /// <para>Encodes the per-path xattrs from <see cref="ErofsOptions.FileXattrs"/> into inline
    /// ibodies and attaches them to the matching nodes. Nodes stay xattr-free when no injection is
    /// configured or the path does not match.</para>
    /// </summary>
    private static void BindXattrs(List<BuildNode> ordered, ErofsOptions options)
    {
        if (options.FileXattrs is not { Count: > 0 })
        {
            return;
        }

        foreach (var node in ordered)
        {
            if (node.IsDirectory || node.Parent is null)
            {
                // 目录与根节点不承载文件 xattr 注入（目录 xattr 不在本写端支持范围）。
                // Directories and the root node do not carry file-xattr injection.
                continue;
            }

            if (!options.FileXattrs.TryGetValue(BuildImagePath(node), out var entries) ||
                entries is not { Count: > 0 })
            {
                continue;
            }

            node.XattrIbody = ErofsXattr.EncodeInline(entries);
        }
    }

    /// <summary>节点的镜像内相对路径（'/' 分隔；根为 ""）。Image-relative path of a node ('/'-separated; root is "").</summary>
    private static string BuildImagePath(BuildNode node)
    {
        var parts = new List<string>();
        for (var current = node; current.Parent is not null; current = current.Parent)
        {
            parts.Add(current.Name);
        }

        parts.Reverse();
        return string.Join('/', parts);
    }

    /// <summary>单节点构建状态。Per-node build state.</summary>
    private sealed class BuildNode
    {
        public string Name = string.Empty;
        public bool IsDirectory;
        public string FullPath = string.Empty;
        public long Size;
        public List<BuildNode> Children = [];
        public BuildNode? Parent;
        public ulong Nid;
        public long MetaOffset;
        public byte[] InlineData = [];   // 内联数据：目录单块负载 / 压缩 map header+索引表
        public long DirBlocks;           // 目录数据块数（≤1 内联，>1 数据区）
        public long DirBlockAddr = -1;
        public long FlatBlockAddr = -1;  // flat 文件数据区起始块
        public long FlatBlocks;
        public PclusterPlan[]? Pclusters;
        public uint CompressedBlocks;
        public uint ClusterCount;
        public bool CompactedIndexes;   // 压缩索引布局：false = legacy full 8B；true = compacted 4B amortized
        public byte[] XattrIbody = [];  // 编码后的 inline xattr ibody（空 = 无 xattr）。Encoded inline xattr ibody (empty = none).

        // ---- 新增：符号链接 / 设备 / 权限元数据。Symlink / device / permission metadata. ----
        public bool IsSymlink;
        public string SymlinkTarget = string.Empty;
        public bool IsDevice;
        public uint DeviceRdev;       // 设备号（写入 i_u）。Device number (written to i_u).
        public ushort DeviceMode;     // 设备完整 i_mode（含类型位）。Full device i_mode (incl. type bits).
        public uint Uid;              // 源文件 uid。Source file uid.
        public uint Gid;              // 源文件 gid。Source file gid.
        public long Mtime;            // 源文件 mtime（Unix 秒，0 = 使用构建时间）。Source mtime (unix secs, 0 = use build time).

        // ---- fragments / dedup。Fragments & dedup state. ----
        public bool IsFragment;               // 整文件汇入 packed inode（fragment inode）。Whole file packed into the packed inode.
        public long FragmentOffset = -1;      // 在 packed inode 文件内的字节偏移。Byte offset inside the packed inode file.
        public byte[]? ContentHash;           // 整文件 SHA-256（仅 Dedup 时计算）。Whole-file SHA-256 (only when Dedup is on).
        public BuildNode? DedupSource;        // flat 整块区复用的源节点（数据只写一次）。Shared flat whole-block source (written once).
        public bool IsPacked;                 // 合成 packed inode。Synthetic packed inode.
        public List<PackedFragment>? PackedFragments;  // packed inode 的碎片槽位（仅 IsPacked）。Packed fragment slots (IsPacked only).

        // ---- METABOX。Metabox state. ----
        public bool IsMetaboxResident;        // 该文件的 inode 记录存放在 metabox 容器（nid 带 bit63）。
                                              // The file's inode record lives in the metabox container (nid carries bit 63).
        public bool IsMetaboxContainer;       // 合成 metabox inode：数据区即容器。Synthetic metabox inode (data = the container).
        public long MetaboxSlot = -1;         // 在 metabox 容器内的槽位索引（* 32 = 容器字节偏移）。
                                              // Slot index inside the metabox container (* 32 = container byte offset).
    }

    /// <summary>packed inode 内的单个碎片区间（归属文件 + 文件内偏移/长度）。
    /// One packed fragment range (owning file + offset/length inside the packed file).</summary>
    private sealed class PackedFragment
    {
        public required BuildNode Owner;
        public long Offset;
        public long Length;
    }

    /// <summary>压缩 pcluster 规划。Per-pcluster compression plan.</summary>
    private sealed class PclusterPlan
    {
        public long SourceOffset;
        public int SourceLength;
        public int CompressedLength;
        public uint BlockAddress;
    }

    private static void BuildImage(BuildNode root, ErofsOptions options, Stream destination, long baseOffset, bool freshDestination)
    {
        using var output = new OffsetStream(destination, baseOffset);
        var blockSize = options.BlockSize;

        // ---- Device-table layout (multi-device). ----
        // 多设备设备表布局：当 ExtraDeviceCount > 0 时在 superblock 之后预留一块对齐的
        // 设备表（s_devt_slotoff 以块为单位；设备表起始偏移 = s_devt_slotoff * blockSize）。
        // 设备表含 ExtraDeviceCount + 1 个 128 字节槽位（设备 0 = 主设备，1..N = 额外设备），
        // inode 表在设备表之后块对齐起始；ExtraDeviceCount == 0 时不分配设备表。
        var extraDevices = options.ExtraDeviceCount;
        long inodeTableStart;
        long devtOffset;          // 设备表起始字节偏移（块对齐）；无设备表时未使用
        ushort devtSlotOff;       // s_devt_slotoff（以块为单位）
        if (extraDevices > 0)
        {
            // 设备表紧跟 superblock 所在块（块 0）之后的块 1。
            // Device table sits at block 1, right after the superblock's block 0.
            devtOffset = blockSize;
            var devtSlotCount = extraDevices + 1;
            var devtTableSize = (long)devtSlotCount * ErofsDeviceSlot.Size;
            inodeTableStart = AlignUp(devtOffset + devtTableSize, blockSize);
            devtSlotOff = (ushort)(devtOffset / blockSize);
        }
        else
        {
            // METABOX 需要 sb_extslots=1 的扩展超级块槽位（s_metabox_nid 位于 sb 偏移 128，占镜像
            // 1152..1168）；inode 表若仍自 1152 起会覆盖该槽位，故自 1168 向上 32 对齐
            // （放弃首个经典槽 36，metabox 模式下经典 nid 自 37 起）。
            // METABOX needs a 16-byte extended superblock slot (s_metabox_nid at superblock offset
            // 128, occupying image bytes 1152..1168); starting the inode table at 1152 would
            // overwrite it, so the table is 32-byte aligned past the extended slot (classic slot 36
            // is left unused; classic nids begin at 37 under metabox mode).
            inodeTableStart = options.Metabox
                ? AlignUp(InodeTableStart + ErofsSuperblock.ExtSlotSize, ErofsSuperblock.CompactInodeSize)
                : InodeTableStart;
            devtOffset = 0;
            devtSlotOff = 0;
        }

        // ---- Pass 1: collect in pre-order and plan per-node storage. ----
        // 第一遍：先序收集并规划各节点存储方式（决策与 nid 无关）。
        var ordered = new List<BuildNode>();
        CollectPreOrder(root, ordered);
        BindXattrs(ordered, options);

        // fragments：packed 区碎片槽位（每个唯一内容只追加一次）。Fragment slots appended to the
        // packed region during Pass 1 (one per unique content).
        var packedFragments = new List<PackedFragment>();
        long packedEnd = 0;
        // dedup：整文件 SHA-256 → 首个采用同一存储形态的节点。Whole-file SHA-256 → the first node
        // that used the same storage form.
        var dedupFlat = new Dictionary<string, BuildNode>();
        var dedupFragments = new Dictionary<string, BuildNode>();

        foreach (var node in ordered)
        {
            if (node.IsDirectory)
            {
                // 目录：整负载 ≤ 一块 → 内联（与 odm/mkfs 小目录一致，root 目录即内联）；
                // 否则多块目录数据写入数据区，Pass 4 按块独立序列化（名字不跨块）。
                // 块数按与 WriteDirBlocks 相同的贪心装箱精确计算（dirent 数组 + 名字区两段式，
                // 每块尾部的空余无法被后续条目复用，CeilDiv(总负载/块大小) 只是下界）。
                // Directories: an inline payload when the whole data fits one block (matching
                // odm/mkfs small dirs; the root dir is inline), otherwise multi-block data in the
                // data area, serialized per block in Pass 4 (names never cross blocks). The block
                // count is computed exactly with the same greedy bin-packing WriteDirBlocks uses
                // (the two-part "dirent array + name area" layout leaves block-tail slack unusable,
                // so CeilDiv(total/blockSize) is only a lower bound).
                var payloadLength = EstimateDirPayloadLength(node);
                node.DirBlocks = payloadLength <= blockSize
                    ? 1
                    : CountDirBlocks(BuildDirEntries(node), blockSize);
                continue;
            }

            // 符号链接与设备节点保持既有存储路径（内联目标 / i_u 设备号），不参与碎片与去重。
            // Symlinks and device nodes keep their existing storage path; they never take part in
            // fragment or dedup planning.
            var isDataFile = !node.IsSymlink && !node.IsDevice;
            if (node.Size <= 0)
            {
                continue;
            }

            if (isDataFile && options.Dedup)
            {
                node.ContentHash = HashFile(node);
            }

            // 文件存储（与 mkfs 一致）：
            //  - 压缩（legacy full / compacted）仅在开启 Compress 且确有收益时采用；
            //  - 未压缩 + Fragments：不足一块的文件整文件汇入 packed inode（fragment inode）；
            //  - 未压缩 + TailPacking：主体整块 flat，余数尾部内联在 inode 后（FLAT_INLINE）；
            //  - 未压缩且两者皆无：余数向上取整整块，全部写入数据区。
            // File storage (matching mkfs): compressed layouts only when requested and smaller;
            // otherwise, with Fragments a sub-block file is packed whole into the packed inode, with
            // TailPacking the remainder is inlined after the inode (FLAT_INLINE), and without either
            // the remainder is rounded up to whole blocks in the data area.
            if (options.Compress)
            {
                node.Pclusters = TryPlanCompressed(node, blockSize, options);
                if (node.Pclusters is not null)
                {
                    node.CompressedBlocks = (uint)node.Pclusters.Sum(p => (long)CeilDiv(p.CompressedLength, blockSize));
                    node.ClusterCount = (uint)CeilDiv(node.Size, blockSize);
                    node.CompactedIndexes = options.UseCompactedIndexes;
                    continue;
                }
            }

            var wholeBlocks = node.Size / blockSize;

            if (isDataFile && options.Fragments && wholeBlocks == 0)
            {
                // fragments：整文件（不足一块）汇入 packed inode。TailPacking 已在 Validate()
                // 强制关闭，此处不会双重内联。
                // Fragments: the whole file (smaller than one block) is merged into the packed
                // inode. Validate() already forced TailPacking off, so nothing is double-inlined.
                node.IsFragment = true;
                var hashKey = options.Dedup && node.ContentHash is not null
                    ? Convert.ToHexString(node.ContentHash)
                    : null;
                if (hashKey is not null && dedupFragments.TryGetValue(hashKey, out var existing))
                {
                    // 相同内容已在 packed 区：复用其偏移，不追加槽位。
                    // Identical content already packed: reuse the offset without a new slot.
                    node.FragmentOffset = existing.FragmentOffset;
                    continue;
                }

                node.FragmentOffset = AlignFragmentStart(packedEnd);
                packedFragments.Add(new PackedFragment { Owner = node, Offset = node.FragmentOffset, Length = node.Size });
                packedEnd = node.FragmentOffset + node.Size;
                if (hashKey is not null)
                {
                    dedupFragments[hashKey] = node;
                }

                continue;
            }

            // 常规 flat 存储。Regular flat storage.
            var remainder = node.Size % blockSize;
            if (options.TailPacking)
            {
                // 尾部打包：主体整块 flat，余数内联在 inode 之后。
                // Tail-packing: whole blocks flat, remainder inlined after the inode.
                node.FlatBlocks = wholeBlocks;
                if (remainder > 0)
                {
                    using var fs = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    fs.Position = wholeBlocks * blockSize;
                    var tail = new byte[remainder];
                    fs.ReadExactly(tail);
                    node.InlineData = tail;
                }
            }
            else
            {
                // 不启用尾部打包：余数向上对齐到整块，全部写入数据区。
                // No tail-packing: round up the remainder to a full block, all in data area.
                node.FlatBlocks = remainder > 0 ? wholeBlocks + 1 : wholeBlocks;
            }

            // dedup：整块区内容去重（TailPacking 的纯内联文件没有可共享的块区，跳过）。
            // Whole-block dedupe: identical files share one flat block range (pure-inline files
            // under tail-packing own no block range and are skipped).
            if (isDataFile && options.Dedup && node.ContentHash is not null && node.FlatBlocks > 0)
            {
                var hashKey = Convert.ToHexString(node.ContentHash);
                if (dedupFlat.TryGetValue(hashKey, out var source))
                {
                    node.DedupSource = source;
                    node.FlatBlocks = source.FlatBlocks;
                }
                else
                {
                    dedupFlat[hashKey] = node;
                }
            }
        }

        // ---- fragments：合成 packed inode（未压缩的普通 regular flat 文件，数据区承载全部
        // 碎片）。仅当确有碎片时创建并加入列表，随后 Pass 2/3/4 与其他 inode 一视同仁。
        // ---- Fragments: the synthetic packed inode (an ordinary uncompressed flat file whose data
        // area holds all packed fragments). Only created when there is real fragment content.
        BuildNode? packedNode = null;
        if (packedFragments.Count > 0)
        {
            packedNode = new BuildNode
            {
                IsPacked = true,
                Size = packedEnd,
                FlatBlocks = CeilDiv(packedEnd, blockSize),
                PackedFragments = packedFragments,
            };
            ordered.Add(packedNode);
        }

        // ---- METABOX：选择容器驻留节点并创建合成 metabox inode。容器 = 逐文件紧凑 inode 槽，
        // 数据区存放（metabox inode 本身位于经典表，i_u 指向容器数据块）。解析侧仅能自 metabox
        // 槽位恢复“纯 32 字节 compact 记录”，故只有纯 flat 全块存储、无压缩/碎片/xattr 的普通
        // 文件入容器；目录（含内联目录数据）、符号链接、设备与其余节点留在经典 inode 表。
        // ---- METABOX: pick container-resident nodes and create the synthetic metabox inode. The
        // container is a per-file compact-inode slot table living in the data area (the metabox
        // inode itself sits in the classic table with its i_u pointing at the container blocks).
        // The read side can only recover pure 32-byte compact records from a metabox slot, so only
        // plain flat whole-block regular files without compression/fragments/xattrs go into the
        // container; directories (incl. inlined directory payloads), symlinks, devices and the rest
        // stay in the classic inode table.
        List<BuildNode>? metaboxResident = null;
        BuildNode? metaboxNode = null;
        if (options.Metabox)
        {
            metaboxResident = ordered.Where(IsMetaboxEligible).ToList();
            if (metaboxResident.Count > 0)
            {
                foreach (var resident in metaboxResident)
                {
                    resident.IsMetaboxResident = true;
                }

                metaboxNode = new BuildNode
                {
                    IsMetaboxContainer = true,
                    Size = (long)metaboxResident.Count * ErofsSuperblock.CompactInodeSize,
                    FlatBlocks = CeilDiv((long)metaboxResident.Count * ErofsSuperblock.CompactInodeSize, blockSize),
                };
                ordered.Add(metaboxNode);
            }
        }

        // ---- Pass 2: assign nids / metadata offsets. ----
        // 第二遍：分配 nid 与元数据偏移。每个 inode 记录 = OnDiskSize + 内联数据
        //（目录单块负载 / 压缩 map header + 索引表），记录不得跨元数据块（fsck 校验
        // "inline data cross block boundary"），跨块时整体对齐到下一块起点。
        // METABOX 下容器驻留文件的记录进入容器（nid = bit63 | 槽号），不占用经典表。
        var metaOff = inodeTableStart;
        if (metaboxResident is not null)
        {
            for (var i = 0; i < metaboxResident.Count; i++)
            {
                var resident = metaboxResident[i];
                resident.MetaboxSlot = i;
                resident.MetaOffset = -1;
                resident.Nid = NidMetaboxFlag | (ulong)i;
            }
        }

        foreach (var node in ordered)
        {
            if (node.IsMetaboxResident)
            {
                continue; // 容器槽位已分配，经典表无记录。Slot assigned; no classic record.
            }

            var recordSize = GetInodeRecordSize(node, blockSize);
            if (metaOff % blockSize + recordSize > blockSize)
            {
                metaOff = AlignUp(metaOff, blockSize);
            }

            node.MetaOffset = metaOff;
            node.Nid = (ulong)(metaOff / 32);
            metaOff += AlignUp(recordSize, 32);
        }

        // ---- Pass 3: allocate data-area blocks. ----
        // 第三遍：分配数据区起始块。
        var dataBlock = AlignUp(metaOff, blockSize) / blockSize;
        foreach (var node in ordered)
        {
            if (node.IsDirectory)
            {
                if (node.DirBlocks > 1)
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
                    dataBlock += CeilDiv(p.CompressedLength, blockSize);
                }
            }
            else if (node.FlatBlocks > 0)
            {
                // 整块区去重：命中同哈希的文件直接复用源节点已分配的块地址（源先出现，
                // 其地址必然已就绪）。Deduped files reuse the source's already-assigned block
                // address (the source precedes this node, so its address is always ready).
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
        if (totalBlocks > uint.MaxValue)
        {
            throw new InvalidOperationException($"Image block count {totalBlocks} exceeds the 32-bit superblock field.");
        }

        if (root.Nid > ushort.MaxValue)
        {
            throw new InvalidOperationException($"Root nid {root.Nid} exceeds the 16-bit superblock field.");
        }

        // ---- Pass 4: stream the image out. ----
        // 第四遍：流式写出（此时 nid 已知，目录 dirent 可引用子节点 nid）。
        var finalSize = checked(totalBlocks * blockSize);
        var oldLength = output.Length;
        output.SetLength(finalSize);
        if (!freshDestination)
        {
            // Newly extended storage is already zero; clear only bytes that overlap old content.
            ZeroImage(output, Math.Min(oldLength, finalSize));
        }

        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var featureIncompat = options.Compress
            ? (options.Algorithm == ErofsCompressionAlgorithm.Lz4
                ? ErofsSuperblock.IncompatLz4_0Padding
                : ErofsSuperblock.IncompatBigPcluster)
            : 0;
        // 多设备镜像需要 DEVICE_TABLE 不兼容特性位，解析侧据此读取设备表。
        // Multi-device images require the DEVICE_TABLE incompat bit; the parser keys off it.
        if (extraDevices > 0)
        {
            featureIncompat |= ErofsSuperblock.IncompatDeviceTable;
        }

        // fragments：存在 packed inode 时置 FRAGMENTS 位（0x20，dedupe 的现代别名同一位）。
        // The FRAGMENTS incompat bit is set whenever a packed inode exists.
        if (packedNode is not null)
        {
            featureIncompat |= ErofsSuperblock.IncompatFragments;
        }

        // METABOX：存在容器时置 METABOX 不兼容位（0x100）并启用一个扩展超级块槽位
        //（s_metabox_nid 位于偏移 128）。erofs-utils 1.8.6 的 fsck/dump 不认识该位
        //（"unidentified incompatible feature 100"），会拒绝此类镜像；解析侧自回环不受影响。
        // METABOX: with a container, set the METABOX incompat bit (0x100) and enable one extended
        // superblock slot (s_metabox_nid at offset 128). erofs-utils 1.8.6 fsck/dump reject such
        // images ("unidentified incompatible feature 100"); the self round-trip reader is unaffected.
        if (metaboxNode is not null)
        {
            featureIncompat |= ErofsSuperblock.IncompatMetabox;
        }

        var superblock = new ErofsSuperblock
        {
            MagicValue = ErofsSuperblock.Magic,
            BlockSizeBits = (byte)BitOperations.Log2((uint)blockSize),
            RootNid = (ushort)root.Nid,
            Inos = (ulong)ordered.Count,
            BuildTime = now,
            Blocks = (uint)totalBlocks,
            MetaBlkAddr = 0,
            AvailableComprAlgs = options.Compress ? (ushort)(1 << (int)options.Algorithm) : (ushort)0,
            FeatureCompat = ErofsSuperblock.CompatSbChecksum,
            FeatureIncompat = featureIncompat,
            ExtraDevices = (ushort)extraDevices,
            DevtSlotoff = devtSlotOff,
            PackedNid = packedNode?.Nid ?? 0,
            SbExtSlots = metaboxNode is not null ? (byte)1 : (byte)0,
            MetaBoxNid = metaboxNode?.Nid ?? 0,
            Uuid = Guid.NewGuid().ToByteArray(),
            VolumeName = options.VolumeName,
        };
        // METABOX 需 144 字节超级块缓冲区（128 字节主槽 + 16 字节扩展槽）容纳 s_metabox_nid。
        // METABOX needs a 144-byte superblock buffer (128-byte main slot + one 16-byte extended slot)
        // to hold s_metabox_nid.
        var superblockBuffer = new byte[metaboxNode is not null
            ? ErofsSuperblock.SuperblockSize + ErofsSuperblock.ExtSlotSize
            : ErofsSuperblock.SuperblockSize];
        superblock.WriteTo(superblockBuffer);
        // 计算 CRC32 覆盖超级块缓冲区（checksum 字段为 0），存于 offset 4。
        // Compute CRC32 over the superblock buffer (checksum field = 0), store at offset 4.
        var crc = System.IO.Hashing.Crc32.Hash(superblockBuffer);
        crc.CopyTo(superblockBuffer, 4); // Offset 4 = s_checksum field
        WriteAt(output, ErofsSuperblock.SuperblockOffset, superblockBuffer, superblockBuffer.Length);

        // 设备表（多设备）：写入 ExtraDeviceCount + 1 个 128 字节槽位。
        // 槽 0 = 主设备（blocks = 镜像总块数，mapped_blkaddr = 0）；
        // 槽 1..N = 额外设备占位（blocks = 0，mapped_blkaddr = 0），真正的多设备文件
        // 分布需要上层 API 支持多输入源，本期仅确保设备表结构正确且可被 fsck.erofs 识别。
        // Device table (multi-device): write ExtraDeviceCount + 1 128-byte slots.
        // Slot 0 = primary device (blocks = total image blocks, mapped_blkaddr = 0);
        // slots 1..N = extra-device placeholders (blocks = 0, mapped_blkaddr = 0). Real
        // multi-device file distribution needs an upper-layer multi-source API; this pass only
        // ensures the device-table structure is correct and fsck.erofs-recognizable.
        if (extraDevices > 0)
        {
            var slotBuffer = new byte[ErofsDeviceSlot.Size];
            var slotCount = extraDevices + 1;
            for (var i = 0; i < slotCount; i++)
            {
                var slot = i == 0
                    ? new ErofsDeviceSlot { Blocks = (uint)totalBlocks, MappedBlkAddr = 0 }
                    : new ErofsDeviceSlot { Blocks = 0, MappedBlkAddr = 0 };
                slot.WriteTo(slotBuffer);
                WriteAt(output, devtOffset + i * ErofsDeviceSlot.Size, slotBuffer, slotBuffer.Length);
            }
        }

        // 数据区（先写数据，随后元数据中的压缩索引表才能引用块号）。
        foreach (var node in ordered)
        {
            if (node.IsDirectory)
            {
                if (node.DirBlockAddr >= 0)
                {
                    WriteDirBlocks(output, node, blockSize);
                }
            }
            else if (node.IsPacked)
            {
                // packed inode 数据 = 全部碎片按偏移拼接（槽位间空白已由 SetLength 置零）。
                // The packed inode data is the concatenation of every fragment slot (gaps between
                // slots are already zeroed by SetLength).
                WritePackedData(output, node, blockSize);
            }
            else if (node.Pclusters is not null)
            {
                WriteCompressedData(output, node, blockSize, options);
            }
            else if (node.IsMetaboxContainer)
            {
                // metabox inode 数据 = 容器驻留文件的紧凑 inode 记录槽表。
                // The metabox inode data is the compact-inode slot table of every resident file.
                WriteMetaboxContainer(output, node, blockSize, now, metaboxResident!);
            }
            else if (node.FlatBlockAddr >= 0 && node.FlatBlocks > 0 && node.DedupSource is null)
            {
                // Copy only the actual file content; if no tail-packing, the padded last block
                // was already zeroed by ZeroImage. Deduped files were already written by their
                // DedupSource and are skipped here.
                var copyLen = Math.Min(node.FlatBlocks * blockSize, node.Size);
                CopyFileRange(output, node, 0, copyLen, node.FlatBlockAddr * blockSize);
            }
        }

        // 元数据区：inode 记录 + 内联数据 + 压缩索引表。
        // 容器驻留文件的记录已在 metabox 容器写出（见数据区循环），此处跳过。
        foreach (var node in ordered)
        {
            if (node.IsMetaboxResident)
            {
                continue; // inode 记录在 metabox 容器内。Its inode record lives in the metabox container.
            }

            var inode = BuildInode(node, blockSize, now);
            var inodeBuffer = new byte[inode.OnDiskSize];
            inode.WriteTo(inodeBuffer);
            WriteAt(output, node.MetaOffset, inodeBuffer, inodeBuffer.Length);
            // inline xattr ibody 紧随 inode（大小 = XattrTotalSize）。
            // The inline xattr ibody follows the inode (size = XattrTotalSize).
            if (node.XattrIbody.Length > 0)
            {
                WriteAt(output, node.MetaOffset + inode.OnDiskSize, node.XattrIbody, node.XattrIbody.Length);
            }

            var cursor = node.MetaOffset + inode.OnDiskSize + inode.XattrTotalSize;

            if (node.IsDirectory)
            {
                // 目录单块内联数据紧随 inode；多块目录已在数据区写出。
                if (node.DirBlocks <= 1)
                {
                    WriteAt(output, cursor, BuildDirPayload(node), (int)EstimateDirPayloadLength(node));
                }
            }
            else if (node.IsFragment)
            {
                // fragment inode：8 字节 map header 指向 packed inode（读端按 8 字节对齐定位）。
                // Fragment inode: the 8-byte z_erofs_map_header pointing into the packed inode (the
                // reader locates it 8-byte aligned).
                WriteFragmentHeader(output, node, inode, options);
            }
            else if (node.Pclusters is not null)
            {
                WriteCompressedIndexes(output, cursor, node, blockSize, options);
            }
            else if (node.InlineData.Length > 0)
            {
                // 文件尾部（tail-packing）内联数据紧随 inode。
                // The file remainder (tail-packing) is inlined right after the inode.
                WriteAt(output, cursor, node.InlineData, node.InlineData.Length);
            }
        }

        output.Position = finalSize;
    }

    /// <summary>估算目录负载长度（dirent 区 + 名字区；含 "." 与 ".." 两个合成条目）。
    /// Estimates the directory payload length (dirent area + name area, incl. "." and "..").</summary>
    private static long EstimateDirPayloadLength(BuildNode node)
        => 2 * ErofsDirectoryEntry.DirentSize + 1 + 2
           + node.Children.Sum(child => (long)ErofsDirectoryEntry.DirentSize + Encoding.UTF8.GetByteCount(child.Name));

    /// <summary>
    /// 构造目录条目：`. ` 与 `..` 在前（mkfs 默认不省略点条目，fsck 强制检查二者存在），
    /// 随后子条目按 UTF-8 字节序排序（与 mkfs mergesort / fsck 递增校验一致）。
    /// <para>Builds the directory entries: "." and ".." first (mkfs keeps dot entries by default
    /// and fsck enforces their presence), then children ordered by their UTF-8 bytes (matches the
    /// mergesort in mkfs and fsck's strictly-increasing check).</para>
    /// </summary>
    private static List<DirEntry> BuildDirEntries(BuildNode node)
    {
        var dotNid = node.Nid;
        var dotDotNid = node.Parent?.Nid ?? node.Nid; // root 的 .. 指向自身。Root's .. points at itself.
        var entries = new List<DirEntry>(node.Children.Count + 2)
        {
            new DirEntry(new BuildNode { Nid = dotNid, IsDirectory = true, Name = "." }, "."u8.ToArray()),
            new DirEntry(new BuildNode { Nid = dotDotNid, IsDirectory = true, Name = ".." }, ".."u8.ToArray()),
        };
        entries.AddRange(node.Children
            .Select(child => new DirEntry(child, Encoding.UTF8.GetBytes(child.Name)))
            .OrderBy(e => e.NameBytes, ByteArrayComparer.Instance));
        return entries;
    }

    /// <summary>
    /// 目录单块负载：dirent 数组在前、名字区紧随（de[i].nameoff = 名字区起点 + 之前名字长
    /// 之和）。与内核 fill_dentries / mkfs fill_dirblock 同构。
    /// <para>Single-block directory payload: the dirent array first, then the contiguous name area
    /// (de[i].nameoff = name-area start + the sum of preceding name lengths), isomorphic to the
    /// kernel fill_dentries / mkfs fill_dirblock.</para>
    /// </summary>

    /// <summary>根据 BuildNode 推导 dirent 文件类型。Derives the dirent file type from a BuildNode.</summary>
    private static byte DirentFileType(BuildNode node)
    {
        if (node.IsDirectory) return ErofsInode.FileTypeDirectory;
        if (node.IsSymlink) return ErofsInode.FileTypeSymlink;
        if (node.IsDevice)
        {
            var type = node.DeviceMode & ErofsInode.ModeTypeMask;
            if (type == ErofsInode.ModeCharacterDevice) return ErofsInode.FileTypeCharacterDevice;
            if (type == ErofsInode.ModeBlockDevice) return ErofsInode.FileTypeBlockDevice;
        }

        return ErofsInode.FileTypeRegularFile;
    }

    private static byte[] BuildDirPayload(BuildNode node)
    {
        var sorted = BuildDirEntries(node);
        var totalNames = sorted.Sum(e => e.NameBytes.Length);
        var payload = new byte[sorted.Count * ErofsDirectoryEntry.DirentSize + totalNames];
        var nameOff = sorted.Count * ErofsDirectoryEntry.DirentSize;
        for (var i = 0; i < sorted.Count; i++)
        {
            var (child, nameBytes) = (sorted[i].Child, sorted[i].NameBytes);
            var slot = payload.AsSpan(i * ErofsDirectoryEntry.DirentSize, ErofsDirectoryEntry.DirentSize);
            BinaryPrimitives.WriteUInt64LittleEndian(slot, child.Nid);
            BinaryPrimitives.WriteUInt16LittleEndian(slot.Slice(8), (ushort)nameOff);
            slot[10] = DirentFileType(child);
            slot[11] = 0;
            nameBytes.CopyTo(payload.AsSpan(nameOff, nameBytes.Length));
            nameOff += nameBytes.Length;
        }

        return payload;
    }

    private readonly record struct DirEntry(BuildNode Child, byte[] NameBytes);

    /// <summary>按 UTF-8 字节序比较名字（与内核 memcmp / fsck 校验一致）。Compares names by UTF-8 bytes.</summary>
    private sealed class ByteArrayComparer : IComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public int Compare(byte[]? x, byte[]? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var n = Math.Min(x.Length, y.Length);
            for (var i = 0; i < n; i++)
            {
                var d = x[i].CompareTo(y[i]);
                if (d != 0) return d;
            }

            return x.Length.CompareTo(y.Length);
        }
    }

    /// <summary>
    /// 按与 <see cref="WriteDirBlocks"/> 完全相同的贪心装箱计算多块目录所需块数
    /// （每块 = dirent 数组 + 紧随的名字区；名字不跨块，块尾空余不参与后续打包）。
    /// <para>Computes the exact number of directory blocks using the same greedy bin-packing as
    /// <see cref="WriteDirBlocks"/> (each block = a dirent array followed by its name area; names
    /// never cross blocks and block-tail slack is not reused by later entries).</para>
    /// </summary>
    private static long CountDirBlocks(List<DirEntry> sorted, int blockSize)
    {
        var blocks = 0L;
        var index = 0;
        while (index < sorted.Count)
        {
            var direntCount = 0;
            var namesLength = 0;
            while (index < sorted.Count)
            {
                var entry = sorted[index];
                var need = (direntCount + 1) * ErofsDirectoryEntry.DirentSize
                           + namesLength + entry.NameBytes.Length;
                if (need > blockSize)
                {
                    break; // 放不下该条目：封块。Cannot fit: seal the block.
                }

                direntCount++;
                namesLength += entry.NameBytes.Length;
                index++;
            }

            if (direntCount == 0)
            {
                // 防御：单个条目超过一块时仍消耗一块并推进（实际不可能：dirent+名字 ≤ 12+255）。
                // Guard: a single oversized entry still consumes a block (unreachable in practice:
                // a dirent + name is at most 12 + 255 bytes).
                index++;
            }

            blocks++;
        }

        return blocks;
    }

    /// <summary>
    /// 多块目录：把排序后的条目按块切分，每块是独立的“dirent 数组 + 名字区”
    /// （与单块内联负载 <see cref="BuildDirPayload"/> 同构：de[0].nameoff = dirent 数组字节数，
    /// 名字紧随数组连续存放、不跨块；块尾空余置零）。
    /// <para>Multi-block directory: sorted entries are split into independent blocks, each holding a
    /// "dirent array + name area" (isomorphic to the single-block inline payload
    /// <see cref="BuildDirPayload"/>: de[0].nameoff is the dirent-array byte count, names follow the
    /// array contiguously and never cross blocks; the block tail is zeroed).</para>
    /// </summary>
    private static void WriteDirBlocks(Stream output, BuildNode node, int blockSize)
    {
        var sorted = BuildDirEntries(node);
        var index = 0;
        for (var block = 0L; block < node.DirBlocks; block++)
        {
            if (index >= sorted.Count)
            {
                break; // 无剩余条目：尾部块保持全零。No entries left: leave the tail block zeroed.
            }

            // 贪心装填本块（与 CountDirBlocks 同一规则，保证写满 DirBlocks 且不越界）。
            // Greedily fill this block (same rule as CountDirBlocks, so all DirBlocks are written
            // without ever exceeding the reserved data range).
            var direntCount = 0;
            var namesLength = 0;
            while (index < sorted.Count)
            {
                var entry = sorted[index];
                var need = (direntCount + 1) * ErofsDirectoryEntry.DirentSize
                           + namesLength + entry.NameBytes.Length;
                if (need > blockSize)
                {
                    break; // 放不下该条目：封块。Cannot fit: seal the block.
                }

                direntCount++;
                namesLength += entry.NameBytes.Length;
                index++;
            }

            if (direntCount == 0)
            {
                break;
            }

            var blockData = new byte[blockSize];
            var nameOff = direntCount * ErofsDirectoryEntry.DirentSize;
            for (var i = 0; i < direntCount; i++)
            {
                var (child, nameBytes) = (sorted[index - direntCount + i].Child, sorted[index - direntCount + i].NameBytes);
                var slot = blockData.AsSpan(i * ErofsDirectoryEntry.DirentSize, ErofsDirectoryEntry.DirentSize);
                BinaryPrimitives.WriteUInt64LittleEndian(slot, child.Nid);
                BinaryPrimitives.WriteUInt16LittleEndian(slot.Slice(8), (ushort)nameOff);
                slot[10] = DirentFileType(child);
                slot[11] = 0;
                nameBytes.CopyTo(blockData.AsSpan(nameOff, nameBytes.Length));
                nameOff += nameBytes.Length;
            }

            WriteAt(output, (node.DirBlockAddr + block) * blockSize, blockData, blockData.Length);
        }
    }

    private static long GetInodeRecordSize(BuildNode node, int blockSize)
    {
        var header = GetInodeHeaderSize(node) + node.XattrIbody.Length;

        // 符号链接：目标字符串内联在 inode 之后。Symlink: target string inlined after inode.
        if (node.IsSymlink)
        {
            return header + node.Size;
        }

        // 设备节点：无内联数据。Device nodes: no inline data.
        if (node.IsDevice)
        {
            return header;
        }

        if (node.IsDirectory)
        {
            return node.DirBlocks <= 1 ? header + EstimateDirPayloadLength(node) : header;
        }

        if (node.Pclusters is not null)
        {
            if (node.CompactedIndexes)
            {
                // compacted 4B amortized 记录 = inode + 8B map header + 位打包索引表
                //（每 2 簇一个 8B pack：2×encodebits 位 + pack 尾 4B blkaddr）。
                // Compacted 4B-amortized record = inode + 8B map header + the bit-packed index
                // table (one 8-byte pack per two clusters: 2×encodebits bits + a 4-byte trailing
                // pack-start block address).
                return header + ZErofsMapHeader.Size + 8L * CeilDiv(node.ClusterCount, 2L);
            }

            // 压缩记录 = inode + map header(8B) + 保留(8B) + nclusters × 8B 索引。
            // Compressed record = inode + 8B map header + 8B pad + nclusters × 8B index.
            return header + ZErofsMapHeader.Size + 8L + node.ClusterCount * ZErofsLclusterIndex.Size;
        }

        if (node.IsFragment)
        {
            // fragment inode 记录 = inode + xattr + 8 对齐填充 + 8B fragment map header（读端按
            // inode+xattr 后 8 字节对齐读取 header，故记录须含填充）。
            // Fragment record = inode + xattr + 8-byte alignment pad + the 8-byte fragment map
            // header (the reader locates the header 8-byte aligned after inode + xattrs, so the
            // record must cover the pad).
            return AlignUp(header, 8) + ZErofsMapHeader.Size;
        }

        // 未压缩文件：整块部分在数据区，尾部内联在 inode 之后。
        // Uncompressed file: whole blocks live in the data area, the remainder follows the inode.
        return header + node.InlineData.Length;
    }

    private static long GetInodeHeaderSize(BuildNode node)
    {
        // compact inode(32B) 是 mkfs 默认（odm 文件/目录均为 compact）；仅当 size 超 32 位、
        // 目录 nlink 超 16 位时使用 extended(64B)。
        // The compact 32-byte inode is the mkfs default (odm files/dirs are compact); the extended
        // 64-byte form is only needed for sizes beyond 32 bits or 16-bit directory nlinks.
        if (node.IsDirectory)
        {
            var subdirs = (ulong)node.Children.Count(child => child.IsDirectory);
            return node.Size > uint.MaxValue || 2 + subdirs > ushort.MaxValue
                ? ErofsInode.ExtendedSize
                : ErofsInode.CompactSize;
        }

        return node.Size > uint.MaxValue ? ErofsInode.ExtendedSize : ErofsInode.CompactSize;
    }

    private static ErofsInode BuildInode(BuildNode node, int blockSize, ulong now)
    {
        var inode = new ErofsInode();
        if (node.Size > uint.MaxValue)
        {
            inode.Advise |= ErofsInode.FlagExtended;
        }

        // 使用源文件 mtime（非零时），否则回退到构建时间。Use source mtime if available, else build time.
        var effectiveTime = node.Mtime > 0 ? (ulong)node.Mtime : now;
        inode.CTime = effectiveTime;
        inode.CTimeNsec = 0;
        inode.Uid = node.Uid;
        inode.Gid = node.Gid;
        inode.Generation = (uint)effectiveTime;
        // inline xattr ibody 紧随 inode：XattrICount 与 ibody 长度满足
        // XattrTotalSize = 12 + 4 * (XattrICount - 1)（读端按该公式还原 ibody 大小）。
        // The inline xattr ibody follows the inode: XattrICount satisfies
        // XattrTotalSize = 12 + 4 * (XattrICount - 1) so the reader recovers the ibody size.
        if (node.XattrIbody.Length > 0)
        {
            inode.XattrICount = ErofsXattr.GetICountForIbodyLength(node.XattrIbody.Length);
        }

        // 符号链接：S_IFLNK|0777，目标字符串内联。Symlink: S_IFLNK|0777, target inlined.
        if (node.IsSymlink)
        {
            inode.Mode = (ushort)(ErofsInode.ModeSymlink | 0x1FF); // S_IFLNK | 0777
            inode.Nlink = 1;
            inode.Size = (ulong)node.Size;
            inode.Advise |= ErofsInode.DataLayoutInline;
            inode.U = uint.MaxValue; // EROFS_NULL_ADDR
            node.InlineData = Encoding.UTF8.GetBytes(node.SymlinkTarget);
            return inode;
        }

        // 字符/块设备：DataLayoutFlat，设备号写入 i_u。Char/block device: flat, rdev in i_u.
        if (node.IsDevice)
        {
            inode.Mode = node.DeviceMode;
            inode.Nlink = 1;
            inode.Size = 0;
            inode.Advise |= ErofsInode.DataLayoutFlat;
            inode.U = node.DeviceRdev;
            return inode;
        }

        if (node.IsDirectory)
        {
            inode.Mode = (ushort)(ErofsInode.ModeDirectory | 0x1ED); // S_IFDIR | 0755
            inode.Nlink = (ulong)(2 + node.Children.Count(child => child.IsDirectory));
            inode.Size = node.DirBlocks <= 1
                ? (ulong)EstimateDirPayloadLength(node)
                : (ulong)(node.DirBlocks * blockSize);
            if (node.DirBlocks <= 1)
            {
                // 目录数据内联：i_u = 空洞哨兵（EROFS_NULL_ADDR）。
                inode.Advise |= ErofsInode.DataLayoutInline;
                inode.U = uint.MaxValue;
            }
            else
            {
                inode.Advise |= ErofsInode.DataLayoutFlat;
                inode.U = (uint)node.DirBlockAddr;
            }
        }
        else
        {
            inode.Mode = (ushort)(ErofsInode.ModeRegularFile | 0x1A4); // S_IFREG | 0644
            inode.Nlink = 1;
            inode.Size = (ulong)node.Size;
            if (node.Pclusters is not null)
            {
                // 压缩：i_u 存压缩后物理块数。索引布局按 <see cref="ErofsOptions.UseCompactedIndexes"/>
                // 选择：legacy full 8B 索引（EROFS_INODE_COMPRESSED_FULL，默认）或 compacted
                // 4B amortized 位打包索引（EROFS_INODE_COMPRESSED_COMPACT）。
                // Compressed: i_u holds the compressed physical block count. The index layout is
                // selected via ErofsOptions.UseCompactedIndexes: legacy full 8-byte indexes
                // (COMPRESSED_FULL, default) or compacted 4-byte amortized bit-packed indexes
                // (COMPRESSED_COMPACT).
                inode.Advise |= node.CompactedIndexes
                    ? ErofsInode.DataLayoutCompactIndexes
                    : ErofsInode.DataLayoutCompressed;
                inode.U = node.CompressedBlocks;
            }
            else if (node.IsFragment)
            {
                // 整文件汇入 packed inode：以压缩布局承载 fragment 元数据（8 字节 map header 的
                // h_clusterbits bit7 声明整文件 fragment，i_u 无物理块语义，恒 0）。
                // Whole file packed into the packed inode: the compressed layout carries the
                // fragment metadata (h_clusterbits bit 7 of the 8-byte map header marks a
                // whole-file fragment; i_u carries no physical-block meaning and stays 0).
                inode.Advise |= ErofsInode.DataLayoutCompressed;
                inode.U = 0;
            }
            else if (node.FlatBlocks > 0)
            {
                // 主体整块 flat：有尾部内联时为 FLAT_INLINE（tail-packing），否则纯 FLAT。
                // Whole blocks are flat; with an inline remainder this is FLAT_INLINE
                // (tail-packing), otherwise plain FLAT.
                inode.Advise |= node.InlineData.Length > 0 ? ErofsInode.DataLayoutInline : ErofsInode.DataLayoutFlat;
                inode.U = (uint)node.FlatBlockAddr;
            }
            else if (node.InlineData.Length > 0)
            {
                // 整个文件不足一块：全部内联，i_u = 空洞哨兵（EROFS_NULL_ADDR）。
                // The whole file is smaller than a block: fully inlined, i_u = hole sentinel.
                inode.Advise |= ErofsInode.DataLayoutInline;
                inode.U = uint.MaxValue;
            }
            else
            {
                // 空文件。Empty file.
                inode.Advise |= ErofsInode.DataLayoutFlat;
                inode.U = 0;
            }
        }

        return inode;
    }

    private static PclusterPlan[]? TryPlanCompressed(BuildNode node, int blockSize, ErofsOptions options)
    {
        // 与 mkfs 同款：一个 pcluster 覆盖若干整逻辑簇（lcluster = 块大小），压缩输出 ≤ 一块，
        // 右对齐写入物理块（前导 0 由 0-padding 特性容忍）。逐簇试探累计输入直至压缩输出
        // 逼近一块；单簇都压不进一块（或毫无收益）时整文件退回 flat。
        // A pcluster covers several whole logical clusters (lcluster = block size) whose compressed
        // output fits one block, written right-aligned (leading zeros tolerated via 0-padding).
        // We greedily accumulate clusters until the output approaches one block; if a single
        // cluster cannot fit into one block the file falls back to flat.
        using var fs = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var clusterSize = blockSize;
        var totalClusters = CeilDiv(node.Size, clusterSize);
        var raw = new byte[clusterSize * 64];
        var compressed = new byte[ErofsCompression.MaximumOutputSize((ErofsAlgorithm)options.Algorithm, raw.Length)];
        var plans = new List<PclusterPlan>();
        var pos = 0L;
        var clusterIndex = 0L;
        while (clusterIndex < totalClusters)
        {
            // 当前 pcluster 已接受的长度与压缩输出。
            var acceptedLen = 0;
            var acceptedOut = 0;
            for (var take = 1; clusterIndex + take <= totalClusters && take <= 64; take++)
            {
                var candidateLen = (int)Math.Min(node.Size - pos, (long)take * clusterSize);
                fs.Position = pos;
                fs.ReadExactly(raw, 0, candidateLen);
                var outLen = ErofsCompression.AlgorithmCompress(
                    (ErofsAlgorithm)options.Algorithm,
                    raw, 0, candidateLen, compressed, 0, compressed.Length,
                    Math.Max(1, options.CompressionLevel));
                if (outLen <= 0 || outLen >= blockSize - 8)
                {
                    // 输出超一块：终止扩展（保留上一个成功的窗口）。
                    // Output would exceed one block: stop growing (keep the last good window).
                    break;
                }

                acceptedLen = candidateLen;
                acceptedOut = outLen;
                if (candidateLen == node.Size - pos)
                {
                    break; // 到文件尾。End of file.
                }
            }

            if (acceptedLen <= 0 || acceptedOut >= acceptedLen && clusterIndex + 1 == totalClusters && acceptedOut == 0)
            {
                return null; // 单簇无法压缩。A single cluster is incompressible.
            }

            if (acceptedOut <= 0)
            {
                return null;
            }

            plans.Add(new PclusterPlan { SourceOffset = pos, SourceLength = acceptedLen, CompressedLength = acceptedOut });
            pos += acceptedLen;
            clusterIndex += CeilDiv(acceptedLen, clusterSize);
        }

        // 压缩后物理块数必须少于 flat。Compressed blocks must be fewer than flat storage.
        if (plans.Sum(p => (long)CeilDiv(p.CompressedLength, blockSize)) >= totalClusters)
        {
            return null;
        }

        return plans.ToArray();
    }

    private static void WriteCompressedData(Stream output, BuildNode node, int blockSize, ErofsOptions options)
    {
        using var fs = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var raw = new byte[blockSize * 64];
        var compressed = new byte[ErofsCompression.MaximumOutputSize((ErofsAlgorithm)options.Algorithm, raw.Length)];
        foreach (var p in node.Pclusters!)
        {
            fs.Position = p.SourceOffset;
            fs.ReadExactly(raw, 0, p.SourceLength);
            var outLen = ErofsCompression.AlgorithmCompress(
                (ErofsAlgorithm)options.Algorithm,
                raw, 0, p.SourceLength, compressed, 0, compressed.Length,
                Math.Max(1, options.CompressionLevel));
            if (outLen != p.CompressedLength)
            {
                throw new InvalidDataException(
                    $"Compressed length mismatch for '{node.FullPath}': {outLen} != {p.CompressedLength}.");
            }

            // 右对齐写入物理块（前导 0 由 0-padding 特性容忍，读端 fixup 跳过）。
            // Write right-aligned in the physical block; the leading zeros are tolerated by the
            // 0-padding feature and skipped by the reader's fixup.
            var blockBytes = CeilDiv(p.CompressedLength, blockSize) * blockSize;
            var start = checked(p.BlockAddress * blockSize);
            if (outLen < blockBytes)
            {
                WriteZeroes(output, start, blockBytes - outLen);
            }

            WriteAt(output, start + blockBytes - outLen, compressed, outLen);
        }
    }

    private static void WriteCompressedIndexes(Stream output, long indexOffset, BuildNode node, int blockSize, ErofsOptions options)
    {
        // z_erofs_map_header（8B，位于 inode 记录内联区起点）：h_advise、h_algorithmtype、
        // h_clusterbits。h_algorithmtype 低 4 位为 HEAD1 算法（与内核 Z_EROFS_COMPRESSION_*
        // 一致；默认 LZ4=0）。
        // The 8-byte z_erofs_map_header at the start of the inode's inline area: h_advise,
        // h_algorithmtype and h_clusterbits. The low nibble of h_algorithmtype is the HEAD1
        // algorithm (matching the kernel Z_EROFS_COMPRESSION_*; LZ4 = 0 by default).
        var header = new byte[ZErofsMapHeader.Size];
        header[4] = 0; // h_advise low
        header[5] = 0;
        header[6] = (byte)options.Algorithm; // h_algorithmtype = HEAD1 算法
        header[7] = 0; // h_clusterbits = 0
        WriteAt(output, indexOffset, header, header.Length);

        if (node.CompactedIndexes)
        {
            // compacted 4B amortized 位打包索引：map header 后紧跟位打包表，无 8B 保留区。
            // The compacted 4B-amortized bit-packed index follows the map header directly (no
            // 8-byte pad).
            WriteCompactedIndexes4B(output, indexOffset + ZErofsMapHeader.Size, node, blockSize);
            return;
        }

        // legacy full：map header 后 8B 保留（内核 Z_EROFS_FULL_INDEX_START 约定），之后每逻辑簇
        // 一条 8B 索引：pcluster 首簇为 HEAD（clusterofs = 0，blkaddr = 物理块号），该 pcluster
        // 覆盖的其余逻辑簇为 NONHEAD（delta[0] = 距 HEAD 的簇数）。
        // Legacy full: an 8-byte pad after the map header (kernel Z_EROFS_FULL_INDEX_START), then
        // one 8-byte index per logical cluster: the first cluster of a pcluster is a HEAD
        // (clusterofs = 0, blkaddr = physical block); the covered clusters are NONHEAD entries
        // (delta[0] = distance from the HEAD cluster).
        var entryOffset = indexOffset + ZErofsMapHeader.Size + 8;
        var clusters = node.ClusterCount;
        var pclusters = node.Pclusters!;
        var pclusterIndex = 0;
        for (var c = 0L; c < clusters; c++)
        {
            var plan = pclusters[pclusterIndex];
            var headCluster = plan.SourceOffset / blockSize;
            var coveredClusters = CeilDiv(plan.SourceLength, blockSize);
            var entry = new byte[ZErofsLclusterIndex.Size];
            if (c == headCluster)
            {
                // 该 pcluster 的首簇：HEAD（blkaddr 指向物理块，clusterofs = 0）。
                BinaryPrimitives.WriteUInt16LittleEndian(entry, ZErofsLclusterIndex.LclusterTypeHead1);
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(2), 0); // clusterofs
                BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), plan.BlockAddress);
            }
            else
            {
                // 同 pcluster 的其余簇：NONHEAD，di_u.delta[0]（offset 4）= 距 HEAD 的簇数。
                // Remaining clusters of the pcluster are NONHEAD entries whose di_u.delta[0]
                // (offset 4) holds the distance to the HEAD cluster.
                BinaryPrimitives.WriteUInt16LittleEndian(entry, ZErofsLclusterIndex.LclusterTypeNonHead);
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(2), 0); // clusterofs 未用
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(4), (ushort)(c - headCluster));
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(6), 0); // delta[1] 未用
            }

            WriteAt(output, entryOffset, entry, entry.Length);
            entryOffset += entry.Length;

            if (c == headCluster + coveredClusters - 1 && pclusterIndex + 1 < pclusters.Length)
            {
                pclusterIndex++;
            }
        }
    }

    /// <summary>
    /// 写出 compacted 4B amortized 位打包索引表（datalayout = COMPRESSED_COMPACT，仅 4B 区、
    /// 不启用 2B 区）。严格对照 erofs-utils <c>write_compacted_indexes</c> 的位打包：
    /// 每条目 v = (clustertype &lt;&lt; lobits) | offset 按 encodebits 位连续写入，每 2 簇一个
    /// 8B pack，pack 尾部 4B 存该 pack 起点块号（blkaddr 随 HEAD 递增，非 big-pcluster 从
    /// 首块号 - 1 起并 dummy_head 置位）。表起点由调用方传入（map header 之后）。
    /// <para>Writes a compacted 4B-amortized bit-packed index table (datalayout =
    /// COMPRESSED_COMPACT; 4B-only section, no 2B section), mirroring erofs-utils
    /// <c>write_compacted_indexes</c> bit packing: each entry v = (clustertype &lt;&lt; lobits) |
    /// offset is written at encodebits bits; two clusters form one 8-byte pack whose trailing 4
    /// bytes hold the pack-start block address (blkaddr increments per HEAD; without
    /// big-pcluster it starts at first-block-minus-one with dummy_head set). The table starts at
    /// the caller-provided offset (right after the map header).</para>
    /// </summary>
    private static void WriteCompactedIndexes4B(Stream output, long tableOffset, BuildNode node, int blockSize)
    {
        const int destsize = 4;
        const int vcnt = 2;
        var lclusterBits = BitOperations.Log2((uint)blockSize);
        var lobits = Math.Max(lclusterBits, 12); // ilog2(Z_EROFS_LI_D0_CBLKCNT) + 1 = 11 + 1
        var encodebits = (vcnt * destsize * 8 - 32) / vcnt; // (2*4*8-32)/2 = 16
        var packSize = vcnt * destsize; // 8 字节每 pack。8 bytes per pack.

        var clusters = node.ClusterCount;
        var pclusters = node.Pclusters!;
        var pack = new byte[packSize];
        var packOffset = tableOffset;
        var entryIndex = 0L;          // 逻辑簇序号。Logical cluster index.
        var pclusterIndex = 0;
        long blkaddr = (long)pclusters[0].BlockAddress - 1; // 非 big-pcluster 先减一。Non-big: minus one first.

        while (entryIndex < clusters)
        {
            // 每个 pack 处理 vcnt 条（不足时余下条目以全零占位，读端不会访问超出 totalIdx 的簇）。
            // Each pack handles vcnt entries (short packs are zero-padded; readers never access
            // clusters beyond totalIdx).
            // pack 尾部 4B 存“进入本 pack 时的当前块号”快照（即 pack 内首个 HEAD 的物理块号
            // 减一；读端经 pblk = packBlkaddr + 回看累计块数还原，与 erofs-utils 写端一致：
            // 非 big-pcluster 场景 mkfs 从首块号 - 1 起步、每遇 HEAD 递增，pack 尾写入进入值）。
            // The pack's trailing 4 bytes hold the block-address snapshot taken when entering this
            // pack (the first HEAD's physical block minus one; readers reconstruct
            // pblk = packBlkaddr + lookback count, matching the erofs-utils writer: without
            // big-pcluster mkfs starts at first-block-minus-one, increments per HEAD, and stores
            // the entry value in the pack tail).
            var packStartBlkaddr = blkaddr;
            var bits = 0;
            for (var i = 0; i < vcnt; i++)
            {
                uint offset;
                byte type;
                if (entryIndex < clusters)
                {
                    var plan = pclusters[pclusterIndex];
                    var headCluster = plan.SourceOffset / blockSize;
                    var coveredClusters = CeilDiv(plan.SourceLength, blockSize);
                    if (entryIndex == headCluster)
                    {
                        // HEAD：clusterofs = 0，物理块号 = 递增后的 blkaddr（校验与规划一致）。
                        // HEAD: clusterofs = 0; the physical block is the incremented blkaddr
                        // (verified against the plan).
                        type = 1; // Z_EROFS_LCLUSTER_TYPE_HEAD1
                        offset = 0;
                        blkaddr++;
                        if (blkaddr != plan.BlockAddress)
                        {
                            throw new InvalidDataException(
                                $"Compacted index blkaddr {blkaddr} != planned block {plan.BlockAddress} for '{node.FullPath}'.");
                        }
                    }
                    else
                    {
                        // NONHEAD：offset = 距 HEAD 的簇数（pack 末条存 delta[1] 的规则只影响
                        // 2B 区与部分解码，纯 4B 区按 delta[0] 存即可；读端按前条回推 delta[0]）。
                        // NONHEAD: offset = cluster distance to the HEAD. The "last slot stores
                        // delta[1]" rule concerns the 2B section; in the 4B section we store
                        // delta[0] and the reader derives delta[0] from the previous entry anyway.
                        type = 2; // Z_EROFS_LCLUSTER_TYPE_NONHEAD
                        offset = (uint)(entryIndex - headCluster);
                    }

                    if (entryIndex == headCluster + coveredClusters - 1 && pclusterIndex + 1 < pclusters.Length)
                    {
                        pclusterIndex++;
                    }

                    entryIndex++;
                }
                else
                {
                    type = 0; // 占位 PLAIN。Zero placeholder.
                    offset = 0;
                }

                // 位打包写入：v 占 encodebits 位，从 bits 偏移起写入 pack。
                // Bit-packed write: v occupies encodebits bits starting at bit offset "bits".
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

            // pack 尾部 4B 存进入时的块号快照（读端 pblk = packBlkaddr + 回看累计块数）。
            // The pack's trailing 4 bytes store the entry-time block snapshot (reader:
            // pblk = packBlkaddr + lookback count).
            BinaryPrimitives.WriteUInt32LittleEndian(pack.AsSpan(packSize - 4, 4), (uint)packStartBlkaddr);
            WriteAt(output, packOffset, pack, pack.Length);
            packOffset += pack.Length;
            Array.Clear(pack);
        }
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
    /// 写出 packed inode 的碎片数据区：把每个碎片槽位按文件内偏移写入数据区对应位置
    /// （槽位之间未写的空白由镜像预分配/置零保证为零）。
    /// <para>Writes the packed inode's fragment data area: each fragment slot's source bytes are
    /// streamed to its offset inside the packed region (unwritten gaps stay zeroed by the image's
    /// preallocation).</para>
    /// </summary>
    private static void WritePackedData(Stream output, BuildNode packedNode, int blockSize)
    {
        var baseOffset = packedNode.FlatBlockAddr * blockSize;
        foreach (var slot in packedNode.PackedFragments!)
        {
            CopyFileRange(output, slot.Owner, 0, slot.Length, baseOffset + slot.Offset);
        }
    }

    /// <summary>
    /// 判断节点是否可作为 METABOX 容器驻留记录：普通文件（非目录/符号链接/设备/合成节点）、
    /// 存储为纯全块 flat（无内联尾部）、无压缩/碎片/xattr，且尺寸 ≤ 32 位（compact inode）。
    /// 解析侧只能自 metabox 槽位恢复这类“纯 32 字节紧凑记录”，其余节点（目录、符号链接、
    /// 设备、压缩/碎片/xattr 文件）留在经典 inode 表。
    /// <para>Whether a node qualifies as a METABOX container record: a regular file (not a directory /
    /// symlink / device / synthetic node) stored as pure whole-block flat data (no inline tail),
    /// without compression / fragments / xattrs, whose size fits 32 bits (compact inode). The read
    /// side can only recover such pure 32-byte compact records from a metabox slot; every other node
    /// (directories, symlinks, devices, compressed/fragment/xattr files) stays in the classic table.</para>
    /// </summary>
    private static bool IsMetaboxEligible(BuildNode node)
        => !node.IsDirectory
           && !node.IsSymlink
           && !node.IsDevice
           && !node.IsPacked
           && node.Size <= uint.MaxValue
           && node.Pclusters is null
           && !node.IsFragment
           && node.XattrIbody.Length == 0;

    /// <summary>
    /// 写出 metabox inode 的容器数据区：把每个容器驻留文件的 32 字节 compact inode 记录写到其
    /// 槽位（metabox inode 的 i_u 指向该数据区，i_size = 槽数 × 32）。
    /// <para>Writes the metabox inode's container data area: each resident file's 32-byte compact
    /// inode record is streamed to its slot (the metabox inode's i_u points at this data area and
    /// its i_size is slotCount × 32).</para>
    /// </summary>
    private static void WriteMetaboxContainer(Stream output, BuildNode metaboxNode, int blockSize, ulong now, List<BuildNode> residents)
    {
        var baseOffset = checked(metaboxNode.FlatBlockAddr * blockSize);
        foreach (var resident in residents)
        {
            var inode = BuildInode(resident, blockSize, now);
            var buffer = new byte[inode.OnDiskSize];
            inode.WriteTo(buffer);
            WriteAt(output, baseOffset + resident.MetaboxSlot * ErofsSuperblock.CompactInodeSize, buffer, buffer.Length);
        }
    }

    /// <summary>
    /// 写出 fragment inode 的 8 字节 z_erofs_map_header：h_fragmentoff = packed 区内字节偏移、
    /// h_clusterbits bit7（<see cref="ZErofsMapHeader.FragmentInodeMask"/>）声明整文件 fragment。
    /// header 起始位置与读端一致：inode + inline xattr 之后 8 字节对齐（<see
    /// cref="GetInodeRecordSize"/> 已把该填充计入记录长度）。
    /// <para>Writes a fragment inode's 8-byte z_erofs_map_header: h_fragmentoff is the byte offset
    /// inside the packed inode and h_clusterbits bit 7
    /// (<see cref="ZErofsMapHeader.FragmentInodeMask"/>) marks a whole-file fragment. The header
    /// starts where the reader expects it: 8-byte aligned after inode + inline xattrs (the pad is
    /// accounted for in <see cref="GetInodeRecordSize"/>).</para>
    /// </summary>
    private static void WriteFragmentHeader(Stream output, BuildNode node, ErofsInode inode, ErofsOptions options)
    {
        var headerOffset = node.MetaOffset + AlignUp(inode.OnDiskSize + inode.XattrTotalSize, 8);
        var header = new byte[ZErofsMapHeader.Size];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)node.FragmentOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 0); // h_advise：无额外标记
        header[6] = (byte)options.Algorithm;                            // h_algorithmtype（读端 fragment 路径不使用）
        header[7] = ZErofsMapHeader.FragmentInodeMask;                  // h_clusterbits bit7
        WriteAt(output, headerOffset, header, header.Length);
    }

    /// <summary>
    /// 流式计算整文件 SHA-256（IncrementalHash，分块读取，不整读大文件）。
    /// <para>Streams a whole file through SHA-256 via <see cref="IncrementalHash"/> in chunks
    /// without loading the file wholly into memory.</para>
    /// </summary>
    private static byte[] HashFile(BuildNode node)
    {
        using var fs = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        var remaining = node.Size;
        while (remaining > 0)
        {
            var count = (int)Math.Min(buffer.Length, remaining);
            fs.ReadExactly(buffer, 0, count);
            hash.AppendData(buffer, 0, count);
            remaining -= count;
        }

        return hash.GetHashAndReset();
    }

    /// <summary>
    /// 选择下一碎片起点：避免 h_fragmentoff 的 bit7 与 read-side 的 fragment 标记掩码冲突
    /// （读端清除该位后仍应还原真实偏移）。packed 区内偏移按 256 对齐补齐越界位。
    /// <para>Picks the next fragment start so bit 7 of h_fragmentoff never collides with the
    /// reader's fragment-flag mask (the reader clears that bit, so real offsets must leave it
    /// zero); starts landing in [128..255] modulo 256 are rounded up to the next 256 boundary.</para>
    /// </summary>
    private static long AlignFragmentStart(long cursor)
        => (cursor & 0x80) == 0 ? cursor : (cursor & ~0xFFL) + 0x100L;

    private static void CopyFileRange(Stream output, BuildNode node, long sourceOffset, long length, long targetOffset)
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
            var count = (int)Math.Min(buffer.Length, remaining);
            source.ReadExactly(buffer, 0, count);
            WriteAt(output, pos, buffer, count);
            pos += count;
            remaining -= count;
        }
    }

    private static long CeilDiv(long value, long divisor) => value == 0 ? 0 : (value + divisor - 1) / divisor;

    private static long AlignUp(long value, long alignment) => (value + alignment - 1) / alignment * alignment;

    private static void ZeroImage(Stream output, long length)
    {
        output.Position = 0;
        var zeroes = new byte[1 << 16];
        while (length > 0)
        {
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

    /// <summary>
    /// 在底层可寻址流上叠加基偏移的写包装（使镜像可写到前缀数据之后）。
    /// <para>A write wrapper adding a base offset over an underlying seekable stream (lets an image
    /// be written after a prefix).</para>
    /// </summary>
    private sealed class OffsetStream(Stream inner, long baseOffset) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length - baseOffset;

        public override long Position
        {
            get => inner.Position - baseOffset;
            set => inner.Position = checked(value + baseOffset);
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin)
            => inner.Seek(offset + (origin == SeekOrigin.Begin ? baseOffset : 0), origin) - baseOffset;

        public override void SetLength(long value) => inner.SetLength(checked(value + baseOffset));

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
