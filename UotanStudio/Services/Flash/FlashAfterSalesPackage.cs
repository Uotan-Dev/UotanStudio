using UotanStudio.Comm.Protocols.Edl.FlashBundle;

namespace UotanStudio.Services.Flash;

/// <summary>
/// One partition image found in an extracted after-sales package.
/// <para>已解压售后包中的一个分区镜像。</para>
/// </summary>
/// <param name="Partition">Partition name shown on the row. <para>行内显示的分区名。</para></param>
/// <param name="FilePath">Full path of the image file. <para>镜像文件的完整路径。</para></param>
/// <param name="Length">Image length in bytes. <para>镜像字节数。</para></param>
/// <param name="Program">rawprogram entry that programs this image, or <see langword="null"/> when no manifest lists it.
/// <para>编程该镜像的 rawprogram 条目；没有清单列出该镜像时为 <see langword="null"/>。</para></param>
public sealed record AfterSalesPartitionImage(string Partition, string FilePath, long Length, RawProgramEntry? Program);

/// <summary>
/// Contents of an extracted after-sales package folder.
/// <para>已解压售后包目录的内容。</para>
/// </summary>
/// <param name="ImageDirectory">IMAGES directory holding the images and manifests.
/// <para>存放镜像与清单的 IMAGES 目录。</para></param>
/// <param name="Images">Partition images in display order. <para>按显示顺序排列的分区镜像。</para></param>
/// <param name="Programs">Entries of the primary rawprogram manifests. <para>主 rawprogram 清单的条目。</para></param>
/// <param name="Programmer">The sole Firehose programmer, or <see langword="null"/> when the folder has none or several.
/// <para>唯一的 Firehose programmer；没有或有多个时为 <see langword="null"/>。</para></param>
/// <param name="Programmers">Every Firehose programmer candidate. <para>全部 Firehose programmer 候选。</para></param>
/// <param name="MergedRadioImages">RADIO images whose partition IMAGES already provides.
/// <para>IMAGES 已提供同分区而被合并掉的 RADIO 镜像数。</para></param>
/// <param name="HasMergedSuper">Whether IMAGES already carries a merged <c>super.img</c>.
/// <para>IMAGES 中已存在合并好的 <c>super.img</c> 时为真。</para></param>
/// <param name="HiddenDynamicImages">Scattered dynamic-partition images hidden because the super is already merged.
/// <para>super 已合并而隐藏的散包动态分区镜像数。</para></param>
public sealed record AfterSalesPackageContents(
    string ImageDirectory,
    IReadOnlyList<AfterSalesPartitionImage> Images,
    IReadOnlyList<RawProgramEntry> Programs,
    string? Programmer,
    IReadOnlyList<string> Programmers,
    int MergedRadioImages,
    bool HasMergedSuper,
    int HiddenDynamicImages);

/// <summary>
/// Reads an extracted OPlus/OnePlus after-sales ("service ROM") package: an <c>IMAGES</c> tree of per-partition
/// images plus a <c>RADIO</c> firmware tree, using the same partition naming and exclusion rules the vendor flow
/// applies. Images a primary rawprogram manifest programs keep their EDL entry so the folder stays flashable over
/// EDL, and a RADIO image never overwrites the IMAGES image of the same partition.
/// <para>读取已解压的 OPlus/OnePlus 售后包：<c>IMAGES</c> 分区镜像树加上 <c>RADIO</c> 固件树，分区命名与排除
/// 规则与厂商流程一致。被主 rawprogram 清单编程的镜像保留其 EDL 条目，使目录仍可按 EDL 刷写；
/// 同一分区的 RADIO 镜像不会覆盖 IMAGES 镜像。</para>
/// </summary>
public static class FlashAfterSalesPackage
{
    /// <summary>
    /// Directories whose presence marks a folder as an after-sales package rather than a plain image directory.
    /// <para>标记目录属于售后包而不是普通镜像目录的目录名。</para>
    /// </summary>
    private static readonly string[] MarkerDirectories =
        ["META", "RADIO", "MY_MANIFEST", "OTA", "PRODUCT", "SYSTEM", "VENDOR", "RECOVERY"];

