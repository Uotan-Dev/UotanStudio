using UotanStudio.Comm.Protocols.Edl.FlashBundle;
using UotanStudio.Comm.Protocols.Edl.Programmer;

namespace UotanStudio.Services.Flash;

/// <summary>
/// One rawprogram-driven image row of a flat Qualcomm image directory.
/// <para>扁平高通镜像目录中由 rawprogram 清单驱动的单个镜像行。</para>
/// </summary>
/// <param name="Partition">Partition name: the manifest label, or the image file stem when the label is empty.
/// <para>分区名：清单标签；标签为空时用镜像文件主干名。</para></param>
/// <param name="FilePath">Full path of the image file. <para>镜像文件的完整路径。</para></param>
/// <param name="Length">Image length in bytes. <para>镜像字节数。</para></param>
/// <param name="Program">The rawprogram entry that programs this image.
/// <para>编程该镜像的 rawprogram 条目。</para></param>
public sealed record FlashBinPackageImage(string Partition, string FilePath, long Length, RawProgramEntry Program);

/// <summary>
/// Reads a flat Qualcomm image directory the way its primary rawprogram manifests describe it: one row per
/// program entry whose image exists on disk, with GPT and alignment placeholders and files no manifest
/// references never becoming rows. Programmer candidates are a validated multi-image programmer manifest when
/// the directory carries one, otherwise the <c>prog_*firehose*</c> ELF/MBN/MELF files.
/// <para>按主 rawprogram 清单读取扁平高通镜像目录：每个镜像存在于磁盘上的 program 条目一行；GPT 与对齐
/// 占位文件、清单未引用的文件都不成行。programmer 候选在目录带有通过校验的多镜像 programmer 清单时取该清单，
/// 否则取 <c>prog_*firehose*</c> 的 ELF/MBN/MELF 文件。</para>
/// </summary>
public static class FlashBinPackage
{
    /// <summary>
    /// Returns the manifest-driven image rows, the parsed program entries and the Firehose programmer candidates.
    /// <para>返回清单驱动的镜像行、解析出的 program 条目与 Firehose programmer 候选。</para>
    /// </summary>
    /// <param name="folder">Directory holding the images and rawprogram manifests; an <c>images</c> subdirectory
    /// is preferred when present.<para>存放镜像与 rawprogram 清单的目录；存在 <c>images</c> 子目录时优先使用。</para></param>
    /// <returns>The image rows, the program entries, the sole programmer candidate, all candidates and the
    /// inferred multi-image mapping when the folder carries no manifest but the well-known scatter files.
    /// <para>镜像行、program 条目、唯一的 programmer 候选、全部候选，以及目录没有清单却带有知名散件时的
    /// 多镜像推断映射。</para></returns>
    public static (IReadOnlyList<FlashBinPackageImage> Images,
        IReadOnlyList<RawProgramEntry> Programs, string? Programmer, IReadOnlyList<string> Programmers,
        EdlInferredProgrammerManifest? InferredProgrammer) Read(string folder)
    {
        var directory = ResolveImageDirectory(folder);
        var manifests = FlashManifestSelection.Enumerate(directory);
        var programs = manifests.Length == 0 ? [] : RawProgramParser.ParseAll(manifests, directory);

        var images = new List<FlashBinPackageImage>();
        var seen = new HashSet<(string Partition, int Lun, string Path)>();
        foreach (var program in programs)
        {
            if (program.Filename.Length == 0 || program.SourceFile.Length == 0 ||
                program.Filename.StartsWith("gpt_", StringComparison.OrdinalIgnoreCase) ||
                program.Filename.StartsWith("ALIGN_TO_", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(program.SourceFile))
            {
                continue;
            }

            var label = program.Label.Trim();
            var partition = label.Length > 0 ? label : Path.GetFileNameWithoutExtension(program.SourceFile);
            if (partition.Length == 0 ||
                !seen.Add((partition, program.PhysicalPartitionNumber, Path.GetFullPath(program.SourceFile))))
            {
                continue;
            }

            images.Add(new FlashBinPackageImage(partition, program.SourceFile,
                new FileInfo(program.SourceFile).Length, program));
        }

        string[] programmers = EnumerateProgrammers(directory);
        var inferred = IsProgrammerManifest(programmers) ? null : EdlManifestInference.TryInfer(directory);
        return (images, programs, programmers.Length == 1 ? programmers[0] : null, programmers, inferred);
    }

    /// <summary>
    /// Reports whether a programmer candidate path is a multi-image manifest rather than a bare image.
    /// <para>报告 programmer 候选路径是多镜像清单而不是散件镜像。</para>
    /// </summary>
    /// <param name="programmer">The candidate path. <para>候选路径。</para></param>
    /// <returns><see langword="true"/> for a <c>.xml</c> manifest path.
    /// <para><c>.xml</c> 清单路径时为 <see langword="true"/>。</para></returns>
    internal static bool IsProgrammerManifestPath(string? programmer)
        => SaharaProgrammerManifest.IsManifestPath(programmer);

    /// <summary>
    /// Reports whether a programmer candidate list is led by a multi-image manifest rather than bare images.
    /// <para>报告 programmer 候选列表是否由多镜像清单而不是散件镜像领衔。</para>
    /// </summary>
    /// <param name="programmers">The enumerated candidates. <para>已列举的候选。</para></param>
    /// <returns><see langword="true"/> when the first candidate is a <c>.xml</c> manifest.
    /// <para>首个候选是 <c>.xml</c> 清单时为 <see langword="true"/>。</para></returns>
    private static bool IsProgrammerManifest(IReadOnlyList<string> programmers)
        => programmers.Count > 0 && IsProgrammerManifestPath(programmers[0]);

    /// <summary>
    /// Gets the largest number of top-level <c>.xml</c> files one directory scan admits as manifest candidates;
    /// the excess is ignored so an untrusted directory cannot inflate the read.
    /// <para>单次目录扫描接纳为清单候选的顶层 <c>.xml</c> 文件数量上限；超出部分被忽略，使不可信目录无法
    /// 拖垮读取。</para>
    /// </summary>
    private const int MaximumManifestCandidates = 16;

    /// <summary>
    /// Enumerates the Firehose programmer candidates of an image directory: a validated Qualcomm multi-image
    /// programmer manifest stands for the whole multi-image package and replaces the individual images, and only
    /// when no manifest validates does the <c>prog_*firehose*</c> ELF/MBN/MELF scan supply the candidates.
    /// <para>列举镜像目录中的 Firehose programmer 候选：通过校验的高通多镜像 programmer 清单代表整个多镜像包，
    /// 取代各个散件；只有没有任何清单通过校验时，才由 <c>prog_*firehose*</c> 的 ELF/MBN/MELF 扫描提供候选。</para>
    /// </summary>
    /// <param name="directory">Directory holding the programmer files. <para>存放 programmer 文件的目录。</para></param>
    /// <returns>The candidates in ordinal name order. <para>按名称顺序排列的候选。</para></returns>
    internal static string[] EnumerateProgrammers(string directory)
    {
        var manifests = EnumerateProgrammerManifests(directory);
        if (manifests.Length > 0)
        {
            return manifests;
        }

        return [.. Directory.EnumerateFiles(directory, "prog_*firehose*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path) is ".elf" or ".mbn" or ".melf")
            .Select(Path.GetFullPath)
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Returns the top-level <c>.xml</c> files that parse as bounded Qualcomm multi-image programmer manifests,
    /// rejecting every other XML (including rawprogram manifests) without letting one bad file abort the scan.
    /// <para>返回能按有界高通多镜像 programmer 清单解析的顶层 <c>.xml</c> 文件，其余 XML（包括 rawprogram 清单）
    /// 一律拒绝，且单个坏文件不会中断扫描。</para>
    /// </summary>
    /// <param name="directory">Directory to scan. <para>要扫描的目录。</para></param>
    /// <returns>The validated manifest paths in ordinal name order.
    /// <para>通过校验的清单路径，按名称顺序排列。</para></returns>
    private static string[] EnumerateProgrammerManifests(string directory)
    {
        var manifests = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.xml", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            if (manifests.Count >= MaximumManifestCandidates)
            {
                break;
            }

            long length;
            try
            {
                length = new FileInfo(path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (length is 0 or > SaharaProgrammerManifest.MaximumManifestBytes)
            {
                continue;
            }

            try
            {
                SaharaProgrammerManifest.Read(path);
                manifests.Add(path);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                or ArgumentException or System.Xml.XmlException)
            {
            }
        }

        return [.. manifests];
    }

    /// <summary>
    /// Returns the directory the package reads its images from: the <c>images</c> subdirectory when it carries a
    /// primary rawprogram manifest, otherwise the folder itself, so an unrelated or empty <c>images</c> directory
    /// never hides manifests that live next to it.
    /// <para>返回包读取镜像所用目录：<c>images</c> 子目录带有主 rawprogram 清单时用它，否则用目录本身，
    /// 使无关或空的 <c>images</c> 目录不会把与其同级的清单隐藏起来。</para>
    /// </summary>
    /// <param name="folder">Package folder. <para>包目录。</para></param>
    /// <returns>The directory holding images and manifests. <para>存放镜像与清单的目录。</para></returns>
    public static string ResolveImageDirectory(string folder)
    {
        var images = Path.Combine(folder, "images");
        return Directory.Exists(images) && FlashManifestSelection.Enumerate(images).Length > 0 ? images : folder;
    }
}