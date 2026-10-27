using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;

namespace UotanStudio.Firmware.FileSystem.Fat;

/// <summary>
/// Builds FAT12, FAT16 and FAT32 volume images from a host directory. The writer plans the whole tree first,
/// derives a layout whose boot record, allocation table width and directory limits agree with the chosen
/// variant, and only then streams the file contents cluster by cluster, so no source file is ever held in
/// memory and a failing build publishes nothing over the previous output.
/// <para>从宿主目录构建 FAT12、FAT16 与 FAT32 卷镜像。写入方先规划整棵树，推导出引导记录、分配表宽度与目录
/// 上限都与所选变体一致的布局，然后才按簇流式写出文件内容，因此任何源文件都不会整体进入内存，失败的构建也
/// 不会覆盖既有输出。</para>
/// </summary>
public static partial class FatBuilder
{
    internal const int DirectoryEntryBytes = 32;
    internal const long MaximumVolumeBytes = 2L * 1024 * 1024 * 1024 * 1024;
    internal const long Fat12MaximumClusters = 4_084;
    internal const long Fat16MinimumClusters = 4_085;
    internal const long Fat16MaximumClusters = 65_524;
    internal const long Fat32MinimumClusters = 65_525;
    internal const long Fat32MaximumClusters = 0x0FFFFFF0;
    private const long MaximumFatBytes = 64L * 1024 * 1024;
    private const int MaximumDirectoryBytes = 64 * 1024 * 1024;
    private const int FatCount = 2;

    /// <summary>
    /// Packs <paramref name="sourceDirectory"/> into a FAT volume image at <paramref name="outputPath"/>.
    /// <para>把 <paramref name="sourceDirectory"/> 打包为 <paramref name="outputPath"/> 处的 FAT 卷镜像。</para>
    /// </summary>
    /// <param name="sourceDirectory">Directory whose contents become the volume; its repack metadata file is never packed.
    /// <para>内容将构成该卷的目录；其重打包元数据文件不会被写入镜像。</para></param>
    /// <param name="outputPath">Destination image path; the parent directory is created and an existing file is
    /// replaced only after the whole image has been written successfully.
    /// <para>目标镜像路径；自动创建父目录，仅在整张镜像成功写出后才替换已存在的文件。</para></param>
    /// <param name="options">Geometry and identity settings; <see langword="null"/> applies <see cref="FatBuildOptions"/> defaults.
    /// <para>几何与标识设置；为 <see langword="null"/> 时使用 <see cref="FatBuildOptions"/> 默认值。</para></param>
    /// <param name="progress">Receives cumulative file bytes written and the total; <see langword="null"/> reports nothing.
    /// <para>接收累计已写出的文件字节数与总量；为 <see langword="null"/> 时不上报。</para></param>
    /// <param name="cancellationToken">Checked while walking, planning and copying.
    /// <para>在遍历、规划与复制过程中检查。</para></param>
    /// <returns>The geometry and counts of the volume that was written.
    /// <para>已写出卷的几何与计数。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="sourceDirectory"/> or <paramref name="outputPath"/> is blank.
    /// <para><paramref name="sourceDirectory"/> 或 <paramref name="outputPath"/> 为空白。</para></exception>
    /// <exception cref="DirectoryNotFoundException"><paramref name="sourceDirectory"/> does not exist.
    /// <para><paramref name="sourceDirectory"/> 不存在。</para></exception>
    /// <exception cref="InvalidDataException">The options, a name, or the content geometry is not representable in the
    /// requested variant. <para>选项、名称或内容几何无法用所请求的变体表示。</para></exception>
    /// <exception cref="FileSystemCorruptedException">The tree exceeds the entry-count, depth or per-directory
    /// size limit. <para>目录树超过条目数、深度或单目录大小上限。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已被触发。</para></exception>
    public static FatBuildReport Build(string sourceDirectory, string outputPath, FatBuildOptions? options = null,
        IProgress<FileSystemByteProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException($"The FAT source directory does not exist: '{sourceDirectory}'.");
        }

