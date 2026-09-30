using System.Formats.Tar;
using System.Text;
using UotanStudio.Firmware.Archive.Models;
using UotanStudio.Firmware.Core.Streams;

namespace UotanStudio.Firmware.Archive.Formats.Tar;

/// <summary>
/// Serializes archive entries into a TAR stream as PAX or Ustar records.
/// <para>将归档条目以 PAX 或 Ustar 记录形式序列化到 TAR 流。</para>
/// </summary>
public sealed class TarArchiveWriter
{
    /// <summary>
    /// Writes all supplied entries to a TAR stream.
    /// <para>将所有提供的条目写入 TAR 流。</para>
    /// </summary>
    /// <param name="stream">
    /// Writable destination kept open by the writer.
    /// <para>可写目标流，写入器不会将其关闭。</para>
    /// </param>
    /// <param name="entries">
    /// Entries to serialize; any entry data stream is disposed once consumed or on failure.
    /// <para>待序列化的条目；条目的数据流在被消费或失败时会被释放。</para>
    /// </param>
    /// <param name="format">
    /// <see cref="ArchiveFormat.Tar"/> selects PAX records with extended attributes; any other value selects Ustar.
    /// <para><see cref="ArchiveFormat.Tar"/> 选择带扩展属性的 PAX 记录；其他值选择 Ustar。</para>
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="stream"/> or <paramref name="entries"/> is <see langword="null"/>. <para><paramref name="stream"/> 或 <paramref name="entries"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArchiveCorruptedException">
    /// A declared length contradicts the entry data, or a source carries bytes beyond its declared length.
    /// <para>声明长度与条目数据不符，或源数据超出声明长度。</para>
    /// </exception>
    public void WriteAll(Stream stream, IEnumerable<ArchiveEntry> entries, ArchiveFormat format)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entries);


        var directStreams = new HashSet<Stream>(ReferenceEqualityComparer.Instance);
        Exception? primary = null;
        try
        {
            using var writer = new TarWriter(stream, format == ArchiveFormat.Tar ? TarEntryFormat.Pax : TarEntryFormat.Ustar, leaveOpen: true);
            foreach (var entry in entries)
            {
                Stream? factoryStream = null;
                var probeFactoryEof = false;
                LengthLimitedReadStream? bounded = null;
                try
                {

                    var isRegular = entry.Metadata.FileType is CpioFileType.Regular or CpioFileType.Socket or CpioFileType.Unknown;
                    var payloadLength = 0L;
                    Stream? payload = null;
                    if (isRegular)
                    {

                        if (entry.DataStream is { } owned) directStreams.Add(owned);
                        payloadLength = entry.DataLength;
                        ValidateInMemoryLength(entry, payloadLength);
                        if (entry.DataStream is { } direct)
                        {
                            if (direct.CanSeek) direct.Position = 0;
                            payload = direct;
                        }
                        else if (entry.DataStreamFactory is { } factory)
                        {
                            factoryStream = factory() ?? throw new InvalidOperationException($"Entry '{entry.Name}' stream factory returned null.");
                            probeFactoryEof = true;
                            payload = factoryStream;
                        }
                        else
                        {
                            factoryStream = new MemoryStream(entry.Data, writable: false);
                            payload = factoryStream;
                        }
                    }

                    if (payload is not null) bounded = new LengthLimitedReadStream(payload, payloadLength, leaveOpen: true, throwOnEarlyEnd: true);
                    writer.WriteEntry(CreateTarEntry(entry, format, bounded));
                }
                catch (Exception ex)
                {
                    primary = ex;
                }
                finally
                {
                    DisposePreserving(ref primary, bounded);
                    if (primary is null && probeFactoryEof && factoryStream is not null)
                    {
                        try
                        {
                            if (factoryStream.ReadByte() != -1)
                            {
                                primary = new InvalidDataException($"Entry '{entry.Name}' source contains data beyond its declared length.");
                            }
                        }
                        catch (Exception ex) { primary = ex; }
                    }
                    DisposePreserving(ref primary, factoryStream);
                }

                if (primary is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
                }
            }
        }
        catch (Exception ex)
        {
            primary ??= ex;
        }
        finally
        {
            foreach (var direct in directStreams) DisposePreserving(ref primary, direct);
        }

        if (primary is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
    }

    private static void ValidateInMemoryLength(ArchiveEntry entry, long payloadLength)
    {
        if (entry.DataStream is null && entry.DataStreamFactory is null
            && entry.DeclaredDataLength.HasValue && payloadLength != entry.Data.LongLength)
        {
            throw new ArchiveCorruptedException($"Entry '{entry.Name}' declared length {payloadLength} does not match its data length {entry.Data.LongLength}.");
        }
    }

    private static void DisposePreserving(ref Exception? primary, IDisposable? disposable)
    {
        if (disposable is null) return;
        try { disposable.Dispose(); }
        catch (Exception ex) { primary = primary is null ? ex : new AggregateException(primary, ex); }
    }

    private static TarEntry CreateTarEntry(ArchiveEntry entry, ArchiveFormat format, Stream? payload)
    {
        var entryType = entry.Metadata.FileType switch
        {
            CpioFileType.Directory => TarEntryType.Directory,
            CpioFileType.SymbolicLink => TarEntryType.SymbolicLink,
            CpioFileType.HardLink => TarEntryType.HardLink,
            CpioFileType.CharacterDevice => TarEntryType.CharacterDevice,
            CpioFileType.BlockDevice => TarEntryType.BlockDevice,
            CpioFileType.Fifo => TarEntryType.Fifo,
            _ => TarEntryType.RegularFile,
        };
        var name = entryType == TarEntryType.Directory && !entry.Name.EndsWith("/", StringComparison.Ordinal) ? entry.Name + "/" : entry.Name;
        var tarEntry = format == ArchiveFormat.Tar
            ? new PaxTarEntry(entryType, name, BuildPaxExtendedAttributes(entry))
            : (TarEntry)new UstarTarEntry(entryType, name);
        tarEntry.Mode = (UnixFileMode)(entry.Metadata.UnixPermissions & 0x0FFF);
        tarEntry.Uid = checked((int)entry.Metadata.UserId);
        tarEntry.Gid = checked((int)entry.Metadata.GroupId);
        if (entry.Metadata.ModificationTimeUnixSeconds > 0) tarEntry.ModificationTime = DateTimeOffset.FromUnixTimeSeconds(entry.Metadata.ModificationTimeUnixSeconds);
        if (entryType is TarEntryType.SymbolicLink or TarEntryType.HardLink) tarEntry.LinkName = entry.LinkName ?? string.Empty;
        if (entryType == TarEntryType.RegularFile) tarEntry.DataStream = payload;
        return tarEntry;
    }

    private static IEnumerable<KeyValuePair<string, string>> BuildPaxExtendedAttributes(ArchiveEntry entry)
    {
        if (entry.Metadata.ExtendedAttributes.Count == 0) return [];
        return entry.Metadata.ExtendedAttributes.Select(pair => new KeyValuePair<string, string>(
            pair.Key.StartsWith("SCHILY.xattr.", StringComparison.Ordinal) ? pair.Key : "SCHILY.xattr." + pair.Key,
            Encoding.UTF8.GetString(pair.Value)));
    }
}