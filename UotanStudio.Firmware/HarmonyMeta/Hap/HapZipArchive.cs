using System.Buffers.Binary;

namespace UotanStudio.Firmware.HarmonyMeta.Hap;

/// <summary>
/// One parsed ZIP entry of a HAP container, keeping the local-header fields and the raw compressed payload
/// verbatim so a re-written archive reproduces the original compressed bytes.
/// <para>HAP 容器中一个已解析的 ZIP 条目，保留局部头字段与原始压缩载荷，使重写后的压缩字节与原档一致。</para>
/// </summary>
internal sealed class HapZipEntry
{
    /// <summary>Fixed local header size. <para>局部头固定长度。</para></summary>
    internal const int LocalHeaderFixed = 30;

    private const uint LocalHeaderSignature = 0x04034b50;
    private const ushort MethodStored = 0;
    private const ushort FlagDataDescriptor = 0x0008;

    internal ushort VersionMadeBy;
    internal ushort VersionNeeded;
    internal ushort Flags;
    internal ushort Method;
    internal ushort DosTime;
    internal ushort DosDate;
    internal uint Crc;
    internal uint CompressedSize;
    internal uint UncompressedSize;
    internal ushort DiskStart;
    internal ushort InternalAttributes;
    internal uint ExternalAttributes;
    internal byte[] Name = [];
    internal byte[] CentralExtra = [];
    internal byte[] Comment = [];
    internal byte[] LocalExtra = [];
    internal byte[] Data = [];

    /// <summary>Absolute offset of the local header as last read or rebuilt. <para>最近一次读取或重建的局部头绝对偏移。</para></summary>
    internal long LocalOffset { get; set; }

    /// <summary>Gets the entry name as text. <para>以文本形式获取条目名。</para></summary>
    internal string NameText => System.Text.Encoding.UTF8.GetString(Name);

    /// <summary>Reports whether the entry is a runnable artifact (<c>.abc</c>/<c>.an</c> or under <c>libs/</c>).
    /// <para>报告条目是否为可运行产物（<c>.abc</c>/<c>.an</c> 或位于 <c>libs/</c> 之下）。</para></summary>
    internal bool IsRunnable => HasExtension("abc") || HasExtension("an") || NameText.StartsWith("libs/", StringComparison.Ordinal);

    /// <summary>Reports whether the entry is the page bitmap (<c>.pages.info</c>).
    /// <para>报告条目是否为页位图（<c>.pages.info</c>）。</para></summary>
    internal bool IsBitmap => string.Equals(NameText, ".pages.info", StringComparison.Ordinal);

    private bool HasExtension(string extension)
    {
        var text = NameText;
        return text.Length > extension.Length + 1
            && text[text.Length - extension.Length - 1] == '.'
            && text.EndsWith(extension, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads one entry's local header, name, extra and raw payload; a trailing data descriptor is consumed and
    /// skipped. <para>读取一个条目的局部头、名称、extra 与原始载荷；尾部数据描述符被消费并跳过。</para>
    /// </summary>
    /// <param name="stream">Seekable source stream. <para>可定位的源流。</para></param>
    /// <param name="localOffset">Local header offset. <para>局部头偏移。</para></param>
    /// <exception cref="InvalidDataException">The local header disagrees with the central record or the payload is
    /// truncated. <para>局部头与中央记录不一致或载荷被截断。</para></exception>
    internal void ReadLocal(Stream stream, uint localOffset)
    {
        if (localOffset > int.MaxValue)
        {
            throw new InvalidDataException("HAP local header offset exceeds ZIP32. HAP 局部头偏移超出 ZIP32。");
        }

        LocalOffset = localOffset;

        Span<byte> header = stackalloc byte[LocalHeaderFixed];
        stream.Seek(localOffset, SeekOrigin.Begin);
        stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != LocalHeaderSignature)
        {
            throw new InvalidDataException("HAP local header signature is invalid. HAP 局部头签名无效。");
        }

        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[26..]);
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
        if (nameLength != Name.Length)
        {
            throw new InvalidDataException("HAP local and central entry names disagree. HAP 局部与中央条目名不一致。");
        }

        var localName = new byte[nameLength];
        stream.ReadExactly(localName);
        if (!localName.AsSpan().SequenceEqual(Name))
        {
            throw new InvalidDataException("HAP local entry name bytes disagree with the central record. HAP 局部条目名字节与中央记录不一致。");
        }

        var localExtra = new byte[extraLength];
        stream.ReadExactly(localExtra);

        var data = new byte[CompressedSize];
        stream.ReadExactly(data);
        if ((Flags & FlagDataDescriptor) != 0)
        {
            Span<byte> peek = stackalloc byte[4];
            stream.ReadExactly(peek);
            var descriptorLength = BinaryPrimitives.ReadUInt32LittleEndian(peek) == 0x08074b50 ? 16 : 12;
            stream.Seek(descriptorLength - 4, SeekOrigin.Current);
        }

        Data = data;
        LocalExtra = localExtra;
    }

