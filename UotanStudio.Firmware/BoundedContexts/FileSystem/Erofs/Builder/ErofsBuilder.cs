using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace UotanStudio.Firmware.BoundedContexts.FileSystem.Erofs;

public sealed partial class ErofsBuilder
{
    private const long InodeTableStart = ErofsSuperblock.SuperblockOffset + ErofsSuperblock.SuperblockSize;

    private const ulong NidMetaboxFlag = 1UL << 63;


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
    /// Packs a directory into erofs image bytes.
    /// <para>将目录打包为 erofs 镜像字节。</para>
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
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    /// <summary>
    /// Atomically packs a directory to a path by building and flushing a temporary file in the destination directory before replacing the destination.
    /// <para>将目录原子打包到指定路径。</para>
    /// </summary>
    public void Build(string directoryPath, string outputPath, ErofsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        var fullOutputPath = Path.GetFullPath(outputPath);
        var outputDirectory = Path.GetDirectoryName(fullOutputPath)!;
        var temporaryPath = Path.Combine(outputDirectory, $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");

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
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    /// <summary>
    /// Packs a directory starting at the output stream's current position.
    /// <para>将目录打包为 erofs 镜像并从输出流当前 Position 写入。</para>
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
            try { File.Delete(temporaryPath); } catch { }
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
    /// Reads Unix metadata (uid/gid/mtime/permissions/dev): Linux uses lstat for all fields; other platforms fall back to defaults (permissions = 0, uid/gid = 0, mtime = 0).
    /// <para>读取 Unix 元数据（uid/gid/mtime/permissions/dev）：Linux 通过 lstat 获取全部字段； 其他平台使用回退默认值（permissions = 0，uid/gid = 0，mtime = 0）。</para>
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
        mtime = 0;
        permissions = 0;
        isDevice = false;
        deviceRdev = 0;
        deviceMode = 0;


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
            if (fileType == 0x2000 || fileType == 0x6000)
            {
                isDevice = true;
                deviceRdev = (uint)buf.st_rdev;
                deviceMode = (ushort)buf.st_mode;
            }
        }
        catch
        {

        }
    }

    private static BuildNode CollectNode(string fullPath, bool isRoot, HashSet<string>? excludedPaths)
    {
        var attributes = File.GetAttributes(fullPath);
        var name = isRoot ? string.Empty : Path.GetFileName(fullPath);


        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            string target = string.Empty;
            try { target = new FileInfo(fullPath).LinkTarget ?? string.Empty; } catch { }
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


        ReadUnixMetadata(
            fullPath,
            out var uid,
            out var gid,
            out var mtime,
            out var _permissions,
            out var isDevice,
            out var deviceRdev,
            out var deviceMode);


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
                if (SharedKernel.RepackMetadata.IsMetadataFile(childPath)) continue;
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
    /// Encodes the per-path xattrs from ErofsOptions.FileXattrs into inline ibodies and attaches them to the matching nodes.
    /// <para>把 ErofsOptions.FileXattrs 中按相对路径列出的 xattr 编码为 inline ibody 挂到对应节点。</para>
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

    /// <summary>
    /// Image-relative path of a node ('/'-separated; root is "").
    /// <para>节点的镜像内相对路径（'/' 分隔；根为 ""）。</para>
    /// </summary>
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
        public byte[] InlineData = [];
        public long DirBlocks;
        public long DirBlockAddr = -1;
        public long FlatBlockAddr = -1;
        public long FlatBlocks;
        public PclusterPlan[]? Pclusters;
        public uint CompressedBlocks;
        public uint ClusterCount;
        public bool CompactedIndexes;
        public bool BigPcluster;

        public byte[] XattrIbody = [];


        public bool IsSymlink;
        public string SymlinkTarget = string.Empty;
        public bool IsDevice;
        public uint DeviceRdev;
        public ushort DeviceMode;
        public uint Uid;
        public uint Gid;
        public long Mtime;


        public bool IsFragment;
        public long FragmentOffset = -1;
        public byte[]? ContentHash;
        public BuildNode? DedupSource;
        public bool IsPacked;
        public List<PackedFragment>? PackedFragments;


        public bool IsMetaboxResident;

        public bool IsMetaboxContainer;
        public long MetaboxSlot = -1;

    }

