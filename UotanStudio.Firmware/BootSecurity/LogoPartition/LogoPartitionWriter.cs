using System.Buffers.Binary;
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Bmp;
using Sdcb.SimdCodecs.Jpeg;
using Sdcb.SimdCodecs.Png;
using UotanStudio.Firmware.Core;

namespace UotanStudio.Firmware.BootSecurity.LogoPartition;

/// <summary>
/// Bounded, format-preserving write operations for boot logo and splash partitions. A caller supplies a
/// standard image file (BMP/PNG/JPEG); the codec decodes it into a bounded BGR raster and the writer
/// re-encodes it in the container's own pixel encoding. Every operation validates offsets and sizes,
/// cancels cooperatively, and publishes the rebuilt container through an adjacent temporary file so a
/// failure leaves the original bytes untouched.
/// <para>启动 Logo 与 splash 分区的有界、保格式写入操作。调用方提供标准图片文件（BMP/PNG/JPEG），
/// 编解码器将其解码为有界 BGR 光栅，写入器再按容器自身像素编码重新编码。每次操作都校验偏移与长度、
/// 协作取消，并通过同目录临时文件原子发布重建容器，失败时原字节保持不变。</para>
/// </summary>
public static class LogoPartitionWriter
{
    /// <summary>Maximum decoded pixels accepted from a replacement image. <para>替换图片接受的最大解码像素数。</para></summary>
    public const long MaximumReplacementPixels = LogoPartitionFormat.MaximumPixels;

    /// <summary>Maximum encoded replacement image file size. <para>替换图片文件的最大编码字节数。</para></summary>
    public const long MaximumReplacementBytes = 32L * 1024 * 1024;

    private const int SplashHeaderSize = 512;
    private const int SplashBlockSize = 512;
    private const int OplusFrameHeaderSize = 0x1000;
    private const int OplusFrameDataOffset = 0x1000;
    private const int OplusFrameFieldOffset = 0x20;
    private const int OplusFrameTableOffset = 0x30;
    private const int OplusNameOffset = 0x180;
    private const int OplusPanelOffset = 0x1C0;
    private const int BmpHeaderSize = 54;

    /// <summary>
    /// Replaces the entry at <paramref name="entryIndex"/> with the image at <paramref name="imagePath"/>,
    /// keeping the container family and the entry's pixel encoding.
    /// <para>用 <paramref name="imagePath"/> 指向的图片替换 <paramref name="entryIndex"/> 处的条目，
    /// 保持容器家族与条目像素编码不变。</para>
    /// </summary>
    /// <param name="sourcePath">Container to modify. <para>待修改的容器。</para></param>
    /// <param name="entryIndex">Zero-based index of the entry to replace. <para>要替换条目的从零开始序号。</para></param>
    /// <param name="imagePath">Standard image file supplying the replacement pixels. <para>提供替换像素的标准图片文件。</para></param>
    /// <param name="cancellationToken">Cancels the operation. <para>取消本次操作。</para></param>
    /// <returns>The new entry index (unchanged by a replacement). <para>替换后条目的序号（保持不变）。</para></returns>
    /// <exception cref="InvalidDataException">The image or container is invalid or unsupported.
    /// <para>图片或容器无效、不受支持。</para></exception>
    public static int ReplaceEntry(string sourcePath, int entryIndex, string imagePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var image = LogoPartitionFormat.Read(sourcePath, cancellationToken);
        if (entryIndex < 0 || entryIndex >= image.Entries.Count)
        {
            throw new InvalidDataException("The logo entry index is out of range.");
        }

        var raster = DecodeRaster(imagePath, cancellationToken);
        var entries = new List<EncodedEntry>();
        using (var input = File.OpenRead(sourcePath))
        {
            for (var index = 0; index < image.Entries.Count; index++)
            {
                entries.Add(index == entryIndex
                    ? EncodeEntry(image.Entries[index], raster, cancellationToken)
                    : ReadEncoded(image.Entries[index], input, cancellationToken));
            }
        }

        return WriteContainer(sourcePath, image, entries, cancellationToken);
    }