    /// <summary>
    /// Applies the official alignment policy to an entry list in order: the first entry's data is page aligned,
    /// stored runnable/bitmap entries are page aligned, subsequent stored entries take 4-byte alignment, and the
    /// pass stops at the first deflated entry after the first one. Padding appends zero bytes to both the local and
    /// central extra fields. <para>按顺序对条目列表应用官方对齐策略：首条目数据页对齐，stored 的 runnable/bitmap
    /// 条目页对齐，后续 stored 条目 4 字节对齐，首条 deflated 条目之后即停止。填充以零字节追加到局部与中央 extra。</para>
    /// </summary>
    /// <param name="entries">Entries in output order. <para>按输出顺序的条目。</para></param>
    internal static void ApplyAlignment(IReadOnlyList<HapZipEntry> entries)
    {
        var currentOffset = 0L;
        var firstSeen = false;
        foreach (var entry in entries)
        {
            if (entry.Method != MethodStored && firstSeen)
            {
                break;
            }

            var alignment = (entry.IsRunnable && entry.Method == MethodStored) || entry.IsBitmap
                ? HapSignatureConstants.AlignmentPage
                : firstSeen
                    ? HapSignatureConstants.AlignmentNormal
                    : HapSignatureConstants.AlignmentPage;
            firstSeen = true;

            var dataStart = currentOffset + LocalHeaderFixed + entry.Name.Length + entry.LocalExtra.Length;
            var remainder = (int)(dataStart % alignment);
            var padding = remainder == 0 ? 0 : alignment - remainder;
            if (padding > 0)
            {
                var paddedLocal = new byte[entry.LocalExtra.Length + padding];
                entry.LocalExtra.CopyTo(paddedLocal, 0);
                entry.LocalExtra = paddedLocal;
                var paddedCentral = new byte[entry.CentralExtra.Length + padding];
                entry.CentralExtra.CopyTo(paddedCentral, 0);
                entry.CentralExtra = paddedCentral;
            }

            currentOffset += LocalHeaderFixed + entry.Name.Length + entry.LocalExtra.Length + entry.Data.Length;
        }
    }
}

/// <summary>
/// Reads a HAP container's entries and rebuilds the archive byte sections: aligned entries, a central directory
/// with patched local offsets, and the EOCD variants for digest computation and final output.
/// <para>读取 HAP 容器条目并重建档案字节区段：对齐后的条目、局部偏移已修补的中央目录，以及供摘要计算与最终
/// 输出使用的两种 EOCD。</para>
/// </summary>
internal static class HapZipArchive
{
    /// <summary>
    /// Reads every entry of the container in central-directory order; the stream position is preserved.
    /// <para>按中央目录顺序读取容器全部条目；流位置保持不变。</para>
    /// </summary>
    /// <param name="stream">Seekable source stream. <para>可定位的源流。</para></param>
    /// <param name="centralDirectoryOffset">Central directory start offset. <para>中央目录起始偏移。</para></param>
    /// <param name="centralDirectorySize">Central directory byte size. <para>中央目录字节大小。</para></param>
    /// <returns>Entries in central-directory order. <para>按中央目录顺序的条目。</para></returns>
    /// <exception cref="InvalidDataException">The container is truncated, ZIP64, or an entry escapes its bounds.
    /// <para>容器被截断、使用 ZIP64，或条目越界。</para></exception>
    internal static List<HapZipEntry> Read(Stream stream, long centralDirectoryOffset, long centralDirectorySize)
    {
        if (centralDirectoryOffset > int.MaxValue || centralDirectorySize > int.MaxValue)
        {
            throw new InvalidDataException("HAP containers with ZIP64 offsets are not supported. 不支持含 ZIP64 偏移的 HAP 容器。");
        }

        if (centralDirectorySize < 46 || centralDirectorySize > 512 << 20)
        {
            throw new InvalidDataException(
                $"HAP central directory size {centralDirectorySize} is out of range. HAP 中央目录大小 {centralDirectorySize} 越界。");
        }

        var original = stream.Position;
        try
        {
            var directory = new byte[centralDirectorySize];
            stream.Seek(centralDirectoryOffset, SeekOrigin.Begin);
            stream.ReadExactly(directory);

            var entries = new List<HapZipEntry>();
            var offset = 0;
            while (offset + 46 <= directory.Length)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(offset)) != 0x02014b50)
                {
                    throw new InvalidDataException("HAP central directory record signature is invalid. HAP 中央目录记录签名无效。");
                }

