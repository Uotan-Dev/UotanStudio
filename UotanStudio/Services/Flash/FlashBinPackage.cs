using UotanStudio.Comm.Protocols.Edl.FlashBundle;

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
/// references never becoming rows. Programmer candidates are the <c>prog_*firehose*</c> ELF/MBN/MELF files.
/// <para>按主 rawprogram 清单读取扁平高通镜像目录：每个镜像存在于磁盘上的 program 条目一行；GPT 与对齐
/// 占位文件、清单未引用的文件都不成行。programmer 候选为 <c>prog_*firehose*</c> 的 ELF/MBN/MELF 文件。</para>
/// </summary>
public static class FlashBinPackage
{
    /// <summary>
    /// Returns the manifest-driven image rows, the parsed program entries and the Firehose programmer candidates.
    /// <para>返回清单驱动的镜像行、解析出的 program 条目与 Firehose programmer 候选。</para>
    /// </summary>
    /// <param name="folder">Directory holding the images and rawprogram manifests; an <c>images</c> subdirectory
    /// is preferred when present.<para>存放镜像与 rawprogram 清单的目录；存在 <c>images</c> 子目录时优先使用。</para></param>
    /// <returns>The image rows, the program entries, the sole programmer candidate and all candidates.
    /// <para>镜像行、program 条目、唯一的 programmer 候选以及全部候选。</para></returns>
    public static (IReadOnlyList<FlashBinPackageImage> Images,
        IReadOnlyList<RawProgramEntry> Programs, string? Programmer, IReadOnlyList<string> Programmers) Read(string folder)
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

        string[] programmers = [.. Directory.EnumerateFiles(directory, "prog_*firehose*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path) is ".elf" or ".mbn" or ".melf")
            .Order(StringComparer.OrdinalIgnoreCase)];
        return (images, programs, programmers.Length == 1 ? programmers[0] : null, programmers);
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