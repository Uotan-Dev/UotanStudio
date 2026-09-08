using System.Buffers.Binary;

namespace UotanStudio.Firmware.BoundedContexts.AndroidMeta.Apk.Signing;

/// <summary>
/// Layout of a ZIP file's End Of Central Directory and central directory sections.
/// <para>ZIP 中央目录结尾（EOCD）与中央目录区段的布局信息。</para>
/// </summary>
/// <param name="EocdOffset">Absolute offset of the EOCD record. EOCD 记录的绝对偏移。</param>
/// <param name="EocdSize">Total EOCD size including comment. 含注释的 EOCD 总大小。</param>
/// <param name="CentralDirectoryOffset">Central directory start offset as stored (pre-signing-block). EOCD 中记录的中央目录起始偏移（未含签名块时）。</param>
/// <param name="CentralDirectorySize">Central directory size in bytes. 中央目录字节大小。</param>
public readonly record struct ApkZipSections(
    long EocdOffset,
    long EocdSize,
    long CentralDirectoryOffset,
    long CentralDirectorySize)
{
    /// <summary>
    /// Locates the EOCD record and central directory sections of a ZIP/APK stream.
    /// The stream must be readable and seekable.
    /// <para>在 ZIP/APK 流中定位 EOCD 记录与中央目录区段。流必须可读且可定位。</para>
    /// </summary>
    /// <exception cref="InvalidDataException">The stream is not a ZIP container or the EOCD is corrupt.</exception>
    /// <exception cref="InvalidDataException">流不是 ZIP 容器或 EOCD 已损坏。</exception>
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

        // Scan the tail window backwards for a consistent EOCD record.
        // 从尾部窗口向后扫描一致的 EOCD 记录。
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
                // The comment must run to EOF; otherwise this is not the real EOCD.
                // 注释必须延伸到文件末尾；否则不是真正的 EOCD。
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
