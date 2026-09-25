using System.Text;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Dz.Models;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Dz.Extraction;

/// <summary>One partition entry read from a KDZ partition-table record.
/// <para>从 KDZ 分区表记录中读出的一条分区条目。</para></summary>
public sealed class KdzPartition
{
    /// <summary>The partition name decoded from the record's 256-byte name field.
    /// <para>从记录的 256 字节名称字段解码出的分区名。</para></summary>
    /// <value>UTF-8 text with trailing NUL characters trimmed; <see cref="string.Empty"/> when the field is all NUL.
    /// <para>按 UTF-8 解码并去除结尾 NUL 的文本；字段全为 NUL 时为空字符串。</para></value>
    public string Name { get; init; } = string.Empty;

    /// <summary>The declared partition payload length.
    /// <para>声明的分区负载长度。</para></summary>
    /// <value>Byte count validated against the input file bounds during parsing.
    /// <para>字节数；解析期间已依据输入文件边界校验。</para></value>
    public ulong Length { get; init; }

    /// <summary>The position of the partition payload inside the KDZ file.
    /// <para>分区负载在 KDZ 文件中的位置。</para></summary>
    /// <value>Zero-based byte offset validated against the input file bounds during parsing.
    /// <para>从零开始的字节偏移；解析期间已依据输入文件边界校验。</para></value>
    public ulong Offset { get; init; }
}

/// <summary>Parses and extracts LG KDZ firmware containers, treating every byte of the input as untrusted.
/// <para>解析并提取 LG KDZ 固件容器，输入的每一字节都视为不可信。</para></summary>
public static class KdzExtractor
{
    /// <summary>
    /// Parses a KDZ file, returning the header type and partition list.
    /// <para>解析 KDZ 文件,返回头部类型与分区列表。</para>
    /// </summary>
    /// <param name="kdzPath">
    /// Path of an existing KDZ file; must not be null or empty.
    /// <para>已存在的 KDZ 文件路径；不能为 <see langword="null"/> 或空字符串。</para>
    /// </param>
    /// <returns>
    /// The header generation (<c>0</c>, <c>1</c> or <c>2</c>) chosen by the 8-byte magic, and the partition
    /// entries decoded from the 272-byte records up to the <c>0x03</c>/<c>0x00</c> terminator.
    /// <para>由 8 字节魔数决定的头部版本（<c>0</c>、<c>1</c> 或 <c>2</c>），以及从 272 字节记录解码、
    /// 直到 <c>0x03</c>/<c>0x00</c> 终止符为止的分区条目。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="kdzPath"/> is null or empty.
    /// <para><paramref name="kdzPath"/> 为 <see langword="null"/> 或空字符串。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The magic does not match a known KDZ header, a partition record is truncated, or a partition exceeds
    /// the input bounds.
    /// <para>魔数与已知 KDZ 头部不符、分区记录被截断，或分区超出输入文件边界。</para>
    /// </exception>
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
    /// <param name="nameBytes">The raw name-field bytes of a KDZ partition record. <para>KDZ 分区记录的原始名称字段字节。</para></param>
    /// <returns>The UTF-8 decoded name with trailing NUL characters trimmed.
    /// <para>按 UTF-8 解码并去除结尾 NUL 的名称。</para></returns>
    public static string GetName(byte[] nameBytes)
        => Encoding.UTF8.GetString(nameBytes).TrimEnd('\0');
}