    /// <summary>
    /// Whether a folder has the after-sales package layout.
    /// <para>判断目录是否符合售后包布局。</para>
    /// </summary>
    /// <param name="folder">Directory to inspect. <para>待检查目录。</para></param>
    /// <returns><see langword="true"/> when the folder carries an IMAGES tree plus a package marker directory.
    /// <para>目录带有 IMAGES 树与任一包标记目录时为 <see langword="true"/>。</para></returns>
    public static bool HasStructure(string folder)
    {
        if (!Directory.Exists(folder) || FindDirectory(folder, "IMAGES") is not { } images)
        {
            return false;
        }

        var hasManifest = FlashManifestSelection.Enumerate(images).Length > 0;
        var hasImage = Directory.EnumerateFiles(images, "*", SearchOption.TopDirectoryOnly)
            .Any(path => TryImageStem(Path.GetFileName(path), out _));
        if (!hasManifest && !hasImage)
        {
            return false;
        }

        return MarkerDirectories.Any(marker => FindDirectory(folder, marker) is not null);
    }

    /// <summary>
    /// Reads an after-sales package folder, preferring manifest labels and IMAGES images over RADIO duplicates.
    /// <para>读取售后包目录；分区命名优先取清单标签，同一分区优先取 IMAGES 镜像。</para>
    /// </summary>
    /// <param name="folder">Package root or any directory above the IMAGES tree.
    /// <para>包根目录或 IMAGES 树之上的任意目录。</para></param>
    /// <returns>The package contents, or <see langword="null"/> when the folder does not have the layout.
    /// <para>包内容；目录不符合该布局时为 <see langword="null"/>。</para></returns>
    public static AfterSalesPackageContents? Read(string folder)
    {
        if (!HasStructure(folder) || FindDirectory(folder, "IMAGES") is not { } imageDirectory)
        {
            return null;
        }

        var radioDirectory = FindDirectory(folder, "RADIO");
        var manifests = FlashManifestSelection.Enumerate(imageDirectory);
        var programs = manifests.Length == 0
            ? []
            : RawProgramParser.ParseAll(manifests, imageDirectory);
        var programsByPath = new Dictionary<string, RawProgramEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var program in programs)
        {
            if (program.Filename.Length > 0 && program.SourceFile.Length > 0)
            {
                programsByPath.TryAdd(Path.GetFullPath(program.SourceFile), program);
            }
        }

        var images = new List<AfterSalesPartitionImage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dynamicPartitions = new List<string>();
        var mergedSuper = File.Exists(Path.Combine(imageDirectory, "super.img"));

        foreach (var directory in Directory.EnumerateDirectories(imageDirectory).Order(StringComparer.OrdinalIgnoreCase))
        {
            var directoryName = Path.GetFileName(directory);
            if (directoryName.StartsWith('.'))
            {
                continue;
            }

            var path = EnumerateImages(directory).FirstOrDefault();
            if (path is null)
            {
                continue;
            }

            var partition = DynamicPartitionName(directoryName, Path.GetFileName(path));
            if (partition is null || !seen.Add(partition))
            {
                continue;
            }

            images.Add(new AfterSalesPartitionImage(partition, path, new FileInfo(path).Length,
                programsByPath.GetValueOrDefault(path)));
            dynamicPartitions.Add(partition);
        }

        AddFolder(imageDirectory, fromImages: true);
        var mergedRadioImages = radioDirectory is { } radio ? AddFolder(radio, fromImages: false) : 0;

        foreach (var (path, program) in programsByPath)
        {
            var partition = PartitionName(program.Label, program.Filename);
            if (partition is null || !seen.Add(partition))
            {
                continue;
            }

            images.Add(new AfterSalesPartitionImage(partition, path, new FileInfo(path).Length, program));
        }

        var hiddenDynamicImages = 0;
        if (mergedSuper)
        {
            hiddenDynamicImages = images.RemoveAll(image =>
                dynamicPartitions.Contains(image.Partition, StringComparer.OrdinalIgnoreCase) ||
                image.Partition.StartsWith("my", StringComparison.OrdinalIgnoreCase));
        }

        var programmers = EnumerateProgrammers(imageDirectory);
        return new AfterSalesPackageContents(imageDirectory, images, programs,
            programmers.Length == 1 ? programmers[0] : null, programmers, mergedRadioImages,
            mergedSuper, hiddenDynamicImages);

