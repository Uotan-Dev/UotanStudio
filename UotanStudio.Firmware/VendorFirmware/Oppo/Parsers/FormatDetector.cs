using System.Buffers.Binary;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.VendorFirmware.Oppo.Models;

namespace UotanStudio.Firmware.VendorFirmware.Oppo.Parsers;

internal static class FormatDetector
{
    private static readonly List<Func<Stream, OppFormat?>> CustomDetectors = [];

    /// <summary>
    /// Registers a custom format detector.
    /// <para>注册自定义格式检测器。</para>
    /// </summary>
    public static void RegisterDetector(Func<Stream, OppFormat?> detector) => CustomDetectors.Add(detector);

    private static ReadOnlySpan<byte> OzipMagic => [0x4F, 0x50, 0x50, 0x4F, 0x45, 0x4E, 0x43, 0x52, 0x59, 0x50, 0x54, 0x21];

    /// <summary>
    /// Detects the format of an Oppo firmware file at the specified path.
    /// <para>检测指定文件路径的 Oppo 固件格式。</para>
    /// </summary>
    internal static OppFormat DetectFormat(string filePath)
    {
        Guard.ThrowIfNullOrWhiteSpace(filePath);

        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.SequentialScan);

        return DetectFormatFromStream(stream);
    }

    /// <summary>
    /// Detects the format from a stream (does not close the stream).
    /// <para>从流中检测 Oppo 固件格式（不关闭流）。</para>
    /// </summary>
    internal static OppFormat DetectFormatFromStream(Stream stream)
    {
        Guard.ThrowIfNull(stream);

        if (!stream.CanRead || !stream.CanSeek)
        {
            return OppFormat.Unknown;
        }

        var originalPosition = stream.Position;

        try
        {
            var fileSize = stream.Length;


            if (fileSize >= 12)
            {
                stream.Position = 0;
                var ozipHeader = new byte[12];
                var bytesRead = stream.Read(ozipHeader, 0, 12);
                if (bytesRead == 12 && ozipHeader.AsSpan().SequenceEqual(OzipMagic))
                {
                    return OppFormat.Ozip;
                }
            }


            if (fileSize >= 2)
            {
                stream.Position = 0;
                var headerBytes = new byte[2];
                var bytesRead = stream.Read(headerBytes, 0, 2);
                if (bytesRead == 2 && headerBytes[0] == 0x50 && headerBytes[1] == 0x4B)
                {
                    return OppFormat.OfpQc;
                }
            }


            if (fileSize >= OpsTailPage.Size)
            {
                stream.Position = fileSize - OpsTailPage.Size;
                var tailBuffer = new byte[OpsTailPage.Size];
                var bytesRead = stream.Read(tailBuffer, 0, OpsTailPage.Size);
                if (bytesRead == OpsTailPage.Size)
                {
                    var tailData = new ReadOnlySpan<byte>(tailBuffer);
                    var version = BinaryPrimitives.ReadUInt32LittleEndian(tailData[..4]);
                    var flags = BinaryPrimitives.ReadUInt32LittleEndian(tailData.Slice(4, 4));
                    var hasMagic = OfpQcTailPage.ValidateMagic(tailData, 0x10);

                    if (hasMagic && version == 2 && flags == 1)
                    {
                        return OppFormat.Ops;
                    }
                }
            }


            int[] pageSizes = [0x200, 0x1000];
            foreach (var pageSize in pageSizes)
            {
                if (fileSize < pageSize)
                {
                    continue;
                }

                stream.Position = fileSize - pageSize;
                var pageBuffer = new byte[pageSize];
                var bytesRead = stream.Read(pageBuffer, 0, pageSize);
                if (bytesRead == pageSize)
                {
                    var pageData = new ReadOnlySpan<byte>(pageBuffer);
                    if (OfpQcTailPage.ValidateMagic(pageData, 0x10))
                    {
                        return OppFormat.OfpQc;
                    }
                }
            }


            foreach (var detector in CustomDetectors)
            {
                var customResult = detector(stream);
                if (customResult.HasValue && customResult.Value != OppFormat.Unknown)
                {
                    return customResult.Value;
                }
            }

            return OppFormat.Unknown;
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    /// <summary>
    /// Checks if the byte span consists entirely of printable ASCII characters or null terminators.
    /// <para>检查字节跨度是否全部为可打印 ASCII 字符或空终止符。</para>
    /// </summary>
    internal static bool IsPrintableAscii(ReadOnlySpan<byte> data)
    {
        var hasPrintable = false;
        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            if (b == 0)
            {
                continue;
            }

            if (b is < 0x20 or > 0x7E)
            {
                return false;
            }

            hasPrintable = true;
        }

        return hasPrintable;
    }

}