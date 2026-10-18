using System.Buffers.Binary;

namespace UotanStudio.Firmware.AndroidMeta.Apk.Signing;

/// <summary>Layout of a ZIP file's End Of Central Directory and central directory sections.
/// <para>ZIP 中央目录结尾（EOCD）与中央目录区段的布局信息。</para></summary>
/// <param name="EocdOffset">Absolute offset of the EOCD record.<para>EOCD 记录的绝对偏移。</para></param>
/// <param name="EocdSize">Total EOCD size including comment.<para>含注释的 EOCD 总大小。</para></param>
/// <param name="CentralDirectoryOffset">Central directory start offset as stored (pre-signing-block).
/// <para>EOCD 中记录的中央目录起始偏移（未含签名块时）。</para></param>
/// <param name="CentralDirectorySize">Central directory size in bytes.
/// <para>中央目录字节大小。</para></param>
public readonly record struct ApkZipSections(
    long EocdOffset,
    long EocdSize,
    long CentralDirectoryOffset,
    long CentralDirectorySize)
{
    /// <summary>Locates the EOCD record and central directory sections of a ZIP/APK stream. The stream must be readable and seekable.
    /// <para>在 ZIP/APK 流中定位 EOCD 记录与中央目录区段。流必须可读且可定位。</para></summary>
    /// <param name="stream">Readable, seekable container stream; it is repositioned but not closed.
    /// <para>可读、可定位的容器流；只重定位、不关闭。</para></param>
    /// <returns>Located sections, validated against the stream length; the EOCD scan only accepts a record whose comment extends exactly to the end of the stream.
    /// <para>已定位并通过流长度校验的区段；EOCD 扫描仅接受 注释恰好延伸到流末尾的记录。</para></returns>
    /// <exception cref="InvalidDataException">The stream is not seekable or too small, is not a ZIP container, uses ZIP64 placeholders, or the EOCD is corrupt.
    /// <para>流不可定位或过小、不是 ZIP 容器、使用了 ZIP64 占位值，或 EOCD 已损坏。</para></exception>
    public static ApkZipSections Locate(Stream stream)
    {
        if (!stream.CanSeek)
        {
            throw new InvalidDataException("APK stream must be seekable to locate the ZIP sections. APK 流必须可定位才能解析 ZIP 区段。");
        }

        var maximumEocdSize = 22 + 0xFFFF;
        var scanLength = (long)Math.Min(maximumEocdSize, stream.Length);
        if (scanLength < 22)
        {
            throw new InvalidDataException("File is too small to contain a ZIP EOCD record. 文件过小，无法包含 ZIP EOCD 记录。");
        }

        var windowOffset = stream.Length - scanLength;
        stream.Seek(windowOffset, SeekOrigin.Begin);
        var window = new byte[scanLength];
        stream.ReadExactly(window);

        for (var candidate = window.Length - 22; candidate >= 0; candidate--)
        {
            if (window[candidate] != 0x50 || window[candidate + 1] != 0x4b
                || window[candidate + 2] != 0x05 || window[candidate + 3] != 0x06)
            {
                continue;
            }

            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(window.AsSpan(candidate + 20, 2));
            var eocdSize = 22 + commentLength;
            if (candidate + eocdSize != window.Length)
            {
                continue;
            }

            var eocdOffset = windowOffset + candidate;
            var centralDirectoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(window.AsSpan(candidate + 16, 4));
            var centralDirectorySize = BinaryPrimitives.ReadUInt32LittleEndian(window.AsSpan(candidate + 12, 4));
            if (centralDirectoryOffset == 0xFFFFFFFF || centralDirectorySize == 0xFFFFFFFF)
            {
                throw new InvalidDataException("ZIP64 archives are not supported for APK signing. APK 签名不支持 ZIP64 容器。");
            }

            var sections = new ApkZipSections(
                eocdOffset,
                eocdSize,
                centralDirectoryOffset,
                centralDirectorySize);
            sections.Validate(stream.Length);
            return sections;
        }

        throw new InvalidDataException("ZIP EOCD record not found; the file is not a valid APK container. 未找到 ZIP EOCD 记录；文件不是有效的 APK 容器。");
    }

    /// <summary>
    /// Validates that the central directory lies inside the file.
    /// <para>校验中央目录位于文件范围内。</para>
    /// </summary>
    /// <param name="fileLength">Total file length the EOCD must exactly end at.
    /// <para>EOCD 必须恰好结束的总文件长度。</para></param>
    /// <exception cref="InvalidDataException">The central directory is negative, overlaps the EOCD, or the
    /// EOCD does not end exactly at <paramref name="fileLength"/>.
    /// <para>中央目录偏移或大小为负、与 EOCD 重叠，或 EOCD 未恰好在
    /// <paramref name="fileLength"/> 处结束。</para></exception>
    public void Validate(long fileLength)
    {
        if (CentralDirectoryOffset < 0 || CentralDirectorySize < 0
            || CentralDirectoryOffset + CentralDirectorySize > EocdOffset
            || EocdOffset + EocdSize != fileLength)
        {
            throw new InvalidDataException(
                $"ZIP central directory layout is inconsistent (cd offset {CentralDirectoryOffset}, size {CentralDirectorySize}, eocd offset {EocdOffset}). ZIP 中央目录布局不一致（CD 偏移 {CentralDirectoryOffset}，大小 {CentralDirectorySize}，EOCD 偏移 {EocdOffset}）。");
        }
    }
}