using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Cpio.Model;
using UotanStudio.Firmware.Cpio.Writing;

namespace UotanStudio.Firmware.Cpio.Formats.Cpio;

/// <summary>
/// Writes normalized archive entries to a CPIO stream.
/// <para>将规范化的归档条目写入 CPIO 流。</para>
/// </summary>
public sealed class CpioArchiveWriter
{
    /// <summary>
    /// Writes all provided entries to a CPIO stream in the selected format.
    /// <para>将提供的全部条目以所选格式写入 CPIO 流。</para>
    /// </summary>
    /// <param name="stream">Destination archive stream.<para>目标归档流。</para></param>
    /// <param name="entries">Entries to write.<para>待写入的条目。</para></param>
    /// <param name="format">Target CPIO format.<para>目标 CPIO 格式。</para></param>
    /// <param name="options">Optional write behavior options.<para>可选的写入行为选项。</para></param>
    public void WriteAll(Stream stream, IEnumerable<ArchiveEntry> entries, ArchiveFormat format, WriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entries);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("Output stream must be writable.", nameof(stream));
        }

        var opts = options ?? new WriteOptions();
        var normalizedEntries = NormalizeEntriesForFormat(entries, format);

        foreach (var entry in normalizedEntries)
        {
            switch (format)
            {
                case ArchiveFormat.NewAscii:
                case ArchiveFormat.CrcAscii:
                    WriteNewAsciiLike(stream, entry, format, opts);
                    break;
                case ArchiveFormat.OldAscii:
                case ArchiveFormat.HpOldAscii:
                    WriteOldAsciiLike(stream, entry, format, opts);
                    break;
                case ArchiveFormat.Binary:
                case ArchiveFormat.HpBinary:
                    WriteBinaryLike(stream, entry, format, opts);
                    break;
                case ArchiveFormat.Unknown:
                    break;
                case ArchiveFormat.Tar:
                    break;
                case ArchiveFormat.Ustar:
                    break;
                default:
                    throw new NotSupportedException($"Unsupported CPIO writer format: {format}");
            }
        }

        if (opts.IncludeTrailer)
        {
            var trailer = new ArchiveEntry
            {
                Name = CpioConstants.TrailerName,
                Data = [],
                Metadata = new ArchiveEntryMetadata
                {
                    FileType = CpioFileType.Regular,
                    LinkCount = 1,
                },
            };

            switch (format)
            {
                case ArchiveFormat.NewAscii:
                case ArchiveFormat.CrcAscii:
                    WriteNewAsciiLike(stream, trailer, format, opts);
                    break;
                case ArchiveFormat.OldAscii:
                case ArchiveFormat.HpOldAscii:
                    WriteOldAsciiLike(stream, trailer, format, opts);
                    break;
                case ArchiveFormat.Binary:
                case ArchiveFormat.HpBinary:
                    WriteBinaryLike(stream, trailer, format, opts);
                    break;
                case ArchiveFormat.Unknown:
                    break;
                case ArchiveFormat.Tar:
                    break;
                case ArchiveFormat.Ustar:
                    break;
                default:
                    break;
            }
        }
    }

    private static IReadOnlyList<ArchiveEntry> NormalizeEntriesForFormat(IEnumerable<ArchiveEntry> entries, ArchiveFormat format)
    {
        var materialized = entries.ToList();
        if (format is not (ArchiveFormat.NewAscii or ArchiveFormat.CrcAscii))
        {
            return materialized;
        }

        var result = new List<ArchiveEntry>(materialized.Count);
        foreach (var entry in materialized)
        {
            result.Add(entry);
        }

        // Collapse duplicate inode regular files into a single data carrier
        // so hardlink groups are serialized without duplicated payloads.
        var groups = new Dictionary<(ulong Inode, uint DevMaj, uint DevMin), List<int>>();
        for (var i = 0; i < result.Count; i++)
        {
            var entry = result[i];
            if (entry.Metadata.FileType != CpioFileType.Regular || entry.Metadata.LinkCount <= 1 || entry.Metadata.Inode == 0)
            {
                continue;
            }

            var key = (entry.Metadata.Inode, entry.Metadata.DeviceMajor, entry.Metadata.DeviceMinor);
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }

            list.Add(i);
        }

        foreach (var (_, indexes) in groups)
        {
            if (indexes.Count <= 1)
            {
                continue;
            }

            var dataCarrierIndex = indexes[^1];
            for (var i = indexes.Count - 1; i >= 0; i--)
            {
                var idx = indexes[i];
                if (result[idx].Data.Length > 0)
                {
                    dataCarrierIndex = idx;
                    break;
                }
            }

            foreach (var idx in indexes)
            {
                if (idx == dataCarrierIndex)
                {
                    continue;
                }

                var original = result[idx];
                result[idx] = new ArchiveEntry
                {
                    Name = original.Name,
                    LinkName = null,
                    Data = [],
                    Metadata = original.Metadata,
                };
            }
        }

        return result;
    }

    private static void WriteNewAsciiLike(Stream stream, ArchiveEntry entry, ArchiveFormat format, WriteOptions options)
    {
        var nameBytes = Encoding.UTF8.GetBytes(entry.Name);
        var payload = ResolvePayload(entry, options);
        uint checksum = 0;
        if (format == ArchiveFormat.CrcAscii)
        {
            foreach (var b in payload)
            {
                checksum += b;
            }
        }

        var header = string.Concat(
            format == ArchiveFormat.CrcAscii ? CpioConstants.CrcAsciiMagic : CpioConstants.NewAsciiMagic,
            Hex8(entry.Metadata.Inode),
            Hex8(entry.Metadata.Mode),
            Hex8(entry.Metadata.UserId),
            Hex8(entry.Metadata.GroupId),
            Hex8(entry.Metadata.LinkCount == 0 ? 1UL : entry.Metadata.LinkCount),
            Hex8((ulong)Math.Max(0, entry.Metadata.ModificationTimeUnixSeconds)),
            Hex8((ulong)payload.Length),
            Hex8(entry.Metadata.DeviceMajor),
            Hex8(entry.Metadata.DeviceMinor),
            Hex8(entry.Metadata.RDeviceMajor),
            Hex8(entry.Metadata.RDeviceMinor),
            Hex8((ulong)nameBytes.Length + 1UL),
            Hex8(checksum));

        var headerBytes = Encoding.ASCII.GetBytes(header);
        stream.Write(headerBytes);
        stream.Write(nameBytes);
        stream.WriteByte(0);
        WritePadding(stream, CpioConstants.NewAsciiHeaderSize + nameBytes.Length + 1, 4);

        stream.Write(payload);
        WritePadding(stream, payload.Length, 4);
    }

    private static void WriteOldAsciiLike(Stream stream, ArchiveEntry entry, ArchiveFormat format, WriteOptions options)
    {
        var nameBytes = Encoding.UTF8.GetBytes(entry.Name);
        var payload = ResolvePayload(entry, options);

        var device = entry.Metadata.DeviceMajor & 0x3FFFFU;
        var rdev = entry.Metadata.RDeviceMajor & 0x3FFFFU;
        var mtime = Math.Max(0, entry.Metadata.ModificationTimeUnixSeconds);
        var fileSize = (long)payload.Length;
        if (format == ArchiveFormat.HpOldAscii && (entry.Metadata.FileType is CpioFileType.CharacterDevice or CpioFileType.BlockDevice))
        {
            // HP old ASCII stores special device identifiers in the filesize field.
            fileSize = ((long)(entry.Metadata.RDeviceMajor & 0xFFFFU) << 16) | (entry.Metadata.RDeviceMinor & 0xFFFFU);
            rdev = 0;
        }

        var header = string.Concat(
            CpioConstants.OldAsciiMagic,
            Oct6(device),
            Oct6(entry.Metadata.Inode & 0x3FFFFUL),
            Oct6(entry.Metadata.Mode & 0x3FFFFU),
            Oct6(entry.Metadata.UserId & 0x3FFFFU),
            Oct6(entry.Metadata.GroupId & 0x3FFFFU),
            Oct6((entry.Metadata.LinkCount == 0 ? 1UL : entry.Metadata.LinkCount) & 0x3FFFFUL),
            Oct6(rdev),
            Oct11((ulong)mtime),
            Oct6((ulong)nameBytes.Length + 1UL),
            Oct11((ulong)Math.Max(fileSize, 0)));

        var headerBytes = Encoding.ASCII.GetBytes(header);
        stream.Write(headerBytes);
        stream.Write(nameBytes);
        stream.WriteByte(0);

        // odc uses no alignment padding.
        stream.Write(payload);
    }

    private static void WriteBinaryLike(Stream stream, ArchiveEntry entry, ArchiveFormat format, WriteOptions options)
    {
        var nameBytes = Encoding.UTF8.GetBytes(entry.Name);
        var payload = ResolvePayload(entry, options);

        Span<byte> header = stackalloc byte[CpioConstants.OldBinaryHeaderSize];
        WriteUInt16BigEndian(header[..2], CpioConstants.BinaryMagic);
        WriteUInt16BigEndian(header.Slice(2, 2), (ushort)(entry.Metadata.DeviceMajor & 0xFFFF));
        WriteUInt16BigEndian(header.Slice(4, 2), (ushort)(entry.Metadata.Inode & 0xFFFF));
        WriteUInt16BigEndian(header.Slice(6, 2), (ushort)(entry.Metadata.Mode & 0xFFFF));
        WriteUInt16BigEndian(header.Slice(8, 2), (ushort)(entry.Metadata.UserId & 0xFFFF));
        WriteUInt16BigEndian(header.Slice(10, 2), (ushort)(entry.Metadata.GroupId & 0xFFFF));
        WriteUInt16BigEndian(header.Slice(12, 2), (ushort)((entry.Metadata.LinkCount == 0 ? 1UL : entry.Metadata.LinkCount) & 0xFFFF));
        WriteUInt16BigEndian(header.Slice(14, 2), (ushort)(entry.Metadata.RDeviceMajor & 0xFFFF));

        var mtime = (uint)Math.Max(0, entry.Metadata.ModificationTimeUnixSeconds);
        WriteUInt16BigEndian(header.Slice(16, 2), (ushort)(mtime >> 16));
        WriteUInt16BigEndian(header.Slice(18, 2), (ushort)(mtime & 0xFFFF));
        WriteUInt16BigEndian(header.Slice(20, 2), (ushort)(nameBytes.Length + 1));

        var size = (uint)payload.Length;
        if (format == ArchiveFormat.HpBinary && (entry.Metadata.FileType is CpioFileType.CharacterDevice or CpioFileType.BlockDevice))
        {
            // HP binary stores the device identifier in the filesize words for special files.
            size = ((entry.Metadata.RDeviceMajor & 0xFFFFU) << 16) | (entry.Metadata.RDeviceMinor & 0xFFFFU);
        }

        WriteUInt16BigEndian(header.Slice(22, 2), (ushort)(size >> 16));
        WriteUInt16BigEndian(header.Slice(24, 2), (ushort)(size & 0xFFFF));

        stream.Write(header);
        stream.Write(nameBytes);
        stream.WriteByte(0);
        WritePadding(stream, CpioConstants.OldBinaryHeaderSize + nameBytes.Length + 1, 2);
        stream.Write(payload);
        WritePadding(stream, payload.Length, 2);
    }

    private static byte[] ResolvePayload(ArchiveEntry entry, WriteOptions options)
    {
        return options.CpioSymlinkAsDataPayload && entry.Metadata.FileType == CpioFileType.SymbolicLink
            ? Encoding.UTF8.GetBytes(entry.LinkName ?? string.Empty)
            : entry.Data;
    }

    private static void WritePadding(Stream stream, int payloadLength, int align)
    {
        if (align <= 1)
        {
            return;
        }

        var pad = (align - (payloadLength % align)) % align;
        for (var i = 0; i < pad; i++)
        {
            stream.WriteByte(0);
        }
    }

    private static void WriteUInt16BigEndian(Span<byte> target, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(target, value);

    private static string Hex8(ulong value) => value.ToString("X8");

    private static string Hex8(uint value) => value.ToString("X8");

    private static string Oct6(ulong value) => Convert.ToString((long)value, 8).PadLeft(6, '0')[^6..];

    private static string Oct6(uint value) => Oct6((ulong)value);

    private static string Oct11(ulong value) => Convert.ToString((long)value, 8).PadLeft(11, '0')[^11..];
}