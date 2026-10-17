using System.Runtime.InteropServices;
using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.Core.Streams;
using UotanStudio.Firmware.FileSystem.Erofs.Models;
using UotanStudio.Firmware.FileSystem.Metadata;
using ErofsInode = UotanStudio.Firmware.FileSystem.Erofs.Models.Inodes.ErofsInode;

namespace UotanStudio.Firmware.FileSystem.Erofs.Builder;

public sealed partial class ErofsBuilder
{
    private const long InodeTableStart = ErofsSuperblock.SuperblockOffset + ErofsSuperblock.SuperblockSize;

    private const ulong NidMetaboxFlag = 1UL << 63;

    /// <summary>
    /// Upper bound on the chunk index entries one chunk-based inode may carry. The table lives in the
    /// metadata area beside the inode record, so an image that would need more entries than this cannot
    /// be laid out within one metadata block and is refused instead of written.
    /// <para>单个 chunk-based inode 可携带的 chunk 索引表项数量上限。该表位于 inode 记录旁的元数据区，
    /// 因此需要多于该值的表项无法布局在一个元数据块内，此时拒绝写出而不是硬写。</para>
    /// </summary>
    private const int MaxChunkIndexEntries = 1 << 16;

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
    /// <param name="progress">Optional reporter receiving <c>(processed, total)</c> payload bytes, where the total is
    /// the summed size of the collected payload files and every payload is measured exactly once: a payload whose
    /// compression plan was accepted is measured while it is hashed and compressed, and every other payload is
    /// measured when its bytes are emitted. The measurement therefore reaches the total regardless of the
    /// compression ratio. <para>可选的进度回调，报告 <c>(已处理, 总量)</c> 负载字节；总量为已收集负载文件大小之和，
    /// 且每个负载只被计量一次：压缩规划被接受的负载在哈希与压缩时计量，其余负载在写出其字节时计量。因此无论压缩率
    /// 如何，计量都会到达总量。</para></param>
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
                BuildImage(root, effective, temporary, 0, freshDestination: true, progress);
                temporary.Flush(flushToDisk: true);
            }

            AtomicPublish.Publish(temporaryPath, fullOutputPath);
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
        => Build(directoryPath, output, options, null);

    /// <summary>
    /// Streams an EROFS image into <paramref name="output"/> while reporting the same payload measurements as the
    /// path overload, so the rebuild optimizer and the preserved-rebuild path can follow a stream-target build.
    /// <para>把 EROFS 镜像流式写入 <paramref name="output"/>，并上报与路径重载相同的负载计量，使重建优化器与保留
    /// 重建路径能够跟踪以流为目标的构建。</para>
    /// </summary>
    /// <param name="directoryPath">Source tree root. <para>源目录树根。</para></param>
    /// <param name="output">Destination stream; written from its current position. <para>目标流；从其当前位置开始写入。</para></param>
    /// <param name="options">Build options; <see langword="null"/> uses validated defaults.
    /// <para>构建选项；为 <see langword="null"/> 时使用经校验的默认值。</para></param>
    /// <param name="progress">Reporter receiving <c>(processed, total)</c> payload bytes; may be <see langword="null"/>.
    /// <para>接收 <c>(已处理, 总量)</c> 负载字节的回调；可为 <see langword="null"/>。</para></param>
    internal void Build(string directoryPath, Stream output, ErofsOptions? options,
        IProgress<(long Processed, long Total)>? progress)
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
            BuildImage(root, effective, output, baseOffset, freshDestination: false, progress);
            return;
        }

        try
        {
            using (var staging = new FileStream(
                       temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
                       FileOptions.SequentialScan))
            {
                BuildImage(root, effective, staging, 0, freshDestination: true, progress);
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
        out ErofsInode root,
        ErofsOptions? options,
        params string[] excludedPaths)
    {
        Rebuilding.FileSystemPreservation.RejectLossyDirectoryRebuild(directoryPath);
        ArgumentNullException.ThrowIfNull(directoryPath);
        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"Input path is not a directory: '{directoryPath}'.");
        }

        var effective = options ?? new ErofsOptions();
        effective.Validate();
        var excludedHostPaths = excludedPaths.Length == 0
            ? null
            : new HashSet<string>(
                excludedPaths.Select(Path.GetFullPath),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var imageExclusions = new ErofsExclusions(effective.ExcludePaths, effective.ExcludeRegexes);
        imageExclusions.Validate();
        root = CollectNodes(directoryPath, excludedHostPaths, imageExclusions, effective.CancellationToken);
        ApplyMetadataOverrides(root, effective);
        return effective;
    }

    /// <summary>
    /// Applies fs_config (uid/gid/mode) and file_contexts (security.selinux inline xattr) overrides
    /// from the Android-style metadata files onto the collected tree.
    /// <para>将 Android 风格元数据文件中的 fs_config（uid/gid/mode）与 file_contexts
    /// （security.selinux inline xattr）覆盖应用到收集完成的目录树。</para>
    /// </summary>
    private static void ApplyMetadataOverrides(ErofsInode root, ErofsOptions options)
    {
        if (options.FsConfig is null && options.FileContexts is null
            && options.FsConfigPath is null && options.FileContextsPath is null) return;
        var fsConfig = options.FsConfig ??= FileSystemMetadataOverrides.LoadFsConfig(options.FsConfigPath);
        var contexts = options.FileContexts ??= FileSystemMetadataOverrides.LoadFileContexts(options.FileContextsPath);
        var ordered = new List<ErofsInode>();
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
                List<ErofsXattrEntry> labelEntries =
                [
                    new ErofsXattrEntry
                    {
                        Name = "security.selinux",
                        Value = FileSystemMetadataOverrides.ToSelinuxXattrValue(label),
                        NameIndex = ErofsXattr.NameIndexSecurity,
                    }
                ];
                BindXattrEncoding(node,
                    ErofsXattr.Encode(labelEntries, PrefixSeed(options), NameFilterRequested(options)),
                    labelEntries);
            }
        }
    }

    private static ErofsInode CollectNodes(string directoryPath, HashSet<string>? excludedPaths,
        ErofsExclusions exclusions, CancellationToken token)
    {
        var rootFull = Path.GetFullPath(directoryPath);
        return CollectNode(rootFull, string.Empty, isRoot: true, excludedPaths, exclusions, token);
    }

    /// <summary>
    /// Reads Unix metadata (uid/gid/mtime with nanoseconds/permissions/rdev): Linux uses lstat for all fields; other platforms fall back to defaults (uid/gid = 0, mtime = 0, no mode).
    /// <para>读取 Unix 元数据（uid/gid/mtime 含纳秒/权限/设备号）：Linux 通过 lstat 获取全部字段；其他平台使用回退默认值（uid/gid = 0，mtime = 0，不带权限）。</para>
    /// </summary>
    /// <returns><see langword="true"/> when <c>lstat</c> supplied a real mode for this entry, so a zero
    /// permission word means "no metadata" rather than mode 0000.
    /// <para>当 <c>lstat</c> 为该条目提供了真实权限位时返回 <see langword="true"/>，从而让全零权限表示"无元数据"而不是 0000 模式。</para></returns>
    private static bool ReadUnixMetadata(
        string fullPath,
        out uint uid,
        out uint gid,
        out long mtime,
        out uint mtimeNanoseconds,
        out ushort permissions,
        out bool isSpecialFile,
        out uint deviceRdev,
        out ushort deviceMode)
    {
        uid = 0;
        gid = 0;
        mtime = 0;
        mtimeNanoseconds = 0;
        permissions = 0;
        isSpecialFile = false;
        deviceRdev = 0;
        deviceMode = 0;

        if (!OperatingSystem.IsLinux()) return false;

        try
        {
            var buf = new LinuxStat();
            if (LStat(fullPath, out buf) != 0) return false;

            uid = (uint)buf.st_uid;
            gid = (uint)buf.st_gid;
            mtime = buf.st_mtime;
            mtimeNanoseconds = (uint)buf.st_mtime_nsec;
            permissions = (ushort)(buf.st_mode & 0xFFF);

            var fileType = buf.st_mode & 0xF000;
            if (fileType is 0x2000 or 0x6000 or 0x1000 or 0xC000)
            {
                isSpecialFile = true;
                deviceRdev = EncodeUnixDeviceId(buf.st_rdev);
                deviceMode = (ushort)buf.st_mode;
            }
        }
        catch
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Converts a glibc <c>st_rdev</c> into the device identifier EROFS stores in <c>i_rdev</c>: the major
    /// number sits at bit 8 and the high minor bits at bit 24. The host value is a 64-bit pair of fields, so it
    /// has to be taken apart with the glibc <c>gnu_dev_major</c>/<c>gnu_dev_minor</c> layout before it is folded
    /// back into 32 bits; ordinary Android device nodes (major below 4096, minor below 256) land on the same
    /// number either way, while a huge major number is only preserved by going through the fields.
    /// <para>把 glibc 的 <c>st_rdev</c> 转成 EROFS 写入 <c>i_rdev</c> 的设备号：磁盘形式把主号放在第 8 位、
    /// 次号高位放在第 24 位。宿主值是 64 位的双字段编码，必须先用 glibc 的
    /// <c>gnu_dev_major</c>/<c>gnu_dev_minor</c> 布局拆开，再折回 32 位。常见的 Android 设备节点
    /// （主号小于 4096、次号小于 256）两种写法结果相同，只有超大主号必须经过字段拆分才能保留。</para>
    /// </summary>
    /// <param name="stRdev">Raw <c>st_rdev</c> as reported by <c>lstat</c>. <para><c>lstat</c> 报告的原始 <c>st_rdev</c>。</para></param>
    /// <returns>The 32-bit on-disk device identifier. <para>写入磁盘的 32 位设备号。</para></returns>
    internal static uint EncodeUnixDeviceId(ulong stRdev)
    {
        var major = (uint)(((stRdev >> 8) & 0xfff) | ((stRdev >> 32) & 0xffff0000));
        var minor = (uint)((stRdev & 0xff) | ((stRdev >> 12) & 0xfff00));
        return (minor & 0xff) | (major << 8) | ((minor & ~0xffu) << 12);
    }

    /// <summary>
    /// Collects one host path into the node tree. Generated repack metadata is never payload: a
    /// <c>metadata.json</c> the extraction wrote into the tree root, or one whose shape
    /// <see cref="Core.Repacking.RepackMetadata.IsMetadataFile"/> validates, is skipped
    /// under an active edit plan too, where the plan membership test alone would have collected it. The
    /// plan test is still applied, so a file the plan does not project stays out as well.
    /// <para>把一个宿主路径收集为节点树。生成的打包元数据绝不是负载：解包写进目录树根部的
    /// <c>metadata.json</c>，或经
    /// <see cref="Core.Repacking.RepackMetadata.IsMetadataFile"/> 校验过的同形态文件，
    /// 在编辑计划生效时同样跳过，否则仅凭计划成员判定会把它收集进来。计划判定仍然生效，因此计划未投影的
    /// 文件同样不会进入。</para>
    /// </summary>
    /// <param name="fullPath">Absolute host path to collect. <para>待收集的宿主绝对路径。</para></param>
    /// <param name="imagePath">Image-relative <c>/</c>-separated path of this entry, empty for the build
    /// root, which is the spelling the exclusion rules compare against.
    /// <para>该条目的镜像内相对路径，以 <c>/</c> 分隔，构建根为空；排除规则正是与该写法比较。</para></param>
    /// <param name="isRoot">Whether the path is the build root, whose node carries an empty name.
    /// <para>该路径是否为构建根，其节点名称相应为空。</para></param>
    /// <param name="excludedPaths">Absolute host paths to leave out, or <see langword="null"/> for none.
    /// <para>要排除的宿主绝对路径；无则为 <see langword="null"/>。</para></param>
    /// <param name="exclusions">Image-relative exclusion rules applied to every child before it is read.
    /// <para>在读取每个子项之前应用的镜像内相对路径排除规则。</para></param>
    /// <param name="token">Cancels collection. <para>取消收集。</para></param>
    /// <returns>The collected node. <para>收集到的节点。</para></returns>
    private static ErofsInode CollectNode(string fullPath, string imagePath, bool isRoot,
        HashSet<string>? excludedPaths, ErofsExclusions exclusions, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var attributes = File.GetAttributes(fullPath);
        var name = isRoot ? string.Empty : Path.GetFileName(fullPath);

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            var target = string.Empty;
            try { target = new FileInfo(fullPath).LinkTarget ?? string.Empty; } catch { }
            return new ErofsInode
            {
                Name = name,
                IsDirectory = false,
                FullPath = fullPath,
                IsSymlink = true,
                SymlinkTarget = target,
                Size = (ulong)Encoding.UTF8.GetByteCount(target),
            };
        }

        var hasUnixMode = ReadUnixMetadata(
            fullPath,
            out var uid,
            out var gid,
            out var mtime,
            out var mtimeNanoseconds,
            out var permissions,
            out var isSpecialFile,
            out var deviceRdev,
            out var deviceMode);

        if (isSpecialFile)
        {
            return new ErofsInode
            {
                Name = name,
                IsDirectory = false,
                FullPath = fullPath,
                IsDevice = true,
                DeviceRdev = deviceRdev,
                DeviceMode = deviceMode,
                Uid = uid,
                Gid = gid,
                CTime = (ulong)mtime,
                CTimeNsec = mtimeNanoseconds,
                ModeOverride = permissions,
                HasModeOverride = hasUnixMode,
            };
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            var node = new ErofsInode
            {
                Name = name,
                IsDirectory = true,
                FullPath = fullPath,
                Uid = uid,
                Gid = gid,
                CTime = (ulong)mtime,
                CTimeNsec = mtimeNanoseconds,
                ModeOverride = permissions,
                HasModeOverride = hasUnixMode,
            };
            foreach (var childPath in Directory.EnumerateFileSystemEntries(fullPath))
            {
                if (Core.Repacking.RepackMetadata.IsMetadataFile(childPath)
                    || (Rebuilding.FileSystemEditPlan.Current is { } plan
                        && !plan.Includes(childPath)))
                {
                    continue;
                }

                var childName = Path.GetFileName(childPath);
                var childImagePath = imagePath.Length == 0 ? childName : $"{imagePath}/{childName}";
                if (exclusions.IsExcluded(childImagePath))
                {
                    continue;
                }

                var childFullPath = Path.GetFullPath(childPath);
                if (excludedPaths?.Contains(childFullPath) == true)
                {
                    continue;
                }

                node.Children.Add(CollectNode(childFullPath, childImagePath, isRoot: false, excludedPaths,
                    exclusions, token));
            }

            return node;
        }

        var info = new FileInfo(fullPath);
        return new ErofsInode
        {
            Name = name,
            IsDirectory = false,
            FullPath = fullPath,
            Size = ToInodeSize(info.Length),
            Uid = uid,
            Gid = gid,
            CTime = (ulong)mtime,
            CTimeNsec = mtimeNanoseconds,
            ModeOverride = permissions,
            HasModeOverride = hasUnixMode,
        };
    }

    private static ulong ToInodeSize(long length)
        => length >= 0
            ? (ulong)length
            : throw new ArgumentOutOfRangeException(nameof(length), length, "EROFS inode size cannot be negative.");

    private static void CollectPreOrder(ErofsInode node, List<ErofsInode> ordered)
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
    private static void BindXattrs(List<ErofsInode> ordered, ErofsOptions options)
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

            BindXattrEncoding(node, ErofsXattr.Encode(entries, PrefixSeed(options), NameFilterRequested(options)), entries);
        }
    }

    /// <summary>
    /// The records an encode starts from, for the collection phase that runs before the image context
    /// exists: the inherited source records, the configured name prefixes and the fingerprint prefix, all
    /// registered verbatim so a name that carries a configured prefix is already compressible on the first
    /// pass rather than only after the prefix table is planned.
    /// <para>编码的起始记录集合，供镜像上下文建立之前的收集阶段使用：继承的源记录、配置的名称前缀与指纹
    /// 前缀，全部逐字注册，使携带配置前缀的名称在首轮编码时即可被压缩，而不必等到前缀表规划之后。</para>
    /// </summary>
    /// <param name="options">Validated build options.<para>经校验的构建选项。</para></param>
    /// <returns>The seed records in table order.<para>按表序的起始记录。</para></returns>
    private static List<(byte BaseIndex, string Infix)> PrefixSeed(ErofsOptions options)
    {
        var seed = new List<(byte BaseIndex, string Infix)>(options.SourceXattrPrefixes ?? []);
        if (options.XattrNamePrefixes is { Count: > 0 } configured)
        {
            foreach (var prefixText in configured)
            {
                AddPrefixRecord(seed, prefixText);
            }
        }

        if (options.IshareXattrPrefix is { Length: > 0 } prefix)
        {
            AddPrefixRecord(seed, prefix);
        }

        return seed;
    }

    /// <summary>
    /// Appends a configured prefix to a seed table unless an earlier record already reconstructs the same
    /// text, keeping every index in the table unique and stable between the collection and layout phases.
    /// <para>把一个配置的前缀追加到起始表；若已有记录能还原出相同文本则跳过，使收集阶段与布局阶段共享同一
    /// 组唯一且稳定的表内索引。</para>
    /// </summary>
    /// <param name="seed">Seed table to append to.<para>待追加的起始表。</para></param>
    /// <param name="prefix">Whole prefix text to register verbatim.<para>逐字注册的完整前缀文本。</para></param>
    private static void AddPrefixRecord(List<(byte BaseIndex, string Infix)> seed, string prefix)
    {
        if (!seed.Any(record => ErofsXattr.PrefixText(record) == prefix))
        {
            seed.Add(ErofsXattr.SplitPrefix(prefix));
        }
    }

    /// <summary>
    /// Whether this build writes per-inode xattr name filters, for the collection phase that runs before
    /// the image context exists.
    /// <para>本次构建是否写出逐 inode 的 xattr 名称过滤器，供镜像上下文建立之前的收集阶段使用。</para>
    /// </summary>
    /// <param name="options">Validated build options.<para>经校验的构建选项。</para></param>
    /// <returns><see langword="true"/> when the filter words are written.
    /// <para>写出过滤字时为 <see langword="true"/>。</para></returns>
    private static bool NameFilterRequested(ErofsOptions options)
        => options.XattrNameFilter
            || options.SourceXattrFilterFeature
            || (options.FeaturesAuthoritative && options.Features.Contains(ErofsFeature.XattrFilter));

    /// <summary>
    /// Stores one node's encoded inline ibody together with the long-prefix records its names registered.
    /// The ibody is final, but the prefix records are a per-node contribution the image flushes as one
    /// shared table, so identical records from several nodes collapse into one table row.
    /// <para>保存单个节点编码后的 inline ibody 及其名称登记的长前缀记录。ibody 已是最终形态，而前缀
    /// 记录是逐节点贡献，镜像将其合并为一张共享表，因此多个节点的相同记录会合并为一行。</para>
    /// </summary>
    private static void BindXattrEncoding(ErofsInode node, ErofsXattrEncoding encoding,
        IReadOnlyList<ErofsXattrEntry> entries)
    {
        node.XattrIbody = encoding.Ibody;
        node.XattrEntries = [.. entries];
        if (encoding.PrefixTable is { Count: > 0 } prefixes)
        {
            node.XattrPrefixes = [.. prefixes];
        }
    }

    /// <summary>
    /// Image-relative path of a node ('/'-separated; root is "").
    /// <para>节点的镜像内相对路径（'/' 分隔；根为 ""）。</para>
    /// </summary>
    private static string BuildImagePath(ErofsInode node)
    {
        var parts = new List<string>();
        for (var current = node; current.Parent is not null; current = current.Parent)
        {
            parts.Add(current.Name);
        }

        parts.Reverse();
        return string.Join('/', parts);
    }

    /// <summary>
    /// Decides whether an inode needs the 64-byte extended record: a compact record stores <c>i_mtime</c>
    /// only as an unsigned 32-bit delta from the superblock epoch and has no per-inode nanosecond field
    /// (all compact inodes share <c>s_fixed_nsec</c>), so an unrepresentable timestamp requires the extended form.
    /// <para>判断 inode 是否需要 64 字节的 extended 记录：compact 记录的 <c>i_mtime</c> 只是相对超级块
    /// epoch 的无符号 32 位增量且没有逐 inode 纳秒字段（所有 compact inode 共用 <c>s_fixed_nsec</c>），
    /// 因此无法表示的时间戳必须改用 extended 形式。</para>
    /// </summary>
    /// <param name="timestamp">Effective inode seconds. <para>生效的 inode 秒数。</para></param>
    /// <param name="epoch">Superblock epoch seconds. <para>超级块 epoch 秒数。</para></param>
    /// <param name="nanoseconds">Per-inode sub-second part. <para>逐 inode 的亚秒部分。</para></param>
    /// <returns><see langword="true"/> when the extended record is required.
    /// <para>必须使用 extended 记录时为 <see langword="true"/>。</para></returns>
    internal static bool NeedsExtendedInodeTime(ulong timestamp, ulong epoch, uint nanoseconds)
        => timestamp < epoch || timestamp - epoch > uint.MaxValue || nanoseconds != 0;

    /// <summary>
    /// Reports whether the feature declaration permits per-node big pclusters. An authoritative
    /// declaration is the authority, so a rebuilt image cannot advertise a layout its source lacked; a
    /// derived declaration asks the options whether the planned pcluster limit actually exceeds one block,
    /// which is what keeps the default single-block layout free of the bit.
    /// <para>判断特性声明是否允许逐节点 big pcluster。权威声明即为依据，因此重建镜像不会声明源镜像缺失的
    /// 布局；推导式声明改问选项规划的 pcluster 上限是否真超过一块，这正是默认单块布局不带该位的原因。</para>
    /// </summary>
    /// <param name="options">Validated build options carrying the declaration.
    /// <para>携带声明的经校验构建选项。</para></param>
    /// <returns><see langword="true"/> when pclusters may span more than one filesystem block.
    /// <para>允许 pcluster 跨越多个文件系统块时为 <see langword="true"/>。</para></returns>
    private static bool DeclaredBigPcluster(ErofsOptions options)
        => options.FeaturesAuthoritative
            ? options.Features.Contains(ErofsFeature.BigPcluster)
            : options.ModernCompressionLayoutRequested;

    /// <summary>
    /// Reports whether the declaration advertises the compression configuration records that follow the
    /// superblock. The records share incompat bit 0x00000002 with big pclusters, so an image without them
    /// also carries no algorithm bitmap and keeps the legacy max-distance encoding in
    /// <c>s_available_compr_algs</c>. A derived declaration resolves to the same single predicate as
    /// <see cref="DeclaredBigPcluster"/>, because one bit licenses both layouts.
    /// <para>判断声明是否写出紧随超级块的压缩配置记录。该记录与 big pcluster 共用 incompat 位 0x00000002，
    /// 因此无该记录的镜像也不携带算法位图，<c>s_available_compr_algs</c> 沿用 legacy 的最大距离编码。
    /// 推导式声明落到与 <see cref="DeclaredBigPcluster"/> 相同的单一判据，因为一位许可两种布局。</para>
    /// </summary>
    /// <param name="options">Validated build options carrying the declaration.
    /// <para>携带声明的经校验构建选项。</para></param>
    /// <returns><see langword="true"/> when configuration records may be written.
    /// <para>允许写出压缩配置记录时为 <see langword="true"/>。</para></returns>
    private static bool DeclaredCompressionConfigs(ErofsOptions options)
        => options.FeaturesAuthoritative
            ? options.Features.Contains(ErofsFeature.CompressionConfigs)
            : options.ModernCompressionLayoutRequested;

    /// <summary>
    /// Reports whether the declaration advertises ztailpacking, the incompat bit licensing a final
    /// compressed cluster stored inline beside its indexes.
    /// <para>判断声明是否 advertise ztailpacking，即允许把最后一个压缩簇内联存放在其索引旁的 incompat
    /// 位。</para>
    /// </summary>
    /// <param name="options">Validated build options carrying the declaration.
    /// <para>携带声明的经校验构建选项。</para></param>
    /// <returns><see langword="true"/> when an inline compressed tail may be written.
    /// <para>允许写出内联压缩尾部时为 <see langword="true"/>。</para></returns>
    private static bool DeclaredZtailpacking(ErofsOptions options)
        => options.FeaturesAuthoritative
            ? options.Features.Contains(ErofsFeature.Ztailpacking)
            : !options.LegacyCompatibility;

    /// <summary>
    /// Resolves the <c>48bit</c> incompat bit, which splits the superblock block count into
    /// <c>s_blocks</c> plus <c>s_blocks_hi</c> and moves the root inode number into the 64-bit
    /// <c>s_rootnid_8b</c> field. A derived declaration answers from the image's own size, because a
    /// superblock whose high half does not fit cannot publish the count any other way.
    /// <para>解析 <c>48bit</c> incompat 位，它把超级块块数拆为 <c>s_blocks</c> 与 <c>s_blocks_hi</c>，
    /// 并把根 inode 编号移入 64 位的 <c>s_rootnid_8b</c> 字段。推导式声明依据镜像自身大小作答，因为高半部分
    /// 装不下的超级块无法以其他方式发布该计数。</para>
    /// </summary>
    /// <param name="options">Validated build options carrying the declaration.
    /// <para>携带声明的经校验构建选项。</para></param>
    /// <param name="totalBlocks">Total image block count the superblock has to publish.
    /// <para>超级块必须发布的镜像总块数。</para></param>
    /// <returns><see langword="true"/> when the wide superblock fields are written.
    /// <para>写出宽超级块字段时为 <see langword="true"/>。</para></returns>
    internal static bool DeclaredFortyEightBit(ErofsOptions options, long totalBlocks)
        => options.FeaturesAuthoritative
            ? options.Features.Contains(ErofsFeature.FortyEightBit)
            : totalBlocks > uint.MaxValue;

    /// <summary>
    /// Resolves the inode advise bit shared by <c>EROFS_I_NLINK_1</c> and dot-omitted directories from
    /// the tri-state declaration: an enabled declaration always allows it, a disabled one never does, and
    /// <see cref="ErofsTriStateFeature.Auto"/> keeps the legacy-compatibility answer so derived builds are
    /// byte-identical.
    /// <para>由三态声明解析 inode advise 位（<c>EROFS_I_NLINK_1</c> 与省略点项目录共用）：启用声明一律允许，
    /// 禁用声明一律不允许，<see cref="ErofsTriStateFeature.Auto"/> 沿用 legacy 兼容的结论，使推导式构建逐字节
    /// 不变。</para>
    /// </summary>
    /// <param name="options">Validated build options whose <see cref="ErofsOptions.DotOmitted"/> is already
    /// resolved. <para>其 <see cref="ErofsOptions.DotOmitted"/> 已完成解析的经校验构建选项。</para></param>
    /// <returns><see langword="true"/> when the advise bit may be set on a compact inode.
    /// <para>允许在紧凑 inode 上置该 advise 位时为 <see langword="true"/>。</para></returns>
    private static bool DeclaredNlinkOneAdvice(ErofsOptions options)
        => options.DotOmitted switch
        {
            ErofsTriStateFeature.Enabled => true,
            ErofsTriStateFeature.Disabled => false,
            _ => !options.LegacyCompatibility,
        };

    /// <summary>
    /// Reports whether the nlink-one / dot-omitted advise bit may be written. Only newer kernels define the
    /// bit, and a kernel that does not define it rejects the inode, so the bit is written only when the caller
    /// declares a target that genuinely defines it.
    /// <para>判断是否可以写出 nlink-one / 省略点项 advise 位。只有较新内核定义该位，未定义它的内核会拒绝该
    /// inode，因此只有调用方声明的目标确实定义该位时才写出。</para>
    /// </summary>
    /// <param name="options">Validated build options whose feature declaration is already resolved.
    /// <para>特性声明已完成解析的经校验构建选项。</para></param>
    /// <returns><see langword="true"/> only when the declaration targets a kernel defining the bit.
    /// <para>仅当声明针对定义了该位的内核时为 <see langword="true"/>。</para></returns>
    private static bool KernelDefinesNlinkOneAdvice(ErofsOptions options)
        => options.NlinkOneAdviseSupported;

    /// <summary>
    /// Reports whether the declaration still allows the superblock checksum. A derived declaration keeps the
    /// option's own answer; an authoritative one that omits the compat bit forbids the checksum field, so the
    /// emitted superblock never carries a CRC the word does not advertise.
    /// <para>判断声明是否仍允许超级块校验和。推导式声明沿用选项自身的结论；缺少该 compat 位的权威声明禁止写入校验和
    /// 字段，因此写出的超级块绝不会携带其字未 advertise 的 CRC。</para>
    /// </summary>
    /// <param name="options">Validated build options carrying the declaration.
    /// <para>携带声明的经校验构建选项。</para></param>
    /// <returns><see langword="true"/> when the checksum field may be written.
    /// <para>允许写出校验和字段时为 <see langword="true"/>。</para></returns>
    private static bool DeclaredSuperblockChecksum(ErofsOptions options)
        => !options.FeaturesAuthoritative || options.Features.Contains(ErofsFeature.SuperblockChecksum);

    /// <summary>Computes s_available_compr_algs: the LZ4 max distance when the declaration carries no
    /// compression configuration records, otherwise the bitmap of every algorithm the build can encode
    /// with, so no node is written with an undeclared algorithm.
    /// <para>计算 s_available_compr_algs：声明未携带压缩配置记录时承载 LZ4 最大匹配距离，否则为本次构建
    /// 可能使用的全部算法位图，确保不会有节点以未声明的算法编码。</para></summary>
    /// <param name="options">Validated build options carrying the declaration and the preserved source
    /// records. <para>携带声明与保留源记录的经校验构建选项。</para></param>
    /// <returns>The 16-bit superblock field value.
    /// <para>超级块 16 位字段的取值。</para></returns>
    private static ushort CompressedAlgorithmsField(ErofsOptions options)
    {
        if (!options.Compress)
        {
            return 0;
        }

        if (!DeclaredCompressionConfigs(options))
        {
            return options.Lz4MaxDistance == 0 ? ErofsCompression.Lz4DistanceMax : options.Lz4MaxDistance;
        }

        var mask = 1 << (int)options.Algorithm;
        if (options.SourceCompressionOptions is { } codecs)
        {
            foreach (var algorithm in codecs.Keys)
            {
                mask |= 1 << (int)algorithm;
            }
        }

        if (options.SourceInodes is { } inodes)
        {
            foreach (var inode in inodes.Values)
            {
                if (inode.SourceCompressed)
                {
                    mask |= 1 << (int)inode.SourceAlgorithm;
                }
            }
        }

        return (ushort)mask;
    }

    private static void BuildImage(ErofsInode root, ErofsOptions options, Stream destination, long baseOffset,
        bool freshDestination, IProgress<(long Processed, long Total)>? progress = null)
    {
        using var output = new OffsetStream(destination, baseOffset);
        var context = ImageBuildContext.Create(root, options, output);
        try
        {
            context.CollectOrderedNodes();
            context.MergeHardlinks();
            var payloadBytes = progress is null ? null
                : new ExtractionByteCounter(context.PayloadByteTotal(), progress, options.CancellationToken);
            context.PlanNodePayloads(payloadBytes);

            context.PlanInodeFingerprints(options);
            context.PlanXattrPrefixTable();
            context.PlanSharedXattrs();
            context.PlanNodeLayouts();
            context.CreatePackedNode();
            context.PlanMetabox();
            var metaOff = context.AssignInodeOffsets();
            var totalBlocks = context.AssignDataAddresses(metaOff);
            context.TotalBlocks = totalBlocks;

            context.VerifyPreservedStorageStrategies();

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
            context.WriteNodeData(output, payloadBytes);
            context.WriteInodes(output);
            context.WriteSuperblockChecksum(output);
            payloadBytes?.Complete();

            output.Position = finalSize;
        }
        finally
        {
            context.StagedCompression?.Dispose();
            context.DeleteMetaboxBodyFile();
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