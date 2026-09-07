using System.Formats.Tar;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Model;

namespace UotanStudio.Firmware.BoundedContexts.Archive.Cpio.Formats.Tar;

public sealed class TarArchiveWriter
{
    /// <summary>
    /// Writes all supplied entries to a TAR stream.
    /// <para>将所有提供的条目写入 TAR 流。</para>
    /// </summary>
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
                DeclaredLengthReadStream? bounded = null;
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

                    if (payload is not null) bounded = new DeclaredLengthReadStream(payload, payloadLength, leaveOpen: true);
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
            throw new InvalidDataException($"Entry '{entry.Name}' declared length {payloadLength} does not match its data length {entry.Data.LongLength}.");
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
