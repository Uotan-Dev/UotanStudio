using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.VendorFirmware.Oppo.Crypto;
using UotanStudio.Firmware.VendorFirmware.Oppo.Exceptions;
using UotanStudio.Firmware.VendorFirmware.Oppo.Models;

namespace UotanStudio.Firmware.VendorFirmware.Oppo.Parsers;

internal sealed class OzipFormatParser : IOppFormatParser
{
    private const int MaxCentralDirectoryBytes = 64 * 1024 * 1024;

    public OppFormat SupportedFormat => OppFormat.Ozip;

    /// <summary>
    /// Parses metadata from an OZip file stream.
    /// <para>从流中解析 OZip 文件的元数据。</para>
    /// </summary>
    public OppMetadata ParseMetadata(Stream stream, long fileSize, IOppCryptoProvider? cryptoProvider)
    {
        Guard.ThrowIfNull(stream);

        var originalPosition = stream.Position;
        _ = OzipHeader.ReadFrom(stream);

        try
        {
            using var decrypted = OzipDecryptor.OpenDecryptedStream(stream, out var detectedKey, leaveOpen: true);
            var entries = ReadZipEntriesWithFallback(decrypted);

            return new OppMetadata
            {
                FormatName = "OZip",
                Format = OppFormat.Ozip,
                FileSize = fileSize,
                EntryCount = entries.Count > 0 ? entries.Count : 1,
                KeyId = detectedKey.Description ?? detectedKey.KeyHex,
                IsZipWrapped = false
            };
        }
        catch (OppDecryptionException)
        {
            return new OppMetadata
            {
                FormatName = "OZip",
                Format = OppFormat.Ozip,
                FileSize = fileSize,
                EntryCount = 1,
                KeyId = null,
                IsZipWrapped = false
            };
        }
        catch (InvalidDataException)
        {
            return new OppMetadata
            {
                FormatName = "OZip",
                Format = OppFormat.Ozip,
                FileSize = fileSize,
                EntryCount = 1,
                KeyId = null,
                IsZipWrapped = false
            };
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    /// <summary>
    /// Parses the entry list from a stream.
    /// <para>从流中解析条目列表。</para>
    /// </summary>
    public IReadOnlyList<OppEntry> ParseEntries(Stream stream, long fileSize, OppMetadata metadata, IOppCryptoProvider? cryptoProvider)
    {
        Guard.ThrowIfNull(stream);

        var originalPosition = stream.Position;
        var header = OzipHeader.ReadFrom(stream);

        try
        {
            using var decrypted = OzipDecryptor.OpenDecryptedStream(stream, out _, leaveOpen: true);
            var entries = ReadZipEntriesWithFallback(decrypted);
            return entries.Count > 0 ? entries : FallbackSingleEntry(header);
        }
        catch (OppDecryptionException)
        {

            return FallbackSingleEntry(header);
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    /// <summary>
    /// Reads all non-directory entries of the decrypted ZIP view via ZipArchive; when a corrupt central directory (e.g.
    /// <para>用 ZipArchive 读取解密视图中的全部非目录条目；中央目录损坏 （EOCD 条目数与实际记录数不一致等）导致 InvalidDataException 时， 回退到手动解析中央目录，保证元数据条目数与条目列表来自同一来源。</para>
    /// </summary>
    private static IReadOnlyList<OppEntry> ReadZipEntriesWithFallback(Stream zipStream)
    {
        try
        {
            zipStream.Position = 0;
            return ReadZipEntries(zipStream);
        }
        catch (InvalidDataException)
        {


            zipStream.Position = 0;
            return ScanCentralDirectoryEntries(zipStream);
        }
    }

    /// <summary>
    /// Reads all non-directory entries from the decrypted ZIP view via ZipArchive.
    /// <para>用 ZipArchive 读取解密 ZIP 视图中的全部非目录条目。</para>
    /// </summary>
    private static IReadOnlyList<OppEntry> ReadZipEntries(Stream zipStream)
    {
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

        var entries = new List<OppEntry>(archive.Entries.Count);
        foreach (var zipEntry in archive.Entries)
        {

            if (zipEntry.Length == 0 && zipEntry.FullName.EndsWith('/'))
            {
                continue;
            }

            entries.Add(new OppEntry
            {
                Name = zipEntry.FullName,
                Size = zipEntry.Length,
                EncryptedSize = 0,
                IsCompressed = zipEntry.CompressedLength < zipEntry.Length,
                PartitionType = "ozip",
                FileName = zipEntry.Name,
            });
        }

        return entries;
    }

    /// <summary>
    /// Tolerant parsing: locates the EOCD at the file tail and walks the central directory (PK\x01\x02 records) by hand, stopping at the first corrupt record and returning the entries parsed so far.
    /// <para>容错解析：从文件尾部定位 EOCD 并手工遍历中央目录（PK\x01\x02 记录）， 遇到损坏记录即停止，返回已解析出的条目。</para>
    /// </summary>
    private static IReadOnlyList<OppEntry> ScanCentralDirectoryEntries(Stream fs)
    {
        if (fs.Length < 22)
        {
            return [];
        }


        var tailLen = (int)Math.Min(fs.Length, 65557);
        var tail = new byte[tailLen];
        fs.Position = fs.Length - tailLen;
        fs.ReadExactly(tail, 0, tailLen);

        var eocdOffset = -1L;
        for (var i = tailLen - 22; i >= 0; i--)
        {
            if (tail[i] == 0x50 && tail[i + 1] == 0x4B && tail[i + 2] == 0x05 && tail[i + 3] == 0x06
                && i + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) == tailLen)
            {
                eocdOffset = fs.Length - tailLen + i;
                break;
            }
        }

        if (eocdOffset < 0)
        {
            return [];
        }

        fs.Position = eocdOffset;
        Span<byte> eocd = stackalloc byte[22];
        fs.ReadExactly(eocd);

        var cdOffset32 = BinaryPrimitives.ReadUInt32LittleEndian(eocd[16..]);
        var cdSize32 = BinaryPrimitives.ReadUInt32LittleEndian(eocd[12..]);

        if (cdOffset32 == uint.MaxValue || cdSize32 == uint.MaxValue
            || cdOffset32 > fs.Length || cdSize32 > fs.Length - cdOffset32
            || cdSize32 > MaxCentralDirectoryBytes)
        {
            return [];
        }

        fs.Position = cdOffset32;
        var cd = new byte[(int)cdSize32];
        fs.ReadExactly(cd, 0, cd.Length);

        var entries = new List<OppEntry>();
        var pos = 0;
        while (pos + 46 <= cd.Length)
        {


            if (cd[pos] != 0x50 || cd[pos + 1] != 0x4B || cd[pos + 2] != 0x01 || cd[pos + 3] != 0x02)
            {
                break;
            }

            var nameLen = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(pos + 28));
            var extraLen = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(pos + 30));
            var commentLen = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(pos + 32));
            var recordLen = 46 + nameLen + extraLen + commentLen;
            if (pos + recordLen > cd.Length)
            {
                break;
            }

            var name = Encoding.UTF8.GetString(cd, pos + 46, nameLen);


            var compressionMethod = BinaryPrimitives.ReadUInt16LittleEndian(cd.AsSpan(pos + 10));
            var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(pos + 20));
            var localHeaderOffset = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(pos + 42));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(pos + 24));


            if (!IsValidLocalHeaderOffset(fs, localHeaderOffset))
            {
                break;
            }

            entries.Add(new OppEntry
            {
                Name = name,
                Size = size,
                CompressedSize = compressedSize,
                EncryptedSize = 0,
                IsCompressed = compressionMethod != 0,
                PartitionType = "ozip",
                FileName = Path.GetFileName(name),
                Offset = localHeaderOffset,
            });

            pos += recordLen;
        }

        return entries;
    }

    /// <summary>
    /// Checks whether the given offset holds a valid ZIP local file header (PK\x03\x04) and lies within the file.
    /// <para>检查指定偏移处是否为有效的 zip 本地文件头（PK\x03\x04），且偏移在文件范围内。</para>
    /// </summary>
    private static bool IsValidLocalHeaderOffset(Stream fs, long offset)
    {
        if (offset < 0 || offset + 4 > fs.Length)
        {
            return false;
        }

        Span<byte> magic = stackalloc byte[4];
        fs.Position = offset;
        if (fs.Read(magic) != 4)
        {
            return false;
        }

        return magic[0] == 0x50 && magic[1] == 0x4B && magic[2] == 0x03 && magic[3] == 0x04;
    }

    /// <summary>
    /// Extracts a single entry to an output stream (decrypts the OZip file).
    /// <para>提取单个条目到输出流（解密 OZip 文件）。</para>
    /// </summary>
    public void ExtractEntry(Stream stream, Stream output, OppEntry entry, OppMetadata metadata, string? keyId, IOppCryptoProvider? cryptoProvider)
    {
        Guard.ThrowIfNull(stream);
        Guard.ThrowIfNull(output);


        OzipDecryptor.DecryptFileWithAutoDetection(stream, output);
    }

    /// <summary>
    /// Creates a fallback single-entry list when decryption fails.
    /// <para>创建解密失败时的回退单条目列表。</para>
    /// </summary>
    private static IReadOnlyList<OppEntry> FallbackSingleEntry(OzipHeader header)
    {
        var entry = new OppEntry
        {
            Name = "decrypted.zip",
            Offset = OzipHeader.DataOffset,
            Size = header.EncryptedDataSize,
            EncryptedSize = header.EncryptedDataSize,
            IsCompressed = false,
            PartitionType = "ozip",
        };

        return [entry];
    }
}