    /// <summary>
    /// Appends the image at <paramref name="imagePath"/> as a new entry using the container's default
    /// pixel encoding. GuoKe containers hold a single JPEG and therefore refuse additions.
    /// <para>把 <paramref name="imagePath"/> 指向的图片作为新条目追加到容器，使用容器默认像素编码。
    /// 果壳容器只含单张 JPEG，因此拒绝追加。</para>
    /// </summary>
    /// <param name="sourcePath">Container to modify. <para>待修改的容器。</para></param>
    /// <param name="imagePath">Standard image file supplying the new pixels. <para>提供新像素的标准图片文件。</para></param>
    /// <param name="cancellationToken">Cancels the operation. <para>取消本次操作。</para></param>
    /// <returns>The zero-based index of the appended entry. <para>新追加条目的从零开始序号。</para></returns>
    /// <exception cref="InvalidDataException">The container cannot accept a new entry.
    /// <para>容器无法接受新条目。</para></exception>
    public static int AddEntry(string sourcePath, string imagePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        var image = LogoPartitionFormat.Read(sourcePath, cancellationToken);
        if (image.Kind == LogoPartitionKind.GuoKeLogo)
        {
            throw new InvalidDataException("GuoKe logo containers hold a single image and cannot accept entries.");
        }

        if (image.Entries.Count >= LogoPartitionFormat.MaximumEntries)
        {
            throw new InvalidDataException("The logo container has reached its entry limit.");
        }

        var raster = DecodeRaster(imagePath, cancellationToken);
        var encoding = image.Kind switch
        {
            LogoPartitionKind.LogoBlockTable => LogoEntryEncoding.BmpFile,
            LogoPartitionKind.QualcommSplash => LogoEntryEncoding.RawBgr,
            LogoPartitionKind.OplusSplash => LogoEntryEncoding.PlanarRle8,
            _ => throw new InvalidDataException("Unsupported logo container family.")
        };

        var encoded = new List<EncodedEntry>();
        using (var input = File.OpenRead(sourcePath))
        {
            foreach (var entry in image.Entries)
            {
                encoded.Add(ReadEncoded(entry, input, cancellationToken));
            }
        }

        var newIndex = image.Entries.Count;
        encoded.Add(EncodeEntry(new LogoPartitionEntry(newIndex, $"new_{newIndex:00}",
            $"new_{newIndex:00}.img", encoding, 0, 0, 0, raster.Width, raster.Height, 24), raster, cancellationToken));
        WriteContainer(sourcePath, image, encoded, cancellationToken);
        return newIndex;
    }

    private readonly record struct Raster(int Width, int Height, byte[] Bgr);

    private readonly record struct EncodedEntry(LogoEntryEncoding Encoding, int Width, int Height, int BitsPerPixel, byte[] Data);

    private static Raster DecodeRaster(string imagePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = new FileInfo(imagePath);
        if (!file.Exists || file.Length > MaximumReplacementBytes)
        {
            throw new InvalidDataException("The replacement image is missing or exceeds the size limit.");
        }

        var bytes = File.ReadAllBytes(imagePath);
        if (!ImageCodecs.TryReadInfo(bytes, out var info) || info.Width <= 0 || info.Height <= 0 ||
            (long)info.Width * info.Height > MaximumReplacementPixels)
        {
            throw new InvalidDataException("The replacement image is unsupported or exceeds the pixel limit.");
        }

        var bgr = new byte[checked(info.Width * info.Height * 3)];
        var stride = checked(info.Width * 3);
        if (!ImageCodecs.TryDecodeInto(bytes, bgr, stride, PixelFormat.Bgr24, DecodeOptions.Default, cancellationToken))
        {
            throw new InvalidDataException("The replacement image could not be decoded to 24-bit BGR.");
        }

        return new Raster(info.Width, info.Height, bgr);
    }

