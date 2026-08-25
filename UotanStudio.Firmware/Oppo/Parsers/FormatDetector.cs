using System.Buffers.Binary;
using UotanStudio.Firmware.Oppo.Helpers;
using UotanStudio.Firmware.Oppo.Models;

namespace UotanStudio.Firmware.Oppo.Parsers;

/// <summary>
/// <para>Oppo 固件文件格式自动检测器。</para>
/// Auto-detector for Oppo firmware file formats.
/// </summary>
internal static class FormatDetector
{
    /// <summary>
    /// <para>注册的自定义格式检测器列表。</para>
    /// Custom format detectors registered by callers.
    /// </summary>
    private static readonly List<Func<Stream, OppFormat?>> CustomDetectors = [];

    /// <summary>
    /// <para>注册自定义格式检测器。调用方可注册增强的格式检测逻辑。</para>
    /// Registers a custom format detector. Callers can register enhanced format detection logic.
    /// </summary>
    /// <param name="detector"><para>格式检测器委托，接收流并返回检测到的格式（若未检测到返回 null 或 Unknown）。</para>A format detector delegate that receives a stream and returns the detected format (or null/Unknown if not detected).</param>
    public static void RegisterDetector(Func<Stream, OppFormat?> detector) => CustomDetectors.Add(detector);

    /// <summary>
    /// <para>OZip 加密固件的魔数标识 "OPPOENCRYPT!" 的字节序列。</para>
    /// The byte sequence of the OZip encrypted firmware magic "OPPOENCRYPT!".
    /// </summary>
    private static ReadOnlySpan<byte> OzipMagic => [0x4F, 0x50, 0x50, 0x4F, 0x45, 0x4E, 0x43, 0x52, 0x59, 0x50, 0x54, 0x21]; // "OPPOENCRYPT!"

    /// <summary>
    /// <para>检测指定文件路径的 Oppo 固件格式。</para>
    /// Detects the format of an Oppo firmware file at the specified path.
    /// </summary>
    /// <param name="filePath"><para>固件文件路径。</para>Path to the firmware file.</param>
    /// <returns><para>检测到的格式，若检测失败返回 OppFormat.Unknown。</para>The detected format, or OppFormat.Unknown if detection fails.</returns>
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
    /// <para>从流中检测 Oppo 固件格式（不关闭流）。</para>
    /// Detects the format from a stream (does not close the stream).
    /// </summary>
    /// <param name="stream"><para>可读取的流。</para>A readable stream.</param>
    /// <returns><para>检测到的格式，若检测失败返回 OppFormat.Unknown。</para>The detected format, or OppFormat.Unknown if detection fails.</returns>
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

            // Step 1: Check for OZip magic "OPPOENCRYPT!" (12 bytes)
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

            // Step 2: Check for ZIP signature (PK = 0x504B)
            if (fileSize >= 2)
            {
                stream.Position = 0;
                var headerBytes = new byte[2];
                var bytesRead = stream.Read(headerBytes, 0, 2);
                if (bytesRead == 2 && headerBytes[0] == 0x50 && headerBytes[1] == 0x4B)
                {
                    return OppFormat.OfpQc; // ZIP-wrapped, treat as QC
                }
            }

            // Step 3: Check for OPS tail page
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

            // Step 4: Try QC tail page detection with different page sizes
            var pageSizes = new[] { 0x200, 0x1000 };
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

            // Step 5: MTK detection is delegated to the registered custom detectors below.
            // The former naive tail scan that searched for the ASCII substring "MTK" was removed
            // because it produced false positives: the trailing bytes of unrelated images (e.g. TWRP /
            // Android boot images) often contained "MTK", routing non-Oppo files to the Oppo MTK handler.

            // Step 6: Try custom detectors
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
    /// <para>检查字节跨度是否全部为零。</para>
    /// Checks if the byte span consists entirely of zero bytes.
    /// </summary>
    /// <param name="data"><para>要检查的字节跨度。</para>The byte span to check.</param>
    /// <returns><para>若所有字节均为零返回 true；否则返回 false。</para>True if all bytes are zero; otherwise, false.</returns>
    internal static bool IsAllZeros(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// <para>检查字节跨度是否全部为可打印 ASCII 字符或空终止符。</para>
    /// Checks if the byte span consists entirely of printable ASCII characters or null terminators.
    /// </summary>
    /// <param name="data"><para>要检查的字节跨度。</para>The byte span to check.</param>
    /// <returns><para>若包含可打印字符返回 true；否则返回 false。</para>True if printable characters are present; otherwise, false.</returns>
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