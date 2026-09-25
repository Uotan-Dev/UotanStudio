using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.VendorFirmware.Rockchip.Models;
using UotanStudio.Firmware.SharedKernel;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.VendorFirmware.Rockchip.Extraction;

/// <summary>Reads and extracts RKFW and RKAF containers.<para>读取并提取 RKFW 和 RKAF 容器。</para></summary>
public static class RockchipExtractor
{
    /// <summary>Lists RKFW loader/image entries or RKAF package files.<para>列出 RKFW 加载器和镜像，或 RKAF 包文件。</para></summary>
    /// <param name="imagePath">
    /// Container to open and fully validate, including the trailing RKFW MD5 or RKAF CRC checksum;
    /// the file is closed when the call returns.
    /// <para>要打开并完整校验的容器，包含尾部的 RKFW MD5 或 RKAF CRC 校验和；调用返回时文件已关闭。</para>
    /// </param>
    /// <returns>The loader, embedded-image and partition entries declared by the container.
    /// <para>容器声明的加载器、内嵌镜像与分区条目。</para></returns>
    /// <exception cref="InvalidDataException">The container magic, geometry or checksum is invalid.
    /// <para>容器魔数、几何信息或校验和无效。</para></exception>
    /// <exception cref="EndOfStreamException">A declared region or checksum extends beyond the end of the file.
    /// <para>声明的区域或校验和超出文件末尾。</para></exception>
    public static List<RkfwEntry> List(string imagePath)
    {
        using var stream = File.OpenRead(imagePath);
        return ReadEntries(stream);
    }

    /// <summary>Reads container entry metadata.<para>读取容器条目元数据。</para></summary>
    /// <param name="imagePath">
    /// Container to open; metadata reading runs the same full validation as <see cref="List"/>.
    /// <para>要打开的容器；元数据读取执行与 <see cref="List"/> 相同的完整校验。</para>
    /// </param>
    /// <returns>The same entry list <see cref="List"/> produces.
    /// <para>与 <see cref="List"/> 相同的条目列表。</para></returns>
    /// <exception cref="InvalidDataException">The container magic, geometry or checksum is invalid.
    /// <para>容器魔数、几何信息或校验和无效。</para></exception>
    public static List<RkfwEntry> ReadMetadata(string imagePath) => List(imagePath);

    /// <summary>Reads bounded RKFW headers and the embedded RKAF table without scanning payload checksums.
    /// <para>读取经过边界校验的 RKFW 头部与内嵌 RKAF 表，但不扫描负载校验和。</para></summary>
    /// <param name="imagePath">
    /// RKFW file to preview; the payload MD5 trailer and the RKAF CRC are not verified. The file is closed
    /// when the call returns.
    /// <para>要预览的 RKFW 文件；不校验负载的 MD5 尾部与 RKAF 的 CRC。调用返回时文件已关闭。</para>
    /// </param>
    /// <returns>The parsed header plus loader, embedded-image and RKAF partition entries whose offsets are
    /// absolute within the file.
    /// <para>解析后的头部，以及加载器、内嵌镜像与 RKAF 分区条目；条目偏移为文件内绝对偏移。</para></returns>
    /// <exception cref="InvalidDataException">The RKFW magic, header size, payload regions, loader magic or
    /// embedded RKAF layout is invalid.
    /// <para>RKFW 魔数、头部长度、负载区域、加载器魔数或内嵌 RKAF 布局无效。</para></exception>
    /// <exception cref="EndOfStreamException">The file is shorter than the bytes the preview must read.
    /// <para>文件长度不足以满足预览所需读取的字节数。</para></exception>
    /// <example>
    /// <code>
    /// var (header, entries) = RockchipExtractor.PreviewRkfw("update.rkfw");
    /// </code>
    /// </example>
    public static (RkFwHeader Header, List<RkfwEntry> Entries) PreviewRkfw(string imagePath)
    {
        using var stream = File.OpenRead(imagePath);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var header = RkFwHeader.Read(reader);
        if (header.Tag != RkFwHeader.MagicRkfw || header.HeaderSize < RkFwHeader.StructSize ||
            header.HeaderSize > stream.Length || stream.Length < RkFwHeader.StructSize + 32)
            throw new InvalidDataException("Invalid RKFW header length or magic.");
        CheckRegion(header.BootOffset, header.BootSize, header.HeaderSize, stream.Length - 32);
        CheckRegion(header.ImageOffset, header.ImageSize, header.HeaderSize, stream.Length - 32);
        if (header.BootSize < 4 || header.ImageSize < 2052 ||
            (header.BootOffset < (long)header.ImageOffset + header.ImageSize &&
             header.ImageOffset < (long)header.BootOffset + header.BootSize))
            throw new InvalidDataException("Invalid or overlapping RKFW payload regions.");
        stream.Position = header.BootOffset;
        var loaderName = reader.ReadUInt32() switch
        {
            0x544F4F42 => "BOOT",
            0x2052444C => "LDR",
            _ => throw new InvalidDataException("Invalid RKFW loader magic.")
        };
        var entries = new List<RkfwEntry>
        {
            new(loaderName, header.BootOffset, header.BootSize, RkfwEntryType.Boot),
            new("embedded-update.img", header.ImageOffset, header.ImageSize, RkfwEntryType.Image)
        };
        entries.AddRange(ReadRkaf(reader, header.ImageOffset, header.ImageSize, validateChecksum: false)
            .Select(entry => entry with { Name = $"embedded-update.img/{entry.Name}", Offset = header.ImageOffset + entry.Offset }));
        return (header, entries);
    }

