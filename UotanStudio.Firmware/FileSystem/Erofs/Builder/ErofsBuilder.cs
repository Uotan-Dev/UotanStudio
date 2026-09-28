using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Checksums;
using UotanStudio.Firmware.FileSystem.Erofs;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Metadata;

namespace UotanStudio.Firmware.FileSystem.Erofs.Builder;

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
    /// <param name="directoryPath">Existing directory whose tree becomes the image root.
    /// <para>作为镜像根目录的现存目录。</para></param>
    /// <param name="options">Build options; <see langword="null"/> uses validated defaults.
    /// <para>构建选项；为 <see langword="null"/> 时使用经校验的默认值。</para></param>
    /// <returns>The complete image as one in-memory byte array.
    /// <para>完整镜像的内存字节数组。</para></returns>
    /// <exception cref="DirectoryNotFoundException"><paramref name="directoryPath"/> does not exist.
    /// <para><paramref name="directoryPath"/> 不存在。</para></exception>
    /// <exception cref="InvalidOperationException">The built image exceeds the maximum byte-array length.
    /// <para>构建出的镜像超过字节数组最大长度。</para></exception>
    [Obsolete("Loads the entire image into memory. Use Build(directoryPath, outputPath) instead.")]
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
    /// <param name="directoryPath">Existing directory whose tree becomes the image root; the temporary and
    /// final output files are excluded from the collected tree.
    /// <para>作为镜像根目录的现存目录；收集目录树时会排除临时文件与最终输出文件。</para></param>
    /// <param name="outputPath">Destination image path, replaced only after the temporary file is fully
    /// written and flushed to disk; an existing file is overwritten.
    /// <para>目标镜像路径；仅在临时文件完整写入并落盘后才替换目标，已存在的文件会被覆盖。</para></param>
    /// <param name="options">Build options; <see langword="null"/> uses validated defaults.
    /// <para>构建选项；为 <see langword="null"/> 时使用经校验的默认值。</para></param>
    /// <param name="progress">Optional reporter receiving processed and total source bytes; the total is the
    /// summed file size at start and is at least 1. <para>可选的进度回调，报告已处理与源文件总字节数；总数为开始时
    /// 的文件大小之和，且至少为 1。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="outputPath"/> is <see langword="null"/>.
    /// <para><paramref name="outputPath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="DirectoryNotFoundException"><paramref name="directoryPath"/> does not exist.
    /// <para><paramref name="directoryPath"/> 不存在。</para></exception>
    public void Build(string directoryPath, string outputPath, ErofsOptions? options = null, IProgress<(long Processed, long Total)>? progress = null)
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
                var counter = progress is null ? null
                    : new UotanStudio.Firmware.Core.ExtractionByteCounter(EstimateSourceBytes(directoryPath), progress);
                Stream sink = temporary;
                if (counter is not null) sink = counter.Wrap(temporary);
                BuildImage(root, effective, sink, 0, freshDestination: true);
                temporary.Flush(flushToDisk: true);
                counter?.Complete();
                sink.Dispose();
            }

            File.Move(temporaryPath, fullOutputPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private static long EstimateSourceBytes(string directoryPath)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (IOException) { }
        }
        return total == 0 ? 1 : total;
    }

    /// <summary>
    /// Packs a directory starting at the output stream's current position.
    /// <para>将目录打包为 erofs 镜像并从输出流当前 Position 写入。</para>
    /// </summary>
    /// <param name="directoryPath">Existing directory whose tree becomes the image root; when the output is
    /// a <see cref="FileStream"/>, that file is excluded from the collected tree.
    /// <para>作为镜像根目录的现存目录；当输出为 <see cref="FileStream"/> 时，收集目录树会排除该文件。</para></param>
    /// <param name="output">Writable stream; a seekable stream supporting length resets is written in place
    /// from its current position, otherwise the image is staged in a temporary file and copied. The caller
    /// keeps ownership and must flush or close it.
    /// <para>可写流；支持改长度的可寻址流从当前位置就地写入，否则先经临时文件中转复制。流的所有权仍归调用方，
    /// 需自行刷新或关闭。</para></param>
    /// <param name="options">Build options; <see langword="null"/> uses validated defaults.
    /// <para>构建选项；为 <see langword="null"/> 时使用经校验的默认值。</para></param>
    /// <exception cref="ArgumentNullException"><paramref name="directoryPath"/> is <see langword="null"/>.
    /// <para><paramref name="directoryPath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="output"/> cannot be written.
    /// <para><paramref name="output"/> 不可写。</para></exception>
    /// <exception cref="DirectoryNotFoundException"><paramref name="directoryPath"/> does not exist.
    /// <para><paramref name="directoryPath"/> 不存在。</para></exception>
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
        UotanStudio.Firmware.FileSystem.Rebuilding.FileSystemPreservation.RejectLossyDirectoryRebuild(directoryPath);
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
        ApplyMetadataOverrides(root, effective);
        return effective;
    }

    /// <summary>
    /// Applies fs_config (uid/gid/mode) and file_contexts (security.selinux inline xattr) overrides
    /// from the Android-style metadata files onto the collected tree.
    /// <para>将 Android 风格元数据文件中的 fs_config（uid/gid/mode）与 file_contexts
    /// （security.selinux inline xattr）覆盖应用到收集完成的目录树。</para>
    /// </summary>
    private static void ApplyMetadataOverrides(BuildNode root, ErofsOptions options)
    {
        if (options.FsConfig is null && options.FileContexts is null
            && options.FsConfigPath is null && options.FileContextsPath is null) return;
        var fsConfig = options.FsConfig ??= FileSystemMetadataOverrides.LoadFsConfig(options.FsConfigPath);
        var contexts = options.FileContexts ??= FileSystemMetadataOverrides.LoadFileContexts(options.FileContextsPath);
        var ordered = new List<BuildNode>();
        CollectPreOrder(root, ordered);
        foreach (var node in ordered)
        {
            var relative = BuildImagePath(node);
            if (fsConfig.Count > 0 && FileSystemMetadataOverrides.TryGetFsConfig(fsConfig, relative, out var entry))
            {
                node.Uid = entry.Uid;
                node.Gid = entry.Gid;
                node.ModeOverride = entry.Mode;
                node.HasModeOverride = true;
            }

            if (contexts.Count > 0 && node.XattrIbody.Length == 0 &&
                FileSystemMetadataOverrides.TryGetLabel(contexts, relative, out var label))
            {
                node.XattrIbody = ErofsXattr.EncodeInline(
                [
                    new ErofsXattrEntry
                    {
                        Name = "security.selinux",
                        Value = FileSystemMetadataOverrides.ToSelinuxXattrValue(label),
                        NameIndex = ErofsXattr.NameIndexSecurity,
                    }
                ]);
            }
        }
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
                if (UotanStudio.Firmware.FileSystem.Rebuilding.FileSystemEditPlan.Current is { } plan ? !plan.Includes(childPath) : UotanStudio.Firmware.Core.Repacking.RepackMetadata.IsMetadataFile(childPath)) continue;
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
        public ulong OriginalInode;
        public ulong LinkCount = 1;
        public ulong Nid;
        public long MetaOffset;
        public byte[] InlineData = [];
        public long DirBlocks;
        public long DirPayloadSize;
        public bool DirectoryInline;
        public long DirBlockAddr = -1;
        public long FlatBlockAddr = -1;
        public long FlatBlocks;
        public PclusterPlan[]? Pclusters;
        public uint CompressedBlocks;
        public uint ClusterCount;
        public int LogicalClusterSize;
        public bool CompactedIndexes;
        public bool BigPcluster;
        public bool InlineCompressedTail;
        public ErofsOptions? CompressionOptions;
        public ErofsSourceInode? SourceInode;

        public byte[] XattrIbody = [];

        public bool IsSymlink;
        public string SymlinkTarget = string.Empty;
        public bool IsDevice;
        public uint DeviceRdev;
        public ushort DeviceMode;
        public uint Uid;
        public uint Gid;
        public ushort ModeOverride;
        public bool HasModeOverride;
        public long Mtime;
        public bool RequiresExtendedTime;

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
        public bool Plain;
    }

    /// <summary>Computes s_available_compr_algs: the LZ4 max distance in legacy (pre-5.13)
    /// images, otherwise the algorithm bitmap.
    /// <para>计算 s_available_compr_algs：legacy（pre-5.13）镜像承载 LZ4 最大匹配距离，
    /// 否则为算法位图，均与 erofs-utils 上游一致。</para></summary>
    private static ushort CompressedAlgorithmsField(ErofsOptions options)
    {
        if (!options.Compress) return 0;
        if (options.LegacyCompatibility)
        {
            return options.Lz4MaxDistance == 0 ? ErofsCompression.Lz4DistanceMax : options.Lz4MaxDistance;
        }
        if (options.SourceCompressionOptions is { } codecs)
        {
            return (ushort)codecs.Keys.Aggregate(0, (mask, algorithm) => mask | (1 << (int)algorithm));
        }
        return (ushort)(1 << (int)options.Algorithm);
    }

    private static void BuildImage(BuildNode root, ErofsOptions options, Stream destination, long baseOffset, bool freshDestination)
    {
        using var output = new OffsetStream(destination, baseOffset);
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

        var ordered = new List<BuildNode>();
        CollectPreOrder(root, ordered);
        BindXattrs(ordered, options);
        if (UotanStudio.Firmware.FileSystem.Rebuilding.FileSystemEditPlan.Current is { } editPlan)
            foreach (var node in ordered)
            {
                var entry = editPlan.Get(node.FullPath);
                node.OriginalInode = entry.Inode;
                node.SourceInode = options.SourceInodes?.GetValueOrDefault(entry.Inode);
                if (entry.Metadata?.ModificationTime is { } time)
                {
                    node.SourceInode = node.SourceInode is { } sourceInode
                        ? sourceInode with { Mtime = checked((ulong)time.Seconds), MtimeNanoseconds = time.Nanoseconds }
                        : new ErofsSourceInode(checked((ulong)time.Seconds), time.Nanoseconds, true,
                            options.Compress, options.Algorithm, options.UseCompactedIndexes, false, blockSize, entry.Path);
                }
                if ((entry.Mode & 0xF000) == 0x8000) { node.FullPath = editPlan.ContentSource(node.FullPath); node.Size = new FileInfo(node.FullPath).Length; }
                node.Name = UotanStudio.Firmware.FileSystem.Rebuilding.FileSystemEditPlan.Name(entry.Path);
                node.Uid = entry.Uid;
                node.Gid = entry.Gid;
                node.ModeOverride = entry.Mode;
                node.HasModeOverride = true;
                node.IsSymlink = (entry.Mode & 0xF000) == 0xA000;
                if (node.IsSymlink) { node.SymlinkTarget = entry.SymlinkTarget ?? ""; node.Size = Encoding.UTF8.GetByteCount(node.SymlinkTarget); }
                node.IsDevice = (entry.Mode & 0xF000) is 0x1000 or 0x2000 or 0x6000 or 0xC000;
                node.DeviceMode = entry.Mode;
                node.DeviceRdev = entry.DeviceId;
                if (node.IsDevice) node.Size = 0;
                node.XattrIbody = entry.Xattrs is { Count: > 0 } ? ErofsXattr.EncodeInline(entry.Xattrs.Select(p => new ErofsXattrEntry { Name = p.Key, Value = p.Value }).ToList()) : [];
            }
        else if (options.SourceInodesByPath is { Count: > 0 } byPath)
            foreach (var node in ordered)
            {
                if (node.SourceInode is not null) continue;
                var imagePath = BuildImagePath(node);
                if (byPath.TryGetValue(imagePath, out var record)) node.SourceInode = record;
            }

        foreach (var node in ordered)
        {
            var timestamp = node.SourceInode?.Mtime ?? (node.Mtime > 0 ? (ulong)node.Mtime : now);
            node.RequiresExtendedTime = timestamp < now || timestamp - now > uint.MaxValue;
        }

        var hardlinks = new List<(BuildNode Alias, BuildNode Canonical)>();
        var canonicalInodes = new Dictionary<ulong, BuildNode>();
        ordered.RemoveAll(node =>
        {
            if (node.IsDirectory || node.OriginalInode == 0) return false;
            if (canonicalInodes.TryAdd(node.OriginalInode, node)) return false;
            var canonical = canonicalInodes[node.OriginalInode];
            canonical.LinkCount++;
            hardlinks.Add((node, canonical));
            return true;
        });
        var packedFragments = new List<PackedFragment>();
        long packedEnd = 0;

        var dedupFlat = new Dictionary<string, BuildNode>();
        var dedupFragments = new Dictionary<string, BuildNode>();

        Core.FirmwareParallelism.ForEach(ordered, node =>
        {
            if (node.IsDirectory || node.IsSymlink || node.IsDevice || node.Size <= 0) return;
            if (!node.IsSymlink && !node.IsDevice && options.Dedup)
                node.ContentHash = HashFile(node);
            node.CompressionOptions = node.SourceInode is { Compressed: true } original
                ? options.SourceCompressionOptions?.GetValueOrDefault(original.Algorithm) ?? options : options;
            node.LogicalClusterSize = node.SourceInode?.LogicalClusterSize ?? blockSize;
            if (node.SourceInode?.Compressed ?? options.Compress)
                node.Pclusters = TryPlanCompressed(node, blockSize, node.CompressionOptions);
        });

        foreach (var node in ordered)
        {
            if (node.IsDirectory)
            {

                var payloadLength = EstimateDirPayloadLength(node);
                if (payloadLength <= blockSize)
                {
                    node.DirBlocks = 1;
                    node.DirPayloadSize = payloadLength;
                }
                else
                {
                    var layout = MeasureDirLayout(BuildDirEntries(node), blockSize);
                    node.DirBlocks = layout.Blocks;
                    node.DirPayloadSize = layout.Size;
                }

                node.DirectoryInline = payloadLength + GetInodeHeaderSize(node) + node.XattrIbody.Length <= blockSize;
                continue;
            }

            var isDataFile = !node.IsSymlink && !node.IsDevice;
            if (node.IsSymlink)
            {
                node.InlineData = Encoding.UTF8.GetBytes(node.SymlinkTarget);
                continue;
            }
            if (node.Size <= 0)
            {
                continue;
            }

            if (node.SourceInode?.Compressed ?? options.Compress)
            {
                if (node.Pclusters is not null)
                {
                    node.CompressedBlocks = (uint)node.Pclusters.Sum(p => CeilDiv(p.CompressedLength, blockSize));
                    node.ClusterCount = (uint)CeilDiv(node.Size, node.LogicalClusterSize);
                    node.CompactedIndexes = node.SourceInode?.CompactIndexes ?? options.UseCompactedIndexes;

                    node.BigPcluster = !options.LegacyCompatibility
                        && (node.CompressionOptions!.Algorithm != ErofsCompressionAlgorithm.Lz4
                            || node.Pclusters.Any(p => p.CompressedLength > blockSize));
                    var tailLength = node.Pclusters[^1].CompressedLength;
                    if (options.CompressedTailPacking && !node.Pclusters[^1].Plain && tailLength < blockSize
                        && GetInodeRecordSize(node, blockSize) % blockSize + tailLength <= blockSize)
                    {
                        node.InlineCompressedTail = true;
                        node.CompressedBlocks--;
                    }
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
            var inlineFallback = options.SourceInodes is { Count: > 0 } ? false : options.TailPacking;
            if (node.SourceInode?.InlineData ?? inlineFallback)
            {

                node.FlatBlocks = wholeBlocks;
                if (remainder > 0)
                {
                    using var fs = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    fs.Position = wholeBlocks * blockSize;
                    var tail = new byte[remainder];
                    fs.ReadExactly(tail);
                    node.InlineData = tail;
                    if (GetInodeHeaderSize(node) + node.XattrIbody.Length + tail.Length > blockSize)
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

            if (isDataFile && options.Dedup && node.ContentHash is not null && node.FlatBlocks > 0)
            {
                var hashKey = Convert.ToHexString(node.ContentHash);
                if (dedupFlat.TryGetValue(hashKey, out var source) && source.FlatBlocks == node.FlatBlocks)
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
            metaboxResident = ordered.Where(node => IsMetaboxEligible(node, blockSize)).ToList();
            if (metaboxResident.Count > 0)
            {
                var metaboxLength = 0L;
                foreach (var resident in metaboxResident)
                {
                    var recordSize = GetInodeRecordSize(resident, blockSize);
                    if (metaboxLength % blockSize + recordSize > blockSize)
                    {
                        metaboxLength = AlignUp(metaboxLength, blockSize);
                    }
                    resident.MetaboxSlot = metaboxLength / ErofsSuperblock.CompactInodeSize;
                    resident.IsMetaboxResident = true;
                    metaboxLength = checked(metaboxLength + AlignUp(recordSize, ErofsSuperblock.CompactInodeSize));
                }

                metaboxNode = new BuildNode
                {
                    IsMetaboxContainer = true,
                    Size = metaboxLength,
                    FlatBlocks = CeilDiv(metaboxLength, blockSize),
                };
                ordered.Add(metaboxNode);
            }
        }

        var metaOff = inodeTableStart;
        if (metaboxResident is not null)
        {
            foreach (var resident in metaboxResident)
            {
                resident.MetaOffset = -1;
                resident.Nid = NidMetaboxFlag | checked((uint)resident.MetaboxSlot);
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
        ErofsSuperblock.ValidateBlockCountField(totalBlocks);

        foreach (var (alias, canonical) in hardlinks) alias.Nid = canonical.Nid;
        ErofsSuperblock.ValidateRootNidField(root.Nid);

        var finalSize = checked(totalBlocks * blockSize);
        var oldLength = output.Length;
        output.SetLength(finalSize);
        if (!freshDestination)
        {

            ZeroImage(output, Math.Min(oldLength, finalSize));
        }

        var featureIncompat = options.Compress
            ? ErofsSuperblock.IncompatLz4_0Padding | (options.LegacyCompatibility ? 0u : ErofsSuperblock.IncompatBigPcluster)
            : 0;

        if (extraDevices > 0)
        {
            featureIncompat |= ErofsSuperblock.IncompatDeviceTable;
        }

        if (packedNode is not null)
        {
            if (options.LegacyCompatibility)
            {
                throw new NotSupportedException(
                    "LegacyCompatibility images cannot contain fragment or metabox packed nodes.");
            }
            featureIncompat |= ErofsSuperblock.IncompatFragments;
        }
        if (options.CompressedTailPacking) featureIncompat |= ErofsSuperblock.IncompatZtailpacking;

        if (metaboxNode is not null)
        {
            if (options.LegacyCompatibility)
            {
                throw new NotSupportedException(
                    "LegacyCompatibility images cannot contain fragment or metabox packed nodes.");
            }
            featureIncompat |= ErofsSuperblock.IncompatMetabox;
        }

        var superblock = new ErofsSuperblock
        {
            MagicValue = ErofsSuperblock.Magic,
            BlockSizeBits = (byte)BitOperations.Log2((uint)blockSize),
            RootNid = (ushort)root.Nid,
            Inos = (ulong)ordered.Count,
            BuildTime = now,
            BuildTimeNsec = options.BuildTimeNanoseconds,
            BuildTime32 = options.BuildTimeOffset,
            Blocks = (uint)totalBlocks,
            MetaBlkAddr = 0,
            AvailableComprAlgs = CompressedAlgorithmsField(options),
            FeatureCompat = (options.SuperblockChecksum ? ErofsSuperblock.CompatSbChecksum : 0u)
                | (options.SourceMtimeFeature ? ErofsSuperblock.CompatMtime : 0u)
                | (options.SourceXattrFilterFeature ? ErofsSuperblock.CompatXattrFilter : 0u),
            FeatureIncompat = featureIncompat,
            ExtraDevices = (ushort)extraDevices,
            DevtSlotoff = devtSlotOff,
            PackedNid = packedNode?.Nid ?? 0,
            SbExtSlots = metaboxNode is not null ? (byte)1 : (byte)0,
            MetaBoxNid = metaboxNode?.Nid ?? 0,
            Uuid = options.Uuid is { Length: 16 } ? options.Uuid : Guid.NewGuid().ToByteArray(),
            VolumeName = options.VolumeName,
            VolumeNameBytes = options.SourceVolumeNameBytes ?? [],

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
            var descriptors = options.ExtraDeviceImages
                ?? throw new NotSupportedException("EROFS multi-device output requires ExtraDeviceImages descriptors.");
            var slotBuffer = new byte[ErofsDeviceSlot.Size];
            var uniAddr = (ulong)totalBlocks;
            for (var i = 0; i < extraDevices; i++)
            {
                var descriptor = descriptors[i];
                var blocks = (ulong)(descriptor.SizeBytes / blockSize);
                var tag = new byte[ErofsDeviceSlot.TagSize];
                System.Text.Encoding.UTF8.GetBytes(descriptor.Tag, tag);
                var slot = new ErofsDeviceSlot { Tag = tag, Blocks = blocks, UniAddr = uniAddr };
                slot.WriteTo(slotBuffer);
                WriteAt(output, devtOffset + i * ErofsDeviceSlot.Size, slotBuffer, slotBuffer.Length);
                uniAddr += blocks;
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
                WriteCompressedData(output, node, blockSize, node.CompressionOptions!);
            }
            else if (node.IsMetaboxContainer)
            {

                WriteMetaboxContainer(output, node, blockSize, now, metaboxResident!, options.LegacyCompatibility);
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

            var inode = BuildInode(node, blockSize, now, options.LegacyCompatibility);
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

                WriteFragmentHeader(output, node, inode, options);
            }
            else if (node.Pclusters is not null)
            {
                WriteCompressedIndexes(output, AlignUp(cursor, 8), node, blockSize, node.CompressionOptions!);
            }
            else if (node.InlineData.Length > 0)
            {

                WriteAt(output, cursor, node.InlineData, node.InlineData.Length);
            }
        }

        if (options.SuperblockChecksum)
        {
            var crcRegion = output.CapturedWindow ?? throw new InvalidOperationException("Superblock checksum window was not captured.");
            var finalCrc = ComputeCrc32C(crcRegion.AsSpan(8), ErofsSuperblockChecksumSeed);
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