                var entry = new HapZipEntry
                {
                    VersionMadeBy = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(offset + 4)),
                    VersionNeeded = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(offset + 6)),
                    Flags = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(offset + 8)),
                    Method = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(offset + 10)),
                    DosTime = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(offset + 12)),
                    DosDate = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(offset + 14)),
                    Crc = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(offset + 16)),
                    CompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(offset + 20)),
                    UncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(offset + 24)),
                    DiskStart = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(offset + 34)),
                    InternalAttributes = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(offset + 36)),
                    ExternalAttributes = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(offset + 38)),
                };
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(offset + 28));
                var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(offset + 30));
                var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(offset + 32));
                var localOffset = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(offset + 42));
                var recordEnd = offset + 46 + nameLength + extraLength + commentLength;
                if (recordEnd > directory.Length)
                {
                    throw new InvalidDataException("HAP central directory record is truncated. HAP 中央目录记录被截断。");
                }

                entry.Name = directory[(offset + 46)..(offset + 46 + nameLength)];
                entry.CentralExtra = directory[(offset + 46 + nameLength)..(offset + 46 + nameLength + extraLength)];
                entry.Comment = directory[(offset + 46 + nameLength + extraLength)..recordEnd];
                entry.ReadLocal(stream, localOffset);
                entries.Add(entry);
                offset = recordEnd;
            }

            if (offset != directory.Length)
            {
                throw new InvalidDataException("HAP central directory has trailing bytes. HAP 中央目录末尾存在多余字节。");
            }

            return entries;
        }
        finally
        {
            stream.Seek(original, SeekOrigin.Begin);
        }
    }

    /// <summary>
    /// Builds the central directory bytes with each record's local header offset patched to the rebuilt position;
    /// the record order is preserved.
    /// <para>构建中央目录字节，各记录的局部头偏移修补为重建后的位置；记录顺序保持不变。</para>
    /// </summary>
    /// <param name="entries">Entries to serialize. <para>待序列化的条目。</para></param>
    /// <returns>Central directory bytes. <para>中央目录字节。</para></returns>
    internal static byte[] BuildCentralDirectory(IReadOnlyList<HapZipEntry> entries)
    {
        var size = 0;
        foreach (var entry in entries)
        {
            checked
            {
                size += 46 + entry.Name.Length + entry.CentralExtra.Length + entry.Comment.Length;
            }
        }

        var buffer = new byte[size];
        var offset = 0;
        var localOffset = 0L;
        foreach (var entry in entries)
        {
            entry.LocalOffset = localOffset;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), 0x02014b50);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 4), entry.VersionMadeBy);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 6), entry.VersionNeeded);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 8), entry.Flags);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 10), entry.Method);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 12), entry.DosTime);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 14), entry.DosDate);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 16), entry.Crc);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 20), entry.CompressedSize);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 24), entry.UncompressedSize);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 28), (ushort)entry.Name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 30), (ushort)entry.CentralExtra.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 32), (ushort)entry.Comment.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 34), entry.DiskStart);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 36), entry.InternalAttributes);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 38), entry.ExternalAttributes);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 42), (uint)localOffset);
            entry.Name.CopyTo(buffer, offset + 46);
            entry.CentralExtra.CopyTo(buffer, offset + 46 + entry.Name.Length);
            entry.Comment.CopyTo(buffer, offset + 46 + entry.Name.Length + entry.CentralExtra.Length);
            offset += 46 + entry.Name.Length + entry.CentralExtra.Length + entry.Comment.Length;
            localOffset += HapZipEntry.LocalHeaderFixed + entry.Name.Length + entry.LocalExtra.Length + entry.Data.Length;
        }

        return buffer;
    }

    /// <summary>
    /// Builds the 22-byte EOCD with the supplied central directory offset.
    /// <para>构建 22 字节 EOCD，使用给定的中央目录偏移。</para>
    /// </summary>
    /// <param name="centralDirectoryOffset">Value for the EOCD's CD offset field. <para>EOCD 的 CD 偏移字段值。</para></param>
    /// <param name="centralDirectorySize">Central directory size. <para>中央目录大小。</para></param>
    /// <returns>EOCD bytes. <para>EOCD 字节。</para></returns>
    internal static byte[] BuildEocd(long centralDirectoryOffset, int centralDirectorySize)
    {
        var eocd = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(eocd, 0x06054b50);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.AsSpan(8), (ushort)1);
        BinaryPrimitives.WriteUInt16LittleEndian(eocd.AsSpan(10), (ushort)1);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.AsSpan(12), (uint)centralDirectorySize);
        BinaryPrimitives.WriteUInt32LittleEndian(eocd.AsSpan(16), (uint)centralDirectoryOffset);
        return eocd;
    }
}