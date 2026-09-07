using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Rockchip;

/// <summary>Reads and extracts RKFW and RKAF containers.<para>读取并提取 RKFW 和 RKAF 容器。</para></summary>
public static class RockchipExtractor
{
    /// <summary>Lists RKFW loader/image entries or RKAF package files.<para>列出 RKFW 加载器和镜像，或 RKAF 包文件。</para></summary>
    public static List<RkfwEntry> List(string imagePath)
    {
        using var stream = File.OpenRead(imagePath);
        return ReadEntries(stream);
    }

    /// <summary>Reads container entry metadata.<para>读取容器条目元数据。</para></summary>
    public static List<RkfwEntry> ReadMetadata(string imagePath) => List(imagePath);

    /// <summary>Extracts validated entries with atomic file replacement.<para>校验条目后以原子文件替换方式提取。</para></summary>
    /// <param name="imagePath">The source image path. 源镜像路径。</param>
    /// <param name="outputDirectory">The output directory. 输出目录。</param>
    /// <param name="progress">The optional progress sink. 可选进度接收器。</param>
    /// <param name="overwrite">Whether existing files may be replaced. 是否允许替换现有文件。</param>
    /// <param name="cancellationToken">The cancellation token. 取消令牌。</param>
    public static int Extract(string imagePath, string outputDirectory, IProgress<double>? progress = null,
        bool overwrite = true, CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(imagePath);
        var entries = ReadEntries(stream);
        var paths = entries.Select(e => FileExtraction.ResolvePath(outputDirectory, e.Name)).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new InvalidDataException("Duplicate Rockchip output paths.");
        var total = entries.Sum(e => e.Size);
        long copied = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[i];
            FileExtraction.WriteAtomic(paths[i], output =>
            {
                stream.Position = entry.Offset;
                StreamCopy.CopyExactly(stream, output, entry.Size, cancellationToken: cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }, overwrite);
            copied += entry.Size;
            progress?.Report(total == 0 ? 1 : (double)copied / total);
        }
        return entries.Count;
    }

    private static List<RkfwEntry> ReadEntries(FileStream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var tag = reader.ReadUInt32();
        stream.Position = 0;
        return tag switch
        {
            RkFwHeader.MagicRkfw => ReadRkfw(reader),
            RkFwHeader.MagicRkaf => ReadRkaf(reader),
            _ => throw new InvalidDataException("Invalid Rockchip container magic.")
        };
    }

    private static List<RkfwEntry> ReadRkfw(BinaryReader reader)
    {
        var stream = reader.BaseStream;
        var header = RkFwHeader.Read(reader);
        if (header.HeaderSize < RkFwHeader.StructSize || header.HeaderSize > stream.Length)
            throw new InvalidDataException("Invalid RKFW header length.");
        CheckRegion(header.BootOffset, header.BootSize, header.HeaderSize, stream.Length);
        CheckRegion(header.ImageOffset, header.ImageSize, header.HeaderSize, stream.Length);
        if (header.BootSize < 4 || header.ImageSize < 140 ||
            (header.BootOffset < (long)header.ImageOffset + header.ImageSize &&
             header.ImageOffset < (long)header.BootOffset + header.BootSize))
            throw new InvalidDataException("Invalid or overlapping RKFW payload regions.");
        stream.Position = header.BootOffset;
        var loaderTag = reader.ReadUInt32();
        var loaderName = loaderTag switch
        {
            0x544F4F42 => "BOOT",
            0x2052444C => "LDR",
            _ => throw new InvalidDataException("Invalid RKFW loader magic.")
        };
        stream.Position = header.ImageOffset;
        if (reader.ReadUInt32() != RkFwHeader.MagicRkaf)
            throw new InvalidDataException("RKFW does not contain an RKAF image at its declared offset.");
        return [new(loaderName, header.BootOffset, header.BootSize, RkfwEntryType.Boot),
            new("embedded-update.img", header.ImageOffset, header.ImageSize, RkfwEntryType.Image)];
    }

    private static List<RkfwEntry> ReadRkaf(BinaryReader reader)
    {
        var stream = reader.BaseStream;
        Span<byte> header = stackalloc byte[140];
        stream.ReadExactly(header);
        var declaredLength = (long)BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) + 4;
        FileExtraction.ValidateRange(0, declaredLength, stream.Length, "RKAF image");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header[136..]);
        var tableEnd = checked(140L + count * 112L);
        FileExtraction.ValidateRange(140, count * 112L, declaredLength, "RKAF entry table");
        var entries = new List<RkfwEntry>();
        Span<byte> descriptor = stackalloc byte[112];
        Span<byte> parameterHeader = stackalloc byte[8];
        for (uint i = 0; i < count; i++)
        {
            stream.Position = 140L + i * 112L;
            stream.ReadExactly(descriptor);
            var name = ReadString(descriptor[..32]);
            var path = ReadString(descriptor.Slice(32, 64));
            if (path == "SELF") continue;
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[96..]);
            var allocated = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[104..]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[108..]);
            if (size > allocated) throw new InvalidDataException("RKAF entry exceeds its allocated length.");
            CheckRegion(offset, allocated, tableEnd, declaredLength);
            if (name == "parameter")
            {
                if (size < 12) throw new InvalidDataException("Truncated RKAF parameter wrapper.");
                stream.Position = offset;
                stream.ReadExactly(parameterHeader);
                if (!parameterHeader[..4].SequenceEqual("PARM"u8) ||
                    BinaryPrimitives.ReadUInt32LittleEndian(parameterHeader[4..]) != size - 12)
                    throw new InvalidDataException("Invalid RKAF parameter wrapper.");
                offset += 8;
                size -= 12;
            }
            entries.Add(new(path, offset, size, RkfwEntryType.Partition));
        }
        return entries;
    }

    private static string ReadString(ReadOnlySpan<byte> value)
    {
        var nul = value.IndexOf((byte)0);
        return Encoding.UTF8.GetString(nul < 0 ? value : value[..nul]);
    }

    private static void CheckRegion(long offset, long size, long headerSize, long length)
    {
        if (offset < headerSize) throw new InvalidDataException("Rockchip payload overlaps its header or table.");
        FileExtraction.ValidateRange(offset, size, length, "Rockchip payload");
    }
}