    private static EncodedEntry ReadEncoded(LogoPartitionEntry entry, Stream input, CancellationToken cancellationToken)
    {
        FileExtraction.ValidateRange(entry.DataOffset, entry.DataSize, input.Length, "Logo entry payload");
        var data = new byte[entry.DataSize];
        input.Position = entry.DataOffset;
        input.ReadExactly(data);
        cancellationToken.ThrowIfCancellationRequested();
        return new EncodedEntry(entry.Encoding, entry.Width, entry.Height, entry.BitsPerPixel, data);
    }

    private static EncodedEntry EncodeEntry(LogoPartitionEntry entry, Raster raster, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return entry.Encoding switch
        {
            LogoEntryEncoding.BmpFile => EncodeBmp(raster),
            LogoEntryEncoding.Png => EncodePng(raster),
            LogoEntryEncoding.RawBgr => new EncodedEntry(LogoEntryEncoding.RawBgr, raster.Width, raster.Height, 24, EncodeRawBgr(raster, cancellationToken)),
            LogoEntryEncoding.Rle24Bgr => new EncodedEntry(LogoEntryEncoding.Rle24Bgr, raster.Width, raster.Height, 24, EncodeRle24(raster, cancellationToken)),
            LogoEntryEncoding.PlanarRle8 => new EncodedEntry(LogoEntryEncoding.PlanarRle8, raster.Width, raster.Height, 24, EncodePlanarRle8(raster, cancellationToken)),
            LogoEntryEncoding.Jpeg => EncodeJpeg(raster),
            _ => throw new InvalidDataException("Unsupported logo entry encoding.")
        };
    }

    private static EncodedEntry EncodeBmp(Raster raster)
    {
        var options = new BmpEncodeOptions { Format = BmpEncodeFormat.Rgb24, PixelsPerMeterX = 0, PixelsPerMeterY = 0 };
        var data = BmpEncoder.Encode(raster.Bgr, raster.Width, raster.Height, PixelFormat.Bgr24, options, checked(raster.Width * 3));
        return new EncodedEntry(LogoEntryEncoding.BmpFile, raster.Width, raster.Height, 24, data);
    }

    private static EncodedEntry EncodePng(Raster raster)
    {
        // The raster is BGR; PngWriter expects RGB, so swap each triplet.
        var rgb = new byte[raster.Bgr.Length];
        for (var i = 0; i < raster.Bgr.Length; i += 3)
        {
            rgb[i] = raster.Bgr[i + 2];
            rgb[i + 1] = raster.Bgr[i + 1];
            rgb[i + 2] = raster.Bgr[i];
        }

        var data = PngWriter.EncodeRgb8(rgb, raster.Width, raster.Height);
        return new EncodedEntry(LogoEntryEncoding.Png, raster.Width, raster.Height, 24, data);
    }

    private static EncodedEntry EncodeJpeg(Raster raster)
    {
        var rgb = new byte[raster.Bgr.Length];
        for (var i = 0; i < raster.Bgr.Length; i += 3)
        {
            rgb[i] = raster.Bgr[i + 2];
            rgb[i + 1] = raster.Bgr[i + 1];
            rgb[i + 2] = raster.Bgr[i];
        }

        var data = JpegEncoder.Encode(rgb, raster.Width, raster.Height, 3, new JpegEncodeOptions());
        return new EncodedEntry(LogoEntryEncoding.Jpeg, raster.Width, raster.Height, 24, data);
    }

    private static byte[] EncodeRawBgr(Raster raster, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (byte[])raster.Bgr.Clone();
    }

