using System.Buffers.Binary;

namespace UotanStudio.Firmware.VendorFirmware.MediaTek;

/// <summary>
/// Bounded metadata-only reader for MTK all-in-one download-agent containers.
/// <para>MTK all-in-one 下载代理容器的有界、仅元数据读取器。</para>
/// </summary>
internal static class MtkDaContainerReader
{
    /// <summary>Reads metadata from the beginning of a seekable stream and restores its original position. Region data and signatures are not read or cryptographically verified.
    /// <para>从可寻址流的起始位置读取元数据，并恢复其原始位置。不读取区域数据或签名，也不做密码学校验。</para></summary>
    /// <param name="source">The caller-owned readable, seekable container stream.
    /// <para>调用方拥有的、可读且可寻址的容器流。</para></param>
    /// <param name="layout">An explicit layout, or unambiguous structural detection.
    /// <para>显式布局，或要求无歧义的结构探测。</para></param>
    /// <param name="cancellationToken">The operation cancellation token.<para>本次操作的取消令牌。</para></param>
    /// <returns>Validated metadata with at most 4096 entries and ten regions per entry.
    /// <para>经过校验的元数据；条目至多 4096 个，每个条目至多十个区域。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.
    /// <para><paramref name="source"/> 为 null。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> cannot be read or cannot be seeked.
    /// <para><paramref name="source"/> 不可读或不可寻址。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="layout"/> is not a defined value.
    /// <para><paramref name="layout"/> 不是已定义的值。</para></exception>
    /// <exception cref="InvalidDataException">The header is truncated or unrecognized, the entry count is out of range, a directory or region fails structural validation, or automatic layout detection is ambiguous or finds no passing layout.
    /// <para>头部被截断或无法识别、条目数量越界、目录或区域未通过结构校验，或自动布局探测存在歧义、无任何布局通过校验。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before or during directory parsing.
    /// <para>在目录解析之前或期间已请求取消。</para></exception>
    /// <example>
    /// <code>
    /// await using var file = File.OpenRead(@"C:\images\DA_all_in_one.bin");
    /// var container = MtkDaContainerReader.Read(file, MtkDaDirectoryLayout.Auto);
    /// </code>
    /// </example>
    public static MtkDaContainer Read(Stream source, MtkDaDirectoryLayout layout = MtkDaDirectoryLayout.Auto,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException("DA metadata requires a readable, seekable container.", nameof(source));
        }
        if (!Enum.IsDefined(layout))
        {
            throw new ArgumentOutOfRangeException(nameof(layout));
        }
        var originalPosition = source.Position;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.Length < 108)
            {
                throw new InvalidDataException("The DA container header is truncated.");
            }
            source.Position = 0;
            Span<byte> header = stackalloc byte[108];
            source.ReadExactly(header);
            if (!header.StartsWith("MTK_DOWNLOAD_AGENT"u8)
                || BinaryPrimitives.ReadUInt32LittleEndian(header[100..]) != 0x22668899)
            {
                throw new InvalidDataException("The DA container header is not recognized.");
            }
            var count = BinaryPrimitives.ReadUInt32LittleEndian(header[104..]);
            if (count is 0 or > 4096)
            {
                throw new InvalidDataException("The DA entry count is empty or exceeds the metadata limit.");
            }
            var v6 = header[..104].IndexOf("MTK_DA_v6"u8) >= 0;
            if (layout != MtkDaDirectoryLayout.Auto)
            {
                return ReadDirectory(source, layout, count, v6, cancellationToken);
            }
            MtkDaContainer? candidate = null;
            foreach (var alternative in new[] { MtkDaDirectoryLayout.Legacy, MtkDaDirectoryLayout.Modern })
            {
                MtkDaContainer parsed;
                try
                {
                    parsed = ReadDirectory(source, alternative, count, v6, cancellationToken);
                }
                catch (InvalidDataException)
                {
                    continue;
                }
                if (candidate is not null)
                {
                    throw new InvalidDataException("The DA directory layout is ambiguous; specify its layout explicitly.");
                }
                candidate = parsed;
            }
            return candidate ?? throw new InvalidDataException("No supported DA directory layout passed structural validation.");
        }
        finally
        {
            source.Position = originalPosition;
        }
    }

    private static MtkDaContainer ReadDirectory(Stream source, MtkDaDirectoryLayout layout, uint count,
        bool v6, CancellationToken cancellationToken)
    {
        var legacy = layout == MtkDaDirectoryLayout.Legacy;
        var stride = legacy ? 216 : 220;
        var fixedLength = legacy ? 16 : 20;
        var directoryEnd = checked(108L + count * stride);
        if (directoryEnd > source.Length)
        {
            throw new InvalidDataException("The DA directory exceeds the container.");
        }
        var entries = new MtkDaEntry[(int)count];
        var record = new byte[stride];
        source.Position = 108;
        for (var index = 0; index < entries.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            source.ReadExactly(record);
            if (U16(record, 0) != 0xDADA)
            {
                throw new InvalidDataException("A DA directory entry has invalid magic.");
            }
            var entryRegion = U16(record, fixedLength - 4);
            var regionCount = U16(record, fixedLength - 2);
            if (regionCount is 0 or > 10 || entryRegion >= regionCount)
            {
                throw new InvalidDataException("A DA entry has an invalid region count or entry-region index.");
            }
            var regions = new MtkDaRegion[regionCount];
            for (var regionIndex = 0; regionIndex < regions.Length; regionIndex++)
            {
                var data = record.AsSpan(fixedLength + regionIndex * 20, 20);
                var offset = BinaryPrimitives.ReadUInt32LittleEndian(data);
                var length = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
                var address = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
                var start = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
                var signature = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
                if ((long)offset + length > source.Length || signature > length
                    || (length != 0 && offset < directoryEnd))
                {
                    throw new InvalidDataException("A DA region overlaps metadata, exceeds its container or has an invalid signature length.");
                }
                regions[regionIndex] = new MtkDaRegion(offset, length, address, start, signature);
            }
            entries[index] = new MtkDaEntry(U16(record, 2), U16(record, 4), U16(record, 6),
                legacy ? null : U16(record, 8), U16(record, fixedLength - 8), entryRegion, Array.AsReadOnly(regions));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new MtkDaContainer(layout, v6, Array.AsReadOnly(entries));
    }

    private static ushort U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
}