    /// <summary>Extracts validated entries with atomic file replacement.<para>校验条目后以原子文件替换方式提取。</para></summary>
    /// <param name="imagePath">The source image path.
    /// <para>源镜像路径。</para></param>
    /// <param name="outputDirectory">Directory that receives the fixed header template file and one file
    /// per validated entry.
    /// <para>接收固定头部模板文件以及每个已校验条目对应文件的目录。</para></param>
    /// <param name="progress">The optional progress sink, reported with a fraction between 0 and 1 after
    /// each extracted entry.
    /// <para>可选进度接收器，每提取完一个条目收到一次 0 到 1 之间的比例值。</para></param>
    /// <param name="overwrite">Whether existing files may be replaced.
    /// <para>是否允许替换现有文件。</para></param>
    /// <param name="cancellationToken">The cancellation token.
    /// <para>取消令牌。</para></param>
    /// <returns>The number of container entries written, not counting the header template file.
    /// <para>已写入的容器条目数量，不计头部模板文件。</para></returns>
    /// <exception cref="InvalidDataException">The container fails validation, or two entries resolve to the
    /// same output path.
    /// <para>容器未通过校验，或两个条目解析到同一输出路径。</para></exception>
    /// <exception cref="IOException">An output file already exists while replacement is disabled.
    /// <para>禁用替换时目标文件已存在。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before or during a copy.
    /// <para>在复制开始前或复制期间请求了取消。</para></exception>
    /// <example>
    /// <code>
    /// int count = RockchipExtractor.Extract("update.rkfw", @"D:\out", overwrite: false);
    /// </code>
    /// </example>
    public static int Extract(string imagePath, string outputDirectory, IProgress<double>? progress = null,
        bool overwrite = true, CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(imagePath);
        var entries = ReadEntries(stream);
        var paths = entries.Select(e => FileExtraction.ResolvePath(outputDirectory, e.Name)).ToArray();
        stream.Position = 0;
        Span<byte> magicBytes = stackalloc byte[4];
        stream.ReadExactly(magicBytes);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(magicBytes);
        var templateName = magic == RkFwHeader.MagicRkfw ? "rkfw-header.bin" : "rkaf-header.bin";
        var templateSize = magic == RkFwHeader.MagicRkfw ? RkFwHeader.StructSize : 2048;
        var templatePath = FileExtraction.ResolvePath(outputDirectory, templateName);
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
            throw new InvalidDataException("Duplicate Rockchip output paths.");
        if (!overwrite && (File.Exists(templatePath) || paths.Any(File.Exists)))
            throw new IOException("One or more Rockchip output files already exist and overwrite is disabled.");
        FileExtraction.WriteAtomic(templatePath, output =>
        {
            stream.Position = 0;
            StreamCopy.CopyExactly(stream, output, templateSize, cancellationToken: cancellationToken);
        }, overwrite);
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
        ValidateRkfwMd5(stream);
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

    private static List<RkfwEntry> ReadRkaf(BinaryReader reader, long baseOffset = 0, long? imageLength = null,
        bool validateChecksum = true)
    {
        var stream = reader.BaseStream;
        var length = imageLength ?? stream.Length;
        if (length < 2052)
            throw new InvalidDataException("RKAF image is shorter than its fixed header and checksum.");
        Span<byte> header = stackalloc byte[2048];
        stream.Position = baseOffset;
        stream.ReadExactly(header);
        var declaredLength = (long)BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) + 4;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != RkFwHeader.MagicRkaf || declaredLength != length)
            throw new InvalidDataException($"Invalid RKAF magic or length: declared {declaredLength}, actual {length}.");
        if (validateChecksum)
            ValidateRkafChecksum(stream, declaredLength);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header[136..]);
        if (count == 0 || count > 16)
            throw new InvalidDataException("RKAF entry count is outside the supported range.");
        const long tableEnd = 2048;
        var entries = new List<RkfwEntry>();
        Span<byte> descriptor = stackalloc byte[112];
        Span<byte> parameterHeader = stackalloc byte[8];
        for (uint i = 0; i < count; i++)
        {
            stream.Position = baseOffset + 140L + i * 112L;
            stream.ReadExactly(descriptor);
            var name = ReadString(descriptor[..32]);
            var path = ReadString(descriptor.Slice(32, 64));
            if (path == "SELF") continue;
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[96..]);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[108..]);
            CheckRegion(offset, size, tableEnd, declaredLength - 4);
            if (name == "parameter")
            {
                if (size < 12) throw new InvalidDataException("Truncated RKAF parameter wrapper.");
                stream.Position = baseOffset + offset;
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

    private static void ValidateRkfwMd5(Stream stream)
    {
        if (stream.Length < RkFwHeader.StructSize + 32)
            throw new InvalidDataException("RKFW image is missing its MD5 trailer.");
        Span<byte> stored = stackalloc byte[32];
        stream.Position = stream.Length - stored.Length;
        stream.ReadExactly(stored);
        foreach (var value in stored)
        {
            if (value is not (>= (byte)'0' and <= (byte)'9') and not (>= (byte)'a' and <= (byte)'f'))
                throw new InvalidDataException("RKFW MD5 trailer is not lowercase hexadecimal ASCII.");
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        stream.Position = 0;
        var remaining = stream.Length - stored.Length;
        var buffer = new byte[128 * 1024];
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new EndOfStreamException();
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        var actual = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(hash.GetHashAndReset()));
        if (!CryptographicOperations.FixedTimeEquals(stored, actual))
            throw new InvalidDataException("RKFW MD5 checksum mismatch.");
    }

    private static void ValidateRkafChecksum(Stream stream, long length)
    {
        stream.Position = 0;
        var remaining = length - 4;
        var crc = 0U;
        var buffer = new byte[128 * 1024];
        while (remaining > 0)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new EndOfStreamException();
            foreach (var value in buffer.AsSpan(0, read))
            {
                crc ^= (uint)value << 24;
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C10DB7U : crc << 1;
            }
            remaining -= read;
        }
        Span<byte> stored = stackalloc byte[4];
        stream.ReadExactly(stored);
        if (BinaryPrimitives.ReadUInt32LittleEndian(stored) != crc)
            throw new InvalidDataException("RKAF CRC checksum mismatch.");
    }
}