    private static byte[] EncodeRle24(Raster raster, CancellationToken cancellationToken)
    {
        var output = new List<byte>(raster.Bgr.Length / 2);
        var source = 0;
        while (source < raster.Bgr.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pixel = raster.Bgr.AsSpan(source, 3);
            var run = 1;
            while (source + (run * 3) < raster.Bgr.Length && run < 129 &&
                   raster.Bgr.AsSpan(source + (run * 3), 3).SequenceEqual(pixel))
            {
                run++;
            }

            if (run > 1)
            {
                output.Add((byte)(run - 1 + 128));
                output.Add(pixel[0]);
                output.Add(pixel[1]);
                output.Add(pixel[2]);
            }
            else
            {
                output.Add(0);
                output.Add(pixel[0]);
                output.Add(pixel[1]);
                output.Add(pixel[2]);
            }

            source += run * 3;
        }

        return output.ToArray();
    }

    private static byte[] EncodePlanarRle8(Raster raster, CancellationToken cancellationToken)
    {
        var output = new List<byte>(raster.Bgr.Length / 2);
        for (var i = 0; i < raster.Bgr.Length; i += 3)
        {
            if ((i & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // On-disk order is R, G, B (the inverse of the BGR raster).
            output.Add(raster.Bgr[i + 2]);
            output.Add(1);
            output.Add(raster.Bgr[i + 1]);
            output.Add(1);
            output.Add(raster.Bgr[i]);
            output.Add(1);
        }

        return output.ToArray();
    }

    private static int WriteContainer(string sourcePath, LogoPartitionImage image,
        IReadOnlyList<EncodedEntry> entries, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var header = new byte[64 * 1024];
        using (var input = File.OpenRead(sourcePath))
        {
            input.Position = 0;
            input.ReadExactly(header.AsSpan(0, (int)Math.Min(header.Length, input.Length)));
        }

        var rebuilt = image.Kind switch
        {
            LogoPartitionKind.LogoBlockTable => RebuildBlockTable(header, image, entries, cancellationToken),
            LogoPartitionKind.QualcommSplash => RebuildQualcommSplash(header, image, entries, cancellationToken),
            LogoPartitionKind.OplusSplash => RebuildOplusSplash(header, image, entries, cancellationToken),
            LogoPartitionKind.GuoKeLogo => RebuildGuoKe(header, entries, cancellationToken),
            _ => throw new InvalidDataException("Unsupported logo container family.")
        };

        FileExtraction.WriteAtomic(sourcePath, output => output.Write(rebuilt), overwrite: true, cancellationToken);
        return entries.Count;
    }

    private static byte[] RebuildBlockTable(byte[] header, LogoPartitionImage image,
        IReadOnlyList<EncodedEntry> entries, CancellationToken cancellationToken)
    {
        var tableOffset = image.HeaderOffset;
        var blockSize = int.Parse(image.Metadata["BlockSize"]);
        using var output = new MemoryStream();
        // The original header already carries the LOGO!!!! magic at tableOffset; keep it and
        // overwrite only the table records and payloads that follow it.
        output.Write(header.AsSpan(0, tableOffset + 8));
        var records = new List<(int Offset, int Count)>();
        var payloads = new List<byte[]>();
        var cursor = Align(tableOffset + 8 + (entries.Count * 8), blockSize);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Max(1, (entry.Data.Length + blockSize - 1) / blockSize);
            records.Add((cursor / blockSize, count));
            var payload = new byte[count * blockSize];
            entry.Data.CopyTo(payload, 0);
            payloads.Add(payload);
            cursor += payload.Length;
        }

        Span<byte> recordBytes = stackalloc byte[8];
        foreach (var record in records)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(recordBytes, (uint)record.Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(recordBytes[4..], (uint)record.Count);
            output.Write(recordBytes);
        }

        // Align the physical payload position with the block indices recorded in the table.
        while (output.Position < (long)records[0].Offset * blockSize)
        {
            output.WriteByte(0);
        }

        foreach (var payload in payloads)
        {
            output.Write(payload);
        }

        return output.ToArray();
    }

    private static byte[] RebuildQualcommSplash(byte[] header, LogoPartitionImage image,
        IReadOnlyList<EncodedEntry> entries, CancellationToken cancellationToken)
    {
        var prefix = image.HeaderOffset;
        using var output = new MemoryStream();
        output.Write(header.AsSpan(0, prefix));
        for (var i = 0; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[i];
            var blocks = (entry.Data.Length + SplashBlockSize - 1) / SplashBlockSize;
            var frameHeader = new byte[SplashHeaderSize];
            "SPLASH!!"u8.CopyTo(frameHeader);
            BinaryPrimitives.WriteUInt32LittleEndian(frameHeader.AsSpan(8), (uint)entry.Width);
            BinaryPrimitives.WriteUInt32LittleEndian(frameHeader.AsSpan(12), (uint)entry.Height);
            BinaryPrimitives.WriteUInt32LittleEndian(frameHeader.AsSpan(16), entry.Encoding == LogoEntryEncoding.Rle24Bgr ? 1u : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(frameHeader.AsSpan(20), (uint)blocks);
            output.Write(frameHeader);
            var payload = new byte[blocks * SplashBlockSize];
            entry.Data.CopyTo(payload, 0);
            output.Write(payload);
        }

        return output.ToArray();
    }

    private static byte[] RebuildOplusSplash(byte[] header, LogoPartitionImage image,
        IReadOnlyList<EncodedEntry> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            throw new InvalidDataException("The Oplus splash container has no frames.");
        }

        var first = entries[0];
        var mainHeader = new byte[0x2000];
        header.AsSpan(0, Math.Min(mainHeader.Length, header.Length)).CopyTo(mainHeader);
        // Clear the old frame table so stale offsets cannot survive a shrink or reorder.
        mainHeader.AsSpan(OplusFrameTableOffset, OplusNameOffset - OplusFrameTableOffset).Clear();
        "SPLASH!!"u8.CopyTo(mainHeader);
        WriteOplusFields(mainHeader, first);

        var offsets = new List<int>();
        var payloads = new List<byte[]>();
        var cursor = 0x2000;
        for (var i = 0; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = entries[i];
            offsets.Add(cursor);
            var payload = entry.Data;
            var frame = new byte[OplusFrameHeaderSize + payload.Length];
            "SPLASH!!"u8.CopyTo(frame);
            WriteOplusFields(frame, entry);
            payload.CopyTo(frame, OplusFrameHeaderSize);
            payloads.Add(frame);
            cursor += frame.Length;
        }

        for (var i = 0; i < offsets.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(mainHeader.AsSpan(OplusFrameTableOffset + (i * 4)), (uint)offsets[i]);
        }

        using var output = new MemoryStream();
        output.Write(mainHeader);
        foreach (var frame in payloads)
        {
            output.Write(frame);
        }

        return output.ToArray();
    }

    private static void WriteOplusFields(Span<byte> target, EncodedEntry entry)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target[OplusFrameFieldOffset..], (uint)entry.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(target[(OplusFrameFieldOffset + 4)..], (uint)entry.Height);
        BinaryPrimitives.WriteUInt32LittleEndian(target[(OplusFrameFieldOffset + 8)..], (uint)entry.Data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(target[(OplusFrameFieldOffset + 12)..], entry.Encoding == LogoEntryEncoding.PlanarRle8 ? 1u : 0u);
    }

    private static byte[] RebuildGuoKe(byte[] header, IReadOnlyList<EncodedEntry> entries, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entries.Count != 1 || entries[0].Encoding != LogoEntryEncoding.Jpeg)
        {
            throw new InvalidDataException("GuoKe logo containers require exactly one JPEG entry.");
        }

        using var output = new MemoryStream();
        output.Write(header.AsSpan(0, 0x2000));
        output.Write(entries[0].Data);
        return output.ToArray();
    }

    private static int Align(int value, int alignment) => (value + alignment - 1) / alignment * alignment;
}