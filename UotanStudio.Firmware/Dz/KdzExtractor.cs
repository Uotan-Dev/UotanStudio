using System.Text;

namespace UotanStudio.Firmware.Dz;

/// <summary>
/// <para>表示 LG KDZ 文件中的一个分区。</para>
/// Represents a partition within an LG KDZ file.
/// </summary>
public sealed class KdzPartition
{
    /// <summary>分区名称。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>数据长度。</summary>
    public ulong Length { get; init; }

    /// <summary>数据偏移量。</summary>
    public ulong Offset { get; init; }
}

/// <summary>
/// <para>提供 LG KDZ 固件文件的解析与提取功能。</para>
/// Provides parsing and extraction functionality for LG KDZ firmware files.
/// </summary>
public static class KdzExtractor
{
    /// <summary>
    /// <para>解析 KDZ 文件,返回头部类型与分区列表。</para>
    /// Parses a KDZ file, returning the header type and partition list.
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
                break;
            }

            var header = KdzHeader.FromBytes(buf);
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
    /// <para>将 KDZ 文件中的所有分区提取到输出目录。</para>
    /// Extracts all partitions from a KDZ file to the output directory.
    /// </summary>
    public static int Extract(string kdzPath, string outDir, Func<string, bool>? nameFilter = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(kdzPath);
        ArgumentException.ThrowIfNullOrEmpty(outDir);

        Directory.CreateDirectory(outDir);

        var (_, partitions) = Parse(kdzPath);
        var count = 0;

        using var fs = new FileStream(kdzPath, FileMode.Open, FileAccess.Read);
        foreach (var part in partitions)
        {
            if (nameFilter is not null && !nameFilter(part.Name))
            {
                continue;
            }

            var outPath = Path.Combine(outDir, part.Name);
            fs.Position = (long)part.Offset;
            using var outFs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
            var remaining = (long)part.Length;
            var buffer = new byte[1024 * 1024];
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(buffer.Length, remaining);
                var read = fs.Read(buffer, 0, toRead);
                if (read == 0)
                {
                    break;
                }
                outFs.Write(buffer, 0, read);
                remaining -= read;
            }
            count++;
        }

        return count;
    }

    /// <summary>
    /// <para>获取 KDZ 分区头的分区名称。</para>
    /// Gets the partition name of a KDZ partition header.
    /// </summary>
    public static string GetName(byte[] nameBytes)
        => Encoding.UTF8.GetString(nameBytes).TrimEnd('\0');
}