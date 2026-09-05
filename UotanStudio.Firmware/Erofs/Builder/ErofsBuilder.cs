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
public sealed partial class ErofsBuilder
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
                    node.CompressedBlocks = (uint)node.Pclusters.Sum(p => CeilDiv(p.CompressedLength, blockSize));
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
            DirBlkBits = (byte)BitOperations.Log2((uint)blockSize),
        };
        // METABOX 需 144 字节超级块缓冲区（128 字节主槽 + 16 字节扩展槽）容纳 s_metabox_nid。
        // METABOX needs a 144-byte superblock buffer (128-byte main slot + one 16-byte extended slot)
        // to hold s_metabox_nid.
        var superblockBuffer = new byte[metaboxNode is not null
            ? ErofsSuperblock.SuperblockSize + ErofsSuperblock.ExtSlotSize
            : ErofsSuperblock.SuperblockSize];
        superblock.WriteTo(superblockBuffer);
        // 先写 checksum=0 的占位超级块，最后再计算并回写校验和。
        // erofs-utils erofs_superblock_csum_verify 从设备读取 [SuperblockOffset, blockSize) 的
        // 全部字节计算 CRC32-C，此时该区域已包含 inode 元数据，必须在所有数据写完后才能计算。
        // Write the superblock with checksum=0 as a placeholder; the actual checksum is computed and
        // written back after all data is flushed, because erofs-utils erofs_superblock_csum_verify reads
        // [SuperblockOffset, blockSize) which includes inode metadata already written by then.
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

        // ---- 所有数据/元数据已写完，回写超级块 CRC32-C 校验和。----
        // 回读 [SuperblockOffset, blockSize) 的全部字节（已含 inode 元数据），
        // 清零 checksum 字段后计算 CRC32-C，写回 offset 4。
        // ---- All data/metadata written; compute and write back the superblock CRC32c. ----
        // Read back [SuperblockOffset, blockSize) (now containing inode metadata),
        // zero the checksum field, compute CRC32-C, and write it back at offset 4.
        {
            var crcLen = blockSize - ErofsSuperblock.SuperblockOffset;
            var crcRegion = new byte[crcLen];
            output.Position = ErofsSuperblock.SuperblockOffset;
            var totalRead = 0;
            while (totalRead < crcLen)
            {
                var n = output.Read(crcRegion, totalRead, crcLen - totalRead);
                if (n <= 0) break;
                totalRead += n;
            }

            // 清零 checksum 字段（offset 4 in superblock = offset 4 in crcRegion）
            crcRegion[4] = 0;
            crcRegion[5] = 0;
            crcRegion[6] = 0;
            crcRegion[7] = 0;
            var finalCrc = ComputeCrc32C(crcRegion);
            output.Position = ErofsSuperblock.SuperblockOffset + 4;
            Span<byte> crcLe = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(crcLe, finalCrc);
            output.Write(crcLe);
        }

        output.Position = finalSize;
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