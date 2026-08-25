namespace UotanStudio.Firmware.Dat;

/// <summary>
/// Writes an Android OTA v4 "new"-style DAT image pair: a <c>.new.dat</c> data file plus a
/// <c>.transfer.list</c> describing the block ranges, from a raw image file.
/// <para>从原始镜像文件写出 Android OTA v4 "new" 型 DAT 镜像对：<c>.new.dat</c> 数据文件
/// 与描述块范围的 <c>.transfer.list</c>。</para>
/// </summary>
public static class DatWriter
{
    /// <summary>
    /// Block size in bytes (4096).
    /// <para>块大小（字节，4096）。</para>
    /// </summary>
    public const int BlockSize = 4096;

    /// <summary>
    /// Writes a raw image as a DAT image pair. The <c>.new.dat</c> file concatenates the whole image
    /// byte-by-byte (the final block is written at its actual length when shorter than
    /// <see cref="BlockSize"/>); the <c>.transfer.list</c> contains version "4", the "new" command,
    /// one range group, and the range "0 &lt;totalBlocks&gt;".
    /// <para>将原始镜像写出为 DAT 镜像对。<c>.new.dat</c> 按字节序拼接整个镜像（末块不足
    /// <see cref="BlockSize"/> 时按实际长度写入）；<c>.transfer.list</c> 包含版本 "4"、命令 "new"、
    /// 一组块范围组与范围 "0 &lt;totalBlocks&gt;"。</para>
    /// </summary>
    /// <param name="inputImagePath">The input raw image file path. <para>输入原始镜像文件路径。</para></param>
    /// <param name="outputDatPath">The output <c>.new.dat</c> file path. <para>输出 <c>.new.dat</c> 文件路径。</para></param>
    /// <param name="outputTransferListPath">The output <c>.transfer.list</c> file path. <para>输出 <c>.transfer.list</c> 文件路径。</para>
    public static void Write(string inputImagePath, string outputDatPath, string outputTransferListPath)
    {
        var totalBytes = new FileInfo(inputImagePath).Length;
        var totalBlocks = (totalBytes + BlockSize - 1) / BlockSize;

        using (var input = File.OpenRead(inputImagePath))
        using (var output = File.Create(outputDatPath))
        {
            input.CopyTo(output);
        }

        // Android v4 transfer list "new" 子集：版本 4、命令 new、1 组范围、范围 "0 <totalBlocks>"。
        // Android v4 transfer list "new" subset: version 4, "new" command, one range group, range "0 <totalBlocks>".
        File.WriteAllText(outputTransferListPath, $"4\nnew\n1\n0 {totalBlocks}\n");
    }
}