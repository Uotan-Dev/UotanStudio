using System.Runtime.InteropServices;
using System.Text;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.Core.Streams;
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

    [LibraryImport("libc", EntryPoint = "lstat", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LStat(string path, out LinuxStat buf);

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
                    : new UotanStudio.Firmware.Core.ExtractionByteCounter(EstimateSourceBytes(directoryPath, effective.CancellationToken), progress);
                Stream sink = temporary;
                if (counter is not null) sink = counter.Wrap(temporary);
                BuildImage(root, effective, sink, 0, freshDestination: true);
                temporary.Flush(flushToDisk: true);
                counter?.Complete();
                sink.Dispose();
            }

            AtomicPublish.Publish(temporaryPath, fullOutputPath);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private static long EstimateSourceBytes(string directoryPath, CancellationToken token)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
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

        string[] excludedPaths = output is FileStream fileStream
            ? [Path.GetFullPath(fileStream.Name)]
            : [];
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"uotan-erofs-{Guid.NewGuid():N}.tmp");
        var stagingExclusions = excludedPaths.Append(Path.GetFullPath(temporaryPath)).ToArray();
        var effective = ValidateAndCollect(directoryPath, out var root, options, stagingExclusions);
        if (output.CanSeek && StreamResize.SupportsResizeWithoutMutation(output))
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
        root = CollectNodes(directoryPath, exclusions, effective.CancellationToken);
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

    private static BuildNode CollectNodes(string directoryPath, HashSet<string>? excludedPaths, CancellationToken token)
    {
        var rootFull = Path.GetFullPath(directoryPath);
        return CollectNode(rootFull, isRoot: true, excludedPaths, token);
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

    private static BuildNode CollectNode(string fullPath, bool isRoot, HashSet<string>? excludedPaths, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
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

                node.Children.Add(CollectNode(childFullPath, isRoot: false, excludedPaths, token));
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

        /// <summary>Staging-file offset of this pcluster's accepted encoding; -1 for a plain cluster, where the
        /// write pass copies the source bytes as they stand instead of reading the staging file.
        /// <para>该 pcluster 已接受编码在暂存文件中的偏移；原样簇为 -1，写出阶段直接复制源字节而不读暂存文件。</para></summary>
        public long StagedOffset = -1;
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
        var context = ImageBuildContext.Create(root, options, output);
        try
        {
            context.CollectOrderedNodes();
            context.MergeHardlinks();
            context.PlanNodePayloads();
            context.PlanNodeLayouts();
            context.CreatePackedNode();
            context.PlanMetabox();
            var metaOff = context.AssignInodeOffsets();
            var totalBlocks = context.AssignDataAddresses(metaOff);

            var finalSize = checked(totalBlocks * context.BlockSize);
            var oldLength = output.Length;
            output.SetLength(finalSize);
            if (!freshDestination)
            {
                ZeroImage(output, Math.Min(oldLength, finalSize), options.CancellationToken);
            }

            var featureIncompat = context.ComputeFeatureIncompat();
            var superblock = context.BuildSuperblock(totalBlocks, featureIncompat);
            context.WriteSuperblockAndDevices(output, superblock, totalBlocks);
            context.WriteNodeData(output);
            context.WriteInodes(output);
            context.WriteSuperblockChecksum(output);

            output.Position = finalSize;
        }
        finally
        {
            context.StagedCompression?.Dispose();
        }
    }

    private sealed class OffsetStream(Stream inner, long baseOffset) : Stream
    {
        private byte[]? _capture;
        private long _captureStart;

        /// <summary>
        /// Starts capturing writes to [start, start+length) in image coordinates.
        /// <para>开始捕获镜像内 [start, start+length) 的写入字节。</para>
        /// </summary>
        /// <param name="start">First captured byte in the same coordinates as <see cref="Position"/>, so it is
        /// relative to the base offset rather than to the underlying stream.
        /// <para>首个被捕获字节的位置，与 <see cref="Position"/> 使用同一坐标，即相对基准偏移而非底层流的绝对位置。</para></param>
        /// <param name="length">Window width in bytes and length of the array exposed by
        /// <see cref="CapturedWindow"/>; a later call replaces the window captured earlier.
        /// <para>窗口宽度（字节），同时也是 <see cref="CapturedWindow"/> 所暴露数组的长度；再次调用会替换先前设定的窗口。</para></param>
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