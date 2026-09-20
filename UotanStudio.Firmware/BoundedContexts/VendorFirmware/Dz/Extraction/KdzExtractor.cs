using System.Text;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Dz.Models;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Dz.Extraction;

public sealed class KdzPartition
{
    public string Name { get; init; } = string.Empty;

    public ulong Length { get; init; }

    public ulong Offset { get; init; }
}

public static class KdzExtractor
{
    /// <summary>
    /// Parses a KDZ file, returning the header type and partition list.
    /// <para>解析 KDZ 文件,返回头部类型与分区列表。</para>
    /// </summary>
    public static (int HeaderType, List<KdzPartition> Partitions) Parse(string kdzPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(kdzPath);

        using var fs = new FileStream(kdzPath, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);

        var magic = br.ReadBytes(8);
        var headerType = magic.SequenceEqual(DzConstants.KdzHeaderV0) ? 0
            : magic.SequenceEqual(DzConstants.KdzHeaderV1) ? 1
            : magic.SequenceEqual(DzConstants.KdzHeaderV2) ? 2
            : throw new InvalidDataException("Unsupported KDZ file format.");

        var partitions = new List<KdzPartition>();
        fs.Position = 8;
        while (true)
        {
            var buf = br.ReadBytes(KdzHeader.Size);
            if (buf.Length < KdzHeader.Size)
            {
                throw new InvalidDataException("The KDZ partition table is truncated.");
            }

            var header = KdzHeader.FromBytes(buf);
            if (header.Offset > (ulong)fs.Length || header.Length > (ulong)fs.Length - header.Offset)
                throw new InvalidDataException("A KDZ partition exceeds the input bounds.");
            partitions.Add(new KdzPartition
            {
                Name = header.GetName(),
                Length = header.Length,
                Offset = header.Offset
            });

            var next = br.ReadByte();
            if (next is 0x03 or 0x00)
            {
                break;
            }

            fs.Seek(-1, SeekOrigin.Current);
        }

        return (headerType, partitions);
    }

    /// <summary>
    /// Extracts all partitions from a KDZ file to the output directory.
    /// <para>将 KDZ 文件中的所有分区提取到输出目录。</para>
    /// </summary>
    /// <param name="kdzPath">The KDZ source path. <para>KDZ 源路径。</para></param>
    /// <param name="outDir">The output directory. <para>输出目录。</para></param>
    /// <param name="nameFilter">The optional entry filter. <para>可选的条目名筛选器。</para></param>
    /// <param name="overwrite">Whether to replace existing files. <para>是否替换已有文件。</para></param>
    /// <param name="cancellationToken">The cancellation token. <para>取消令牌。</para></param>
    /// <returns>The committed entry count. <para>已提交的条目数。</para></returns>
    public static int Extract(string kdzPath, string outDir, Func<string, bool>? nameFilter = null,
        bool overwrite = true, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(kdzPath);
        ArgumentException.ThrowIfNullOrEmpty(outDir);
        cancellationToken.ThrowIfCancellationRequested();

        var (_, partitions) = Parse(kdzPath);
        var count = 0;

        using var fs = new FileStream(kdzPath, FileMode.Open, FileAccess.Read);
        foreach (var part in partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (nameFilter is not null && !nameFilter(part.Name))
            {
                continue;
            }

            var outPath = FileExtraction.ResolvePath(outDir, part.Name);
            fs.Position = (long)part.Offset;
            FileExtraction.WriteAtomic(outPath, output => StreamCopy.CopyExactly(fs, output, checked((long)part.Length),
                cancellationToken: cancellationToken), overwrite);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Gets the partition name of a KDZ partition header.
    /// <para>获取 KDZ 分区头的分区名称。</para>
    /// </summary>
    public static string GetName(byte[] nameBytes)
        => Encoding.UTF8.GetString(nameBytes).TrimEnd('\0');
}