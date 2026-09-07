using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.SharedKernel.Streams;
using UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Model;
using UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Writing;

namespace UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Formats.Cpio;

public sealed class CpioArchiveWriter
{
    /// <summary>
    /// Writes all provided entries to a CPIO stream in the selected format.
    /// <para>将提供的全部条目以所选格式写入 CPIO 流。</para>
    /// </summary>
    public void WriteAll(Stream stream, IEnumerable<ArchiveEntry> entries, ArchiveFormat format, WriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entries);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("Output stream must be writable.", nameof(stream));
        }

        var opts = options ?? new WriteOptions();
        var writtenHardlinks = new HashSet<(ulong Inode, uint DevMaj, uint DevMin)>();
        var directStreams = new HashSet<Stream>(ReferenceEqualityComparer.Instance);
        Exception? primary = null;
        try
        {


            foreach (var yieldedEntry in entries)
            {
                try
                {
                    if (yieldedEntry.DataStream is { } direct) directStreams.Add(direct);

                    var entry = NormalizeHardlinkOnline(yieldedEntry, format, writtenHardlinks);
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
                        case ArchiveFormat.Tar:
                        case ArchiveFormat.Ustar:
                            break;
                        default:
                            throw new NotSupportedException($"Unsupported CPIO writer format: {format}");
                    }
                }
                catch (Exception ex)
                {
                    primary = ex;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            primary ??= ex;
        }
        finally
        {
            foreach (var direct in directStreams)
            {
                try { direct.Dispose(); }
                catch (Exception ex) { primary = primary is null ? ex : new AggregateException(primary, ex); }
            }
        }

        if (primary is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
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

    private static ArchiveEntry NormalizeHardlinkOnline(
        ArchiveEntry entry,
        ArchiveFormat format,
        HashSet<(ulong Inode, uint DevMaj, uint DevMin)> writtenHardlinks)
    {
        if (format is not (ArchiveFormat.NewAscii or ArchiveFormat.CrcAscii)
            || entry.Metadata.FileType != CpioFileType.Regular
            || entry.Metadata.LinkCount <= 1
            || entry.Metadata.Inode == 0)
        {
            return entry;
        }

        var key = (entry.Metadata.Inode, entry.Metadata.DeviceMajor, entry.Metadata.DeviceMinor);


        if (!writtenHardlinks.Contains(key))
        {
            if (entry.DataLength > 0) writtenHardlinks.Add(key);
            return entry;
        }
        return new ArchiveEntry
        {
            Name = entry.Name,
            LinkName = null,
            Data = [],
            DataStream = null,
            DataStreamFactory = null,
            DeclaredDataLength = 0,
            Metadata = entry.Metadata,
        };
    }

    private static void WriteNewAsciiLike(Stream stream, ArchiveEntry entry, ArchiveFormat format, WriteOptions options)
    {
        var nameBytes = Encoding.UTF8.GetBytes(entry.Name);
        var payloadLength = ResolvePayloadLength(entry, options);
        ValidatePayloadLength(entry, payloadLength, uint.MaxValue, format);

        FileStream? spool = null;
        try
        {
            if (format == ArchiveFormat.CrcAscii
                && !(options.CpioSymlinkAsDataPayload && entry.Metadata.FileType == CpioFileType.SymbolicLink)
                && (entry.DataStream is { CanSeek: false } || entry.DataStreamFactory is not null))
            {
                if (entry.DataStreamFactory is { } factory)
                {
                    using var source = factory() ?? throw new InvalidOperationException($"Entry '{entry.Name}' stream factory returned null.");
                    spool = CreatePayloadSpool(source, payloadLength, entry.Name, probeEof: true);
                }
                else
                {
                    spool = CreatePayloadSpool(entry.DataStream!, payloadLength, entry.Name, probeEof: false);
                }
                entry = CloneWithStream(entry, spool, payloadLength);
            }

            var checksum = format == ArchiveFormat.CrcAscii
                ? ComputePayloadChecksum(entry, options, payloadLength)
                : 0U;

            var header = FormatNewcHeader(
                format == ArchiveFormat.CrcAscii ? CpioConstants.CrcAsciiMagic : CpioConstants.NewAsciiMagic,
                entry.Metadata.Inode,
                entry.Metadata.Mode,
                entry.Metadata.UserId,
                entry.Metadata.GroupId,
                entry.Metadata.LinkCount == 0 ? 1UL : entry.Metadata.LinkCount,
                (ulong)Math.Max(0, entry.Metadata.ModificationTimeUnixSeconds),
                (ulong)payloadLength,
                entry.Metadata.DeviceMajor,
                entry.Metadata.DeviceMinor,
                entry.Metadata.RDeviceMajor,
                entry.Metadata.RDeviceMinor,
                (ulong)nameBytes.Length + 1UL,
                checksum);

            var headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes);
            stream.Write(nameBytes);
            stream.WriteByte(0);
            WritePadding(stream, CpioConstants.NewAsciiHeaderSize + nameBytes.Length + 1, 4);

            WritePayload(stream, entry, options, payloadLength);
            WritePadding(stream, payloadLength, 4);
        }
        finally
        {
            if (spool is not null)
            {
                var path = spool.Name;
                try
                {
                    spool.Dispose();
                }
                finally
                {
                    try { File.Delete(path); } catch {  }
                }
            }
        }
    }

    private static void WriteOldAsciiLike(Stream stream, ArchiveEntry entry, ArchiveFormat format, WriteOptions options)
    {
        var nameBytes = Encoding.UTF8.GetBytes(entry.Name);
        var payloadLength = ResolvePayloadLength(entry, options);
        ValidatePayloadLength(entry, payloadLength, 0x1FFFFFFFFL, format);

        var device = entry.Metadata.DeviceMajor & 0x3FFFFU;
        var rdev = entry.Metadata.RDeviceMajor & 0x3FFFFU;
        var mtime = Math.Max(0, entry.Metadata.ModificationTimeUnixSeconds);
        var hpSpecial = format == ArchiveFormat.HpOldAscii
            && entry.Metadata.FileType is CpioFileType.CharacterDevice or CpioFileType.BlockDevice;
        if (hpSpecial && payloadLength != 0)
        {
            throw new InvalidDataException($"HP special-device entry '{entry.Name}' cannot contain payload data.");
        }

        var fileSize = hpSpecial
            ? ((long)(entry.Metadata.RDeviceMajor & 0xFFFFU) << 16) | (entry.Metadata.RDeviceMinor & 0xFFFFU)
            : payloadLength;
        if (hpSpecial) rdev = 0;

        var header = FormatOldcHeader(
            device,
            entry.Metadata.Inode & 0x3FFFFUL,
            entry.Metadata.Mode & 0x3FFFFU,
            entry.Metadata.UserId & 0x3FFFFU,
            entry.Metadata.GroupId & 0x3FFFFU,
            (entry.Metadata.LinkCount == 0 ? 1UL : entry.Metadata.LinkCount) & 0x3FFFFUL,
            rdev,
            (ulong)mtime,
            (ulong)nameBytes.Length + 1UL,
            (ulong)Math.Max(fileSize, 0));

        var headerBytes = Encoding.ASCII.GetBytes(header);
        stream.Write(headerBytes);
        stream.Write(nameBytes);
        stream.WriteByte(0);


        WritePayload(stream, entry, options, payloadLength);
    }

    private static void WriteBinaryLike(Stream stream, ArchiveEntry entry, ArchiveFormat format, WriteOptions options)
    {
        var nameBytes = Encoding.UTF8.GetBytes(entry.Name);
        var payloadLength = ResolvePayloadLength(entry, options);
        ValidatePayloadLength(entry, payloadLength, uint.MaxValue, format);

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

        var hpSpecial = format == ArchiveFormat.HpBinary
            && entry.Metadata.FileType is CpioFileType.CharacterDevice or CpioFileType.BlockDevice;
        if (hpSpecial && payloadLength != 0)
        {
            throw new InvalidDataException($"HP special-device entry '{entry.Name}' cannot contain payload data.");
        }

        var size = hpSpecial
            ? ((entry.Metadata.RDeviceMajor & 0xFFFFU) << 16) | (entry.Metadata.RDeviceMinor & 0xFFFFU)
            : (uint)payloadLength;

        WriteUInt16BigEndian(header.Slice(22, 2), (ushort)(size >> 16));
        WriteUInt16BigEndian(header.Slice(24, 2), (ushort)(size & 0xFFFF));

        stream.Write(header);
        stream.Write(nameBytes);
        stream.WriteByte(0);
        WritePadding(stream, CpioConstants.OldBinaryHeaderSize + nameBytes.Length + 1, 2);
        WritePayload(stream, entry, options, payloadLength);
        WritePadding(stream, payloadLength, 2);
    }

    /// <summary>
    /// Resolves the payload length in bytes, preferring the streaming source when present.
    /// <para>解析负载字节长度；存在流式源时优先取其长度。</para>
    /// </summary>
    private static long ResolvePayloadLength(ArchiveEntry entry, WriteOptions options)
    {
        if (options.CpioSymlinkAsDataPayload && entry.Metadata.FileType == CpioFileType.SymbolicLink)
        {
            return Encoding.UTF8.GetByteCount(entry.LinkName ?? string.Empty);
        }

        return entry.DataLength;
    }

    /// <summary>
    /// Computes the CRC ASCII payload checksum (sum of all payload bytes) without materializing the payload when a seekable streaming source is available.
    /// <para>在不物化负载的情况下计算 CRC ASCII 负载校验和（所有负载字节之和）； 存在可寻址流式源时直接读取计算。</para>
    /// </summary>
    private static uint ComputePayloadChecksum(ArchiveEntry entry, WriteOptions options, long payloadLength)
    {
        if (options.CpioSymlinkAsDataPayload && entry.Metadata.FileType == CpioFileType.SymbolicLink)
        {
            uint total = 0;
            foreach (var b in Encoding.UTF8.GetBytes(entry.LinkName ?? string.Empty))
            {
                total += b;
            }
            return total;
        }

        if (entry.DataStream is { } stream)
        {
            if (!stream.CanSeek)
            {
                throw new InvalidOperationException($"CRC CPIO entry '{entry.Name}' uses a single non-seekable stream; provide DataStreamFactory so checksum and payload can be read independently.");
            }

            stream.Position = 0;
            var total = SumExactly(stream, payloadLength, entry.Name);
            stream.Position = 0;
            return total;
        }

        if (entry.DataStreamFactory is { } factory)
        {
            using var source = factory() ?? throw new InvalidOperationException($"Entry '{entry.Name}' stream factory returned null.");
            return SumExactly(source, payloadLength, entry.Name);
        }

        uint sum = 0;
        for (long i = 0; i < payloadLength; i++)
        {
            sum += entry.Data[checked((int)i)];
        }
        return sum;
    }

    private static uint SumExactly(Stream source, long length, string entryName)
    {
        uint total = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                var read = source.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                if (read == 0)
                {
                    throw new EndOfStreamException($"Entry '{entryName}' ended before its declared length.");
                }
                for (var i = 0; i < read; i++)
                {
                    total += buffer[i];
                }
                remaining -= read;
            }
            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Writes the entry payload, streaming from ArchiveEntry.DataStream when present and falling back to ArchiveEntry.Data otherwise.
    /// <para>写入条目负载；存在 DataStream 时流式拷贝，否则回退到 Data。</para>
    /// </summary>
    private static void WritePayload(Stream stream, ArchiveEntry entry, WriteOptions options, long payloadLength)
    {
        if (options.CpioSymlinkAsDataPayload && entry.Metadata.FileType == CpioFileType.SymbolicLink)
        {
            var bytes = Encoding.UTF8.GetBytes(entry.LinkName ?? string.Empty);
            stream.Write(bytes);
            return;
        }

        if (entry.DataStream is { } dataStream)
        {
            if (dataStream.CanSeek)
            {
                dataStream.Position = 0;
            }
            StreamCopy.CopyExactly(dataStream, stream, payloadLength);
            return;
        }

        if (entry.DataStreamFactory is { } factory)
        {
            using var source = factory() ?? throw new InvalidOperationException($"Entry '{entry.Name}' stream factory returned null.");
            StreamCopy.CopyExactly(source, stream, payloadLength);
            if (source.ReadByte() != -1)
            {
                throw new InvalidDataException($"Entry '{entry.Name}' source contains data beyond its declared length {payloadLength}.");
            }
            return;
        }

        stream.Write(entry.Data, 0, checked((int)payloadLength));
    }

    private static void ValidatePayloadLength(ArchiveEntry entry, long payloadLength, long maximum, ArchiveFormat format)
    {
        if (entry.DataStream is null && entry.DataStreamFactory is null
            && entry.DeclaredDataLength.HasValue && payloadLength != entry.Data.LongLength)
        {
            throw new InvalidDataException($"Entry '{entry.Name}' declared length {payloadLength} does not match its data length {entry.Data.LongLength}.");
        }
        if (payloadLength < 0)
        {
            throw new InvalidDataException($"Entry '{entry.Name}' has a negative payload length.");
        }
        if (payloadLength > maximum)
        {
            throw new InvalidDataException($"Entry '{entry.Name}' payload length {payloadLength} cannot be represented by {format}.");
        }
    }

    private static FileStream CreatePayloadSpool(Stream source, long length, string entryName, bool probeEof)
    {
        var path = Path.Combine(Path.GetTempPath(), $"uotan-cpio-{Guid.NewGuid():N}.tmp");
        try
        {
            var spool = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.SequentialScan | FileOptions.DeleteOnClose);
            try
            {
                StreamCopy.CopyExactly(source, spool, length);
                if (probeEof && source.ReadByte() != -1)
                {
                    throw new InvalidDataException($"Entry '{entryName}' source contains data beyond its declared length {length}.");
                }
                spool.Position = 0;
                return spool;
            }
            catch
            {
                spool.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            try { File.Delete(path); } catch {  }
            throw new InvalidDataException($"Unable to spool CRC payload for entry '{entryName}'.", ex);
        }
    }

    private static ArchiveEntry CloneWithStream(ArchiveEntry entry, Stream stream, long length) => new()
    {
        Name = entry.Name,
        LinkName = entry.LinkName,
        Data = entry.Data,
        DataStream = stream,
        DataStreamFactory = null,
        DeclaredDataLength = length,
        Metadata = entry.Metadata,
    };

    private static void WritePadding(Stream stream, long payloadLength, int align)
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

    /// <summary>
    /// Formats a newc/crc CPIO header (110 ASCII hex chars) with a single string allocation.
    /// <para>使用单次字符串分配格式化 newc/crc CPIO 头（110 个 ASCII 十六进制字符）。</para>
    /// </summary>
    private static string FormatNewcHeader(
        string magic, ulong inode, uint mode, uint uid, uint gid,
        ulong nlink, ulong mtime, ulong filesize,
        uint devmajor, uint devminor, uint rdevmajor, uint rdevminor,
        ulong namesize, uint checksum)
    {
        return string.Create(CpioConstants.NewAsciiHeaderSize,
            (magic, inode, mode, uid, gid, nlink, mtime, filesize,
             devmajor, devminor, rdevmajor, rdevminor, namesize, checksum),
            static (dest, s) =>
            {
                s.magic.AsSpan().CopyTo(dest);
                s.inode.TryFormat(dest[6..], out _, "X8");
                s.mode.TryFormat(dest[14..], out _, "X8");
                s.uid.TryFormat(dest[22..], out _, "X8");
                s.gid.TryFormat(dest[30..], out _, "X8");
                ((uint)s.nlink).TryFormat(dest[38..], out _, "X8");
                ((uint)s.mtime).TryFormat(dest[46..], out _, "X8");
                ((uint)s.filesize).TryFormat(dest[54..], out _, "X8");
                s.devmajor.TryFormat(dest[62..], out _, "X8");
                s.devminor.TryFormat(dest[70..], out _, "X8");
                s.rdevmajor.TryFormat(dest[78..], out _, "X8");
                s.rdevminor.TryFormat(dest[86..], out _, "X8");
                ((uint)s.namesize).TryFormat(dest[94..], out _, "X8");
                s.checksum.TryFormat(dest[102..], out _, "X8");
            });
    }

    /// <summary>
    /// Formats an oldc CPIO header (76 ASCII octal chars) with a single string allocation.
    /// <para>使用单次字符串分配格式化 oldc CPIO 头（76 个 ASCII 八进制字符）。</para>
    /// </summary>
    private static string FormatOldcHeader(
        ulong device, ulong inode, ulong mode, ulong uid, ulong gid,
        ulong nlink, ulong rdev, ulong mtime, ulong namesize, ulong filesize)
    {
        return string.Create(CpioConstants.OldAsciiHeaderSize,
            (device, inode, mode, uid, gid, nlink, rdev, mtime, namesize, filesize),
            static (dest, s) =>
            {
                "070707".AsSpan().CopyTo(dest);
                FormatOctal(dest[6..], s.device, 6);
                FormatOctal(dest[12..], s.inode, 6);
                FormatOctal(dest[18..], s.mode, 6);
                FormatOctal(dest[24..], s.uid, 6);
                FormatOctal(dest[30..], s.gid, 6);
                FormatOctal(dest[36..], s.nlink, 6);
                FormatOctal(dest[42..], s.rdev, 6);
                FormatOctal(dest[48..], s.mtime, 11);
                FormatOctal(dest[59..], s.namesize, 6);
                FormatOctal(dest[65..], s.filesize, 11);
            });
    }

    /// <summary>
    /// Writes a zero-padded octal representation of value into dest.
    /// <para>将 value 的零填充八进制表示写入 dest。</para>
    /// </summary>
    private static void FormatOctal(Span<char> dest, ulong value, int width)
    {
        for (int i = width - 1; i >= 0; i--)
        {
            dest[i] = (char)('0' + (value & 7));
            value >>= 3;
        }
    }
}