        var settings = (options ?? new FatBuildOptions()).Validated();
        cancellationToken.ThrowIfCancellationRequested();
        var plan = Plan(sourceDirectory, settings, cancellationToken);
        var byteProgress = FileSystemByteProgress.ForStage(progress, FileSystemWorkStage.BuildingImage);
        byteProgress?.Report((0, plan.DataBytes));
        FileExtraction.WriteAtomic(outputPath, stream => Write(stream, plan, byteProgress, cancellationToken),
            overwrite: true, cancellationToken);
        return plan.Report;
    }

    /// <summary>
    /// Describes the FAT volume one build produced.
    /// <para>描述一次构建所产出的 FAT 卷。</para>
    /// </summary>
    /// <param name="Variant">The written variant name: <c>FAT12</c>, <c>FAT16</c> or <c>FAT32</c>.
    /// <para>写出的变体名：<c>FAT12</c>、<c>FAT16</c> 或 <c>FAT32</c>。</para></param>
    /// <param name="BytesPerSector">Logical bytes per sector written to the boot record.
    /// <para>写入引导记录的每扇区逻辑字节数。</para></param>
    /// <param name="SectorsPerCluster">File-system sectors in one cluster.
    /// <para>一个簇占用的文件系统扇区数。</para></param>
    /// <param name="TotalClusters">Addressable clusters in the data area.
    /// <para>数据区中可寻址的簇数。</para></param>
    /// <param name="VolumeBytes">Total image bytes the boot record declares.
    /// <para>引导记录声明的镜像总字节数。</para></param>
    /// <param name="EntryCount">Entries written, excluding the volume label.
    /// <para>已写出的条目数，不含卷标。</para></param>
    /// <param name="FileCount">Regular files written.
    /// <para>已写出的常规文件数。</para></param>
    /// <param name="DataBytes">Cumulative file bytes written.
    /// <para>已写出的文件字节总数。</para></param>
    public sealed record FatBuildReport(string Variant, int BytesPerSector, int SectorsPerCluster,
        long TotalClusters, long VolumeBytes, int EntryCount, int FileCount, long DataBytes);

    private sealed class FatNode(string name, bool isDirectory)
    {
        internal string Name { get; } = name;
        internal bool IsDirectory { get; } = isDirectory;
        internal string SourcePath { get; set; } = string.Empty;
        internal long Length { get; set; }
        internal FileAttributes Attributes { get; set; }
        internal DateTime LastWriteTimeUtc { get; set; } = DateTime.UtcNow;
        internal FatNode? Parent { get; set; }
        internal List<FatNode> Children { get; } = [];
        internal byte[] ShortName { get; set; } = new byte[11];
        internal byte[][] LongNameEntries { get; set; } = [];
        internal int EntryBytes => DirectoryEntryBytes * (1 + LongNameEntries.Length);
        internal List<int> Clusters { get; } = [];
        internal int DirectoryBytes { get; set; }
    }

    private sealed record FatPlan(FatNode Root, Layout Layout, IReadOnlyList<FatNode> Directories,
        IReadOnlyList<FatNode> Files, long DataBytes, int EntryCount, int FileCount)
    {
        internal FatBuildReport Report => new(Layout.VariantName, Layout.BytesPerSector, Layout.SectorsPerCluster,
            Layout.ClusterCount, Layout.VolumeBytes, EntryCount, FileCount, DataBytes);
    }

    private sealed record Layout(string VariantName, FatVariant Variant, int BytesPerSector, int SectorsPerCluster,
        int ReservedSectors, int RootDirectoryEntries, int SectorsPerFat, long TotalSectors, long ClusterCount,
        long FatStart, long RootStart, long DataStart, long VolumeBytes, long ClusterBytes,
        byte MediaDescriptor, int HiddenSectors, string VolumeLabel, string OemName);

    private static FatPlan Plan(string sourceDirectory, FatBuildOptions options, CancellationToken cancellationToken)
    {
        var root = new FatNode(string.Empty, true)
        {
            SourcePath = sourceDirectory,
            LastWriteTimeUtc = Directory.GetLastWriteTimeUtc(sourceDirectory),
            Attributes = FileAttributes.Directory,
        };
        var (entryCount, dataBytes) = Walk(root, cancellationToken);
        AssignNames(root, options);
        var clusterBytes = ChooseClusterBytes(options, dataBytes);
        var requiredClusters = RequiredClusters(root, clusterBytes);
        var layout = ResolveLayout(options, clusterBytes, requiredClusters);
        if (layout.Variant != FatVariant.Fat32
            && root.DirectoryBytes > layout.RootDirectoryEntries * DirectoryEntryBytes)
        {
            throw new InvalidDataException(
                $"The root directory needs {root.DirectoryBytes / DirectoryEntryBytes} slots but a {layout.VariantName} volume stores {layout.RootDirectoryEntries}; raise RootDirectoryEntries or build FAT32.");
        }

        var directories = new List<FatNode>();
        var files = new List<FatNode>();
        Collect(root, directories, files);
        Allocate(root, directories, files, layout, requiredClusters);
        return new FatPlan(root, layout, directories, files, dataBytes, entryCount, files.Count);
    }

    private static (int Entries, long DataBytes) Walk(FatNode root, CancellationToken cancellationToken)
    {
        var entries = 0;
        var dataBytes = 0L;
        var queue = new Queue<(FatNode Node, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = queue.Dequeue();
            if (depth > FatVolumeTree.MaximumDepth)
            {
                throw new FileSystemCorruptedException("The source directory tree exceeds the supported depth.");
            }

            var children = new List<FatNode>();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory.SourcePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"FAT cannot represent the reparse point or symbolic link '{path}'; pack a plain directory tree.");
                }

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (!isDirectory && RepackMetadata.IsMetadataFile(path))
                {
                    continue;
                }

                var info = new FileInfo(path);
                var node = new FatNode(Path.GetFileName(path), isDirectory)
                {
                    SourcePath = path,
                    LastWriteTimeUtc = isDirectory ? Directory.GetLastWriteTimeUtc(path) : info.LastWriteTimeUtc,
                    Attributes = attributes,
                    Length = isDirectory ? 0 : info.Length,
                };
                if (!isDirectory)
                {
                    if (node.Length > uint.MaxValue)
                    {
                        throw new InvalidDataException(
                            $"FAT cannot store the file '{path}', which is {node.Length} bytes long; the size field is 32 bits.");
                    }

                    dataBytes = checked(dataBytes + node.Length);
                }

                if (++entries > FatVolumeTree.MaximumEntries)
                {
                    throw new FileSystemCorruptedException("The source tree stores more entries than the supported limit.");
                }

                children.Add(node);
                if (isDirectory)
                {
                    queue.Enqueue((node, depth + 1));
                }
            }

            children.Sort(static (left, right) =>
            {
                var byName = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
                return byName != 0 ? byName : StringComparer.Ordinal.Compare(left.Name, right.Name);
            });
            foreach (var child in children)
            {
                child.Parent = directory;
                directory.Children.Add(child);
            }
        }

        return (entries, dataBytes);
    }

    private static void Collect(FatNode root, List<FatNode> directories, List<FatNode> files)
    {
        var queue = new Queue<FatNode>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node.IsDirectory)
            {
                directories.Add(node);
                foreach (var child in node.Children)
                {
                    queue.Enqueue(child);
                }
            }
            else
            {
                files.Add(node);
            }
        }
    }

    private static int ChooseClusterBytes(FatBuildOptions options, long dataBytes)
    {
        if (options.SectorsPerCluster != 0)
        {
            return options.SectorsPerCluster * options.BytesPerSector;
        }

        var estimate = options.TotalBytes ?? Math.Max(dataBytes, 1);
        var target = estimate switch
        {
            < 16L * 1024 * 1024 => 512,
            < 64L * 1024 * 1024 => 1024,
            < 128L * 1024 * 1024 => 2048,
            < 256L * 1024 * 1024 => 4096,
            < 512L * 1024 * 1024 => 8192,
            < 2L * 1024 * 1024 * 1024 => 16_384,
            _ => 32_768,
        };
        var sectors = Math.Max(1, target / options.BytesPerSector);
        var sectorsPerCluster = 1;
        while (sectorsPerCluster * 2 <= sectors && sectorsPerCluster * 2 <= 128)
        {
            sectorsPerCluster *= 2;
        }

        return sectorsPerCluster * options.BytesPerSector;
    }

    private static long RequiredClusters(FatNode root, int clusterBytes)
    {
        long total = 0;
        var queue = new Queue<FatNode>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node.IsDirectory)
            {
                if (node.Parent is not null)
                {
                    total += Math.Max(1L, CeilDiv(node.DirectoryBytes, clusterBytes));
                }

                foreach (var child in node.Children)
                {
                    queue.Enqueue(child);
                }
            }
            else
            {
                total += CeilDiv(node.Length, clusterBytes);
            }
        }

        return total;
    }

    private static Layout ResolveLayout(FatBuildOptions options, int clusterBytes, long requiredClusters)
    {
        if (options.Variant != FatVariant.Auto)
        {
            return ComputeLayout(options.Variant, options, clusterBytes, requiredClusters);
        }

        var estimate = requiredClusters;
        if (options.TotalBytes is { } requested)
        {
            var sectorsPerCluster = clusterBytes / options.BytesPerSector;
            estimate = Math.Max(estimate, requested / options.BytesPerSector / sectorsPerCluster);
        }

        var variant = VariantForClusters(estimate);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var layout = ComputeLayout(variant, options, clusterBytes, requiredClusters);
            var expected = VariantForClusters(layout.ClusterCount);
            if (expected == variant)
            {
                return layout;
            }

            variant = expected;
        }

        throw new InvalidDataException(
            "The FAT volume geometry did not converge; pin Variant, SectorsPerCluster or TotalBytes to a consistent combination.");
    }

    private static FatVariant VariantForClusters(long clusters) => clusters switch
    {
        <= Fat12MaximumClusters => FatVariant.Fat12,
        <= Fat16MaximumClusters => FatVariant.Fat16,
        _ => FatVariant.Fat32,
    };

    private static Layout ComputeLayout(FatVariant variant, FatBuildOptions options, int clusterBytes,
        long requiredClusters)
    {
        var bytesPerSector = options.BytesPerSector;
        var sectorsPerCluster = clusterBytes / bytesPerSector;
        var reservedSectors = options.ReservedSectors != 0
            ? options.ReservedSectors
            : variant == FatVariant.Fat32 ? 32 : 1;
        var rootDirectoryEntries = variant == FatVariant.Fat32
            ? 0
            : options.RootDirectoryEntries != 0 ? options.RootDirectoryEntries : 512;
        var rootDirectorySectors = (long)rootDirectoryEntries * DirectoryEntryBytes / bytesPerSector;
        var (floor, ceiling) = variant switch
        {
            FatVariant.Fat12 => (1L, Fat12MaximumClusters),
            FatVariant.Fat16 => (Fat16MinimumClusters, Fat16MaximumClusters),
            _ => (Fat32MinimumClusters, Fat32MaximumClusters),
        };
        var clusters = Math.Max(requiredClusters, floor);
        if (clusters > ceiling)
        {
            throw new InvalidDataException(
                $"The content needs {requiredClusters} clusters with a {clusterBytes}-byte cluster, more than {variant} can address ({ceiling}); choose a wider variant or a larger cluster size.");
        }

        var targetSectors = options.TotalBytes is { } requested
            ? (requested + bytesPerSector - 1) / bytesPerSector
            : 0L;
        if (targetSectors > 0)
        {
            var contentSectorsPerFat = SectorsPerFat(variant, requiredClusters, bytesPerSector);
            var contentOverhead = reservedSectors + (long)FatCount * contentSectorsPerFat + rootDirectorySectors;
            if (contentOverhead + Math.Max(requiredClusters, 1) * sectorsPerCluster > targetSectors)
            {
                throw new InvalidDataException(
                    $"The content needs at least {contentOverhead + Math.Max(requiredClusters, 1) * sectorsPerCluster} sectors but TotalBytes declares {targetSectors}; raise the capacity or the cluster size.");
            }

            for (var attempt = 0; attempt < 64; attempt++)
            {
                var sectorsPerFat = SectorsPerFat(variant, clusters, bytesPerSector);
                var overhead = reservedSectors + (long)FatCount * sectorsPerFat + rootDirectorySectors;
                var fitted = targetSectors > overhead ? (targetSectors - overhead) / sectorsPerCluster : 0;
                var next = Math.Max(Math.Max(requiredClusters, floor), fitted);
                if (next <= clusters)
                {
                    break;
                }

                clusters = next;
            }

            var lowerBound = Math.Max(requiredClusters, floor);
            while (clusters > lowerBound)
            {
                var shrunkSectorsPerFat = SectorsPerFat(variant, clusters, bytesPerSector);
                var shrunkOverhead = reservedSectors + (long)FatCount * shrunkSectorsPerFat + rootDirectorySectors;
                if (shrunkOverhead + clusters * sectorsPerCluster <= targetSectors)
                {
                    break;
                }

                clusters--;
            }
        }

        var finalSectorsPerFat = SectorsPerFat(variant, clusters, bytesPerSector);
        var finalOverhead = reservedSectors + (long)FatCount * finalSectorsPerFat + rootDirectorySectors;
        var totalSectors = finalOverhead + clusters * sectorsPerCluster;
        if (finalSectorsPerFat > ushort.MaxValue && variant != FatVariant.Fat32)
        {
            throw new InvalidDataException(
                $"The {variant} allocation table needs {finalSectorsPerFat} sectors, more than its 16-bit field can address.");
        }

        if (totalSectors > uint.MaxValue)
        {
            throw new InvalidDataException(
                $"The volume needs {totalSectors} sectors, more than the 32-bit total-sector field can address.");
        }

        if ((long)finalSectorsPerFat * bytesPerSector > MaximumFatBytes)
        {
            throw new InvalidDataException(
                $"The {variant} allocation table would occupy {finalSectorsPerFat * bytesPerSector} bytes, above the supported ceiling.");
        }

        var fatStart = (long)reservedSectors * bytesPerSector;
        var rootStart = fatStart + (long)FatCount * finalSectorsPerFat * bytesPerSector;
        var dataStart = rootStart + rootDirectorySectors * bytesPerSector;
        var variantName = variant switch
        {
            FatVariant.Fat12 => "FAT12",
            FatVariant.Fat16 => "FAT16",
            _ => "FAT32",
        };
        return new Layout(variantName, variant, bytesPerSector, sectorsPerCluster, reservedSectors,
            rootDirectoryEntries, finalSectorsPerFat, totalSectors, clusters, fatStart, rootStart, dataStart,
            totalSectors * bytesPerSector, clusterBytes, options.MediaDescriptor, options.HiddenSectors,
            options.VolumeLabel, options.OemName);
    }

    private static int SectorsPerFat(FatVariant variant, long clusters, int bytesPerSector)
    {
        var entries = checked(clusters + 2);
        var fatBytes = variant switch
        {
            FatVariant.Fat12 => (entries * 3 + 1) / 2,
            FatVariant.Fat16 => entries * 2,
            _ => entries * 4,
        };
        return checked((int)((fatBytes + bytesPerSector - 1) / bytesPerSector));
    }

    private static void Allocate(FatNode root, List<FatNode> directories, List<FatNode> files, Layout layout,
        long requiredClusters)
    {
        var next = 2;
        var clusterBytes = (int)layout.ClusterBytes;
        foreach (var directory in directories)
        {
            if (directory == root && layout.Variant != FatVariant.Fat32)
            {
                continue;
            }

            var clusters = Math.Max(1L, CeilDiv(directory.DirectoryBytes, clusterBytes));
            for (var index = 0L; index < clusters; index++)
            {
                directory.Clusters.Add(Take());
            }
        }

        foreach (var file in files)
        {
            var clusters = CeilDiv(file.Length, clusterBytes);
            for (var index = 0L; index < clusters; index++)
            {
                file.Clusters.Add(Take());
            }
        }

        if (next - 2 != requiredClusters && next - 2 > layout.ClusterCount)
        {
            throw new InvalidDataException(
                $"The planned tree needs {next - 2} clusters but the volume declares {layout.ClusterCount}.");
        }

        return;

        int Take()
        {
            var cluster = next++;
            if (cluster - 2 >= layout.ClusterCount)
            {
                throw new InvalidDataException(
                    $"The planned tree needs more clusters than the {layout.VariantName} volume declares ({layout.ClusterCount}); raise TotalBytes or the cluster size.");
            }

            return cluster;
        }
    }

    private static long CeilDiv(long value, long divisor) => value <= 0 ? 0 : (value + divisor - 1) / divisor;
}