    private sealed class PackedFragment
    {
        public required BuildNode Owner;
        public long Offset;
        public long Length;
    }

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


        output.CaptureWindow(ErofsSuperblock.SuperblockOffset, blockSize - ErofsSuperblock.SuperblockOffset);


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


        var ordered = new List<BuildNode>();
        CollectPreOrder(root, ordered);
        BindXattrs(ordered, options);


        var packedFragments = new List<PackedFragment>();
        long packedEnd = 0;


        var dedupFlat = new Dictionary<string, BuildNode>();
        var dedupFragments = new Dictionary<string, BuildNode>();

        foreach (var node in ordered)
        {
            if (node.IsDirectory)
            {


                var payloadLength = EstimateDirPayloadLength(node);
                node.DirBlocks = payloadLength <= blockSize
                    ? 1
                    : CountDirBlocks(BuildDirEntries(node), blockSize);
                continue;
            }


            var isDataFile = !node.IsSymlink && !node.IsDevice;
            if (node.Size <= 0)
            {
                continue;
            }

            if (isDataFile && options.Dedup)
            {
                node.ContentHash = HashFile(node);
            }


            if (options.Compress)
            {
                node.Pclusters = TryPlanCompressed(node, blockSize, options);
                if (node.Pclusters is not null)
                {
                    node.CompressedBlocks = (uint)node.Pclusters.Sum(p => CeilDiv(p.CompressedLength, blockSize));
                    node.ClusterCount = (uint)CeilDiv(node.Size, blockSize);
                    node.CompactedIndexes = options.UseCompactedIndexes;


                    node.BigPcluster = options.Algorithm != ErofsCompressionAlgorithm.Lz4;
                    continue;
                }
            }

            var wholeBlocks = node.Size / blockSize;

            if (isDataFile && options.Fragments && wholeBlocks == 0)
            {


                node.IsFragment = true;
                var hashKey = options.Dedup && node.ContentHash is not null
                    ? Convert.ToHexString(node.ContentHash)
                    : null;
                if (hashKey is not null && dedupFragments.TryGetValue(hashKey, out var existing))
                {


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


            var remainder = node.Size % blockSize;
            if (options.TailPacking)
            {


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


                node.FlatBlocks = remainder > 0 ? wholeBlocks + 1 : wholeBlocks;
            }


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


        var metaOff = inodeTableStart;
        if (metaboxResident is not null)
        {
            for (var i = 0; i < metaboxResident.Count; i++)
            {
                var resident = metaboxResident[i];
                resident.MetaboxSlot = i;
                resident.MetaOffset = -1;
                resident.Nid = NidMetaboxFlag | (ulong)(uint)i;
            }
        }

        foreach (var node in ordered)
        {
            if (node.IsMetaboxResident)
            {
                continue;
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


        var finalSize = checked(totalBlocks * blockSize);
        var oldLength = output.Length;
        output.SetLength(finalSize);
        if (!freshDestination)
        {

            ZeroImage(output, Math.Min(oldLength, finalSize));
        }

        var now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var featureIncompat = options.Compress
            ? (options.Algorithm == ErofsCompressionAlgorithm.Lz4
                ? ErofsSuperblock.IncompatLz4_0Padding
                : ErofsSuperblock.IncompatBigPcluster)
            : 0;


        if (extraDevices > 0)
        {
            featureIncompat |= ErofsSuperblock.IncompatDeviceTable;
        }


        if (packedNode is not null)
        {
            featureIncompat |= ErofsSuperblock.IncompatFragments;
        }


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


            DirBlkBits = 0,
        };


        var superblockBuffer = new byte[metaboxNode is not null
            ? ErofsSuperblock.SuperblockSize + ErofsSuperblock.ExtSlotSize
            : ErofsSuperblock.SuperblockSize];
        superblock.WriteTo(superblockBuffer);


        WriteAt(output, ErofsSuperblock.SuperblockOffset, superblockBuffer, superblockBuffer.Length);
        if (compressionConfiguration.Length > 0)
        {
            WriteAt(output, ErofsSuperblock.SuperblockOffset + superblockBuffer.Length,
                compressionConfiguration, compressionConfiguration.Length);
        }


        if (extraDevices > 0)
        {
            var slotBuffer = new byte[ErofsDeviceSlot.Size];
            var slotCount = extraDevices;
            for (var i = 0; i < slotCount; i++)
            {
                var slot = new ErofsDeviceSlot { Blocks = 0, MappedBlkAddr = 0 };
                slot.WriteTo(slotBuffer);
                WriteAt(output, devtOffset + i * ErofsDeviceSlot.Size, slotBuffer, slotBuffer.Length);
            }
        }


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


                WritePackedData(output, node, blockSize);
            }
            else if (node.Pclusters is not null)
            {
                WriteCompressedData(output, node, blockSize, options);
            }
            else if (node.IsMetaboxContainer)
            {


                WriteMetaboxContainer(output, node, blockSize, now, metaboxResident!);
            }
            else if (node.FlatBlockAddr >= 0 && node.FlatBlocks > 0 && node.DedupSource is null)
            {


                var copyLen = Math.Min(node.FlatBlocks * blockSize, node.Size);
                CopyFileRange(output, node, 0, copyLen, node.FlatBlockAddr * blockSize);
            }
        }


        foreach (var node in ordered)
        {
            if (node.IsMetaboxResident)
            {
                continue;
            }

            var inode = BuildInode(node, blockSize, now);
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

                if (node.DirBlocks <= 1)
                {
                    WriteAt(output, cursor, BuildDirPayload(node), (int)EstimateDirPayloadLength(node));
                }
            }
            else if (node.IsFragment)
            {


                WriteFragmentHeader(output, node, inode, options);
            }
            else if (node.Pclusters is not null)
            {
                WriteCompressedIndexes(output, cursor, node, blockSize, options);
            }
            else if (node.InlineData.Length > 0)
            {


                WriteAt(output, cursor, node.InlineData, node.InlineData.Length);
            }
        }


        {
            var crcRegion = output.CapturedWindow ?? throw new InvalidOperationException("Superblock checksum window was not captured.");
            crcRegion[4] = 0;
            crcRegion[5] = 0;
            crcRegion[6] = 0;
            crcRegion[7] = 0;
            var finalCrc = ComputeCrc32C(crcRegion);
            Span<byte> crcLe = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(crcLe, finalCrc);
            output.Position = ErofsSuperblock.SuperblockOffset + 4;
            output.Write(crcLe);
        }

        output.Position = finalSize;
    }

    private sealed class OffsetStream(Stream inner, long baseOffset) : Stream
    {
        private byte[]? _capture;
        private long _captureStart;

        /// <summary>
        /// Starts capturing writes to [start, start+length) in image coordinates.
        /// <para>开始捕获镜像内 [start, start+length) 的写入字节。</para>
        /// </summary>
        public void CaptureWindow(long start, int length)
        {
            _captureStart = start;
            _capture = new byte[length];
        }

        public byte[]? CapturedWindow => _capture;

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

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_capture is not null)
            {
                var imagePos = inner.Position - baseOffset;
                var from = Math.Max(imagePos, _captureStart);
                var to = Math.Min(imagePos + count, _captureStart + _capture.Length);
                if (to > from)
                {
                    Buffer.BlockCopy(buffer, offset + (int)(from - imagePos), _capture, (int)(from - _captureStart), (int)(to - from));
                }
            }

            inner.Write(buffer, offset, count);
        }
    }
}