        int AddFolder(string directory, bool fromImages)
        {
            var merged = 0;
            foreach (var path in EnumerateImages(directory))
            {
                var partition = PartitionName(null, Path.GetFileName(path));
                if (partition is null)
                {
                    continue;
                }

                if (fromImages && dynamicPartitions.Any(name =>
                        partition.StartsWith(name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (!seen.Add(partition))
                {
                    if (!fromImages)
                    {
                        merged++;
                    }

                    continue;
                }

                Add(partition, path);
            }

            return merged;
        }

        void Add(string partition, string path)
        {
            images.Add(new AfterSalesPartitionImage(partition, path, new FileInfo(path).Length,
                programsByPath.GetValueOrDefault(path)));
        }
    }

    /// <summary>
    /// Returns the partition name a row displays: the manifest label when it is usable, otherwise the lower-cased
    /// file name with a trailing numeric version segment removed.
    /// <para>返回行内显示的分区名：清单标签可用时用它，否则用去掉尾部数字版本段的小写文件名。</para>
    /// </summary>
    /// <param name="label">Manifest label, or <see langword="null"/>. <para>清单标签，可为 <see langword="null"/>。</para></param>
    /// <param name="fileName">Image file name. <para>镜像文件名。</para></param>
    /// <returns>The partition name, or <see langword="null"/> when neither source yields a usable name.
    /// <para>分区名；两个来源都给不出可用名时为 <see langword="null"/>。</para></returns>
    internal static string? PartitionName(string? label, string fileName)
    {
        if (label is not null && Acceptable(label.Trim()))
        {
            return label.Trim();
        }

        if (!TryImageStem(fileName, out var stem))
        {
            return null;
        }

        var dot = stem.LastIndexOf('.');
        if (dot > 0 && stem.Length > dot + 1 && stem[(dot + 1)..].All(char.IsAsciiDigit))
        {
            stem = stem[..dot];
        }

        var name = stem.ToLowerInvariant();
        return Acceptable(name) ? name : null;
    }

    /// <summary>
    /// Returns the partition name of an image inside a per-partition subdirectory
    /// (<c>my_bigball/my_bigball.27310181.img</c> becomes <c>my_bigball</c>).
    /// <para>返回按分区分子目录中镜像的分区名（<c>my_bigball/my_bigball.27310181.img</c> 得到 <c>my_bigball</c>）。</para>
    /// </summary>
    /// <param name="directoryName">Name of the subdirectory. <para>子目录名。</para></param>
    /// <param name="fileName">Image file name. <para>镜像文件名。</para></param>
    /// <returns>The partition name, or <see langword="null"/> when the file is not a valid partition image.
    /// <para>分区名；文件不是有效分区镜像时为 <see langword="null"/>。</para></returns>
    internal static string? DynamicPartitionName(string directoryName, string fileName)
    {
        if (Acceptable(directoryName) &&
            fileName.StartsWith(directoryName + ".", StringComparison.OrdinalIgnoreCase))
        {
            return directoryName.ToLowerInvariant();
        }

        return PartitionName(null, fileName);
    }

    private static bool Acceptable(string name)
        => name.Length is >= 1 and <= 64
           && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
           && !name.StartsWith("gpt_", StringComparison.OrdinalIgnoreCase)
           && !name.StartsWith("ALIGN_TO_", StringComparison.OrdinalIgnoreCase)
           && !name.Equals("super_meta", StringComparison.OrdinalIgnoreCase)
           && !name.Equals("userdata", StringComparison.OrdinalIgnoreCase)
           && !name.Equals("metadata", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Splits an image file name into its stem, accepting <c>.img</c>, <c>.img.zst</c> and <c>.iso</c>.
    /// <para>拆出镜像文件名的主干，接受 <c>.img</c>、<c>.img.zst</c> 与 <c>.iso</c>。</para>
    /// </summary>
    /// <param name="fileName">Image file name. <para>镜像文件名。</para></param>
    /// <param name="stem">Name without the image extension. <para>去掉镜像扩展名后的名字。</para></param>
    /// <returns><see langword="true"/> when the file name carries an image extension.
    /// <para>文件名带镜像扩展名时为 <see langword="true"/>。</para></returns>
    private static bool TryImageStem(string fileName, out string stem)
    {
        foreach (var extension in new[] { ".img.zst", ".img", ".iso" })
        {
            if (fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                stem = fileName[..^extension.Length];
                return stem.Length > 0;
            }
        }

        stem = string.Empty;
        return false;
    }

    private static IEnumerable<string> EnumerateImages(string directory)
        => Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => TryImageStem(Path.GetFileName(path), out _))
            .Select(Path.GetFullPath)
            .Order(StringComparer.OrdinalIgnoreCase);

    private static string[] EnumerateProgrammers(string directory)
        => [.. Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetFileName(path) is var name &&
                name.StartsWith("prog_", StringComparison.OrdinalIgnoreCase) &&
                name.Contains("firehose", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".elf", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)];

    private static string? FindDirectory(string folder, string name)
    {
        foreach (var path in Directory.EnumerateDirectories(folder))
        {
            if (string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        return null;
    }
}