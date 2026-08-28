using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using UotanStudio.Firmware.Oppo.Crypto;
using UotanStudio.Firmware.Oppo.Crypto.KeyDatabase;
using UotanStudio.Firmware.Oppo.Helpers;
using UotanStudio.Firmware.Oppo.Models;

namespace UotanStudio.Firmware.Oppo.Parsers;

/// <summary>
/// <para>提供 OZip 加密固件格式的解析操作实现。</para>
/// Provides parsing operations for the OZip encrypted firmware format.
/// </summary>
/// <remarks>
/// <para>
/// OZip 文件是 Oppo 的加密固件格式，解密后通常为标准的 ZIP 文件。
/// 此解析器负责读取 OZip 头部信息、提取元数据，以及执行解密操作。
/// </para>
/// OZip files are Oppo's encrypted firmware format, typically resulting in a standard ZIP file
/// after decryption. This parser reads OZip header information, extracts metadata,
/// and performs decryption operations.
/// </remarks>
internal sealed class OzipFormatParser : IOppFormatParser
{
    /// <summary>
    /// <para>获取此解析器支持的固件格式。</para>
    /// Gets the firmware format supported by this parser.
    /// </summary>
    public OppFormat SupportedFormat => OppFormat.Ozip;

    /// <summary>
    /// <para>从流中解析 OZip 文件的元数据。</para>
    /// Parses metadata from an OZip file stream.
    /// </summary>
    public OppMetadata ParseMetadata(Stream stream, long fileSize, IOppCryptoProvider? cryptoProvider)
    {
        Guard.ThrowIfNull(stream);

        var originalPosition = stream.Position;
        _ = OzipHeader.ReadFrom(stream);

        try
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"UotanStudio.Oppo.Ozip.{Guid.NewGuid():N}.zip");

            try
            {
                stream.Position = 0;
                OzipKeyEntry detectedKey;
                using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, FileOptions.SequentialScan))
                {
                    detectedKey = OzipDecryptor.DecryptFileWithAutoDetection(stream, fs);
                }

                using var archive = ZipFile.OpenRead(tempFile);
                var entryCount = 0;
                foreach (var zipEntry in archive.Entries)
                {
                    if (zipEntry.Length == 0 && zipEntry.FullName.EndsWith("/"))
                    {
                        continue;
                    }

                    entryCount++;
                }

                return new OppMetadata
                {
                    FormatName = "OZip",
                    Format = OppFormat.Ozip,
                    FileSize = fileSize,
                    EntryCount = entryCount > 0 ? entryCount : 1,
                    KeyId = detectedKey.Description ?? detectedKey.KeyHex,
                    IsZipWrapped = false
                };
            }
            finally
            {
                try { File.Delete(tempFile); } catch { /* best effort */ }
            }
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
    /// <para>从流中解析条目列表。</para>
    /// Parses the entry list from a stream.
    /// </summary>
    public IReadOnlyList<OppEntry> ParseEntries(Stream stream, long fileSize, OppMetadata metadata, IOppCryptoProvider? cryptoProvider)
    {
        Guard.ThrowIfNull(stream);

        var header = OzipHeader.ReadFrom(stream);

        try
        {
            // Decrypt OZIP data to a temporary file to handle large files (>2GB)
            var tempFile = Path.Combine(Path.GetTempPath(), $"UotanStudio.Oppo.Ozip.{Guid.NewGuid():N}.zip");

            try
            {
                stream.Position = 0;
                using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, FileOptions.SequentialScan))
                {
                    OzipDecryptor.DecryptFileWithAutoDetection(stream, fs);
                }

                IReadOnlyList<OppEntry> entries;
                try
                {
                    entries = ReadZipEntries(tempFile);
                }
                catch (InvalidDataException)
                {
                    // .NET ZipArchive 无法打开（中央目录损坏/ZIP64 变体等）时，
                    // 回退到手动解析中央目录，仍能枚举出可读的真实条目。
                    // When .NET ZipArchive cannot open the file (corrupt central directory / ZIP64
                    // variants etc.), fall back to manually parsing the central directory so as many
                    // real entries as possible are still enumerated.
                    entries = ScanCentralDirectoryEntries(tempFile);
                }

                return entries.Count > 0
                    ? entries
                    : FallbackSingleEntry(header);
            }
            finally
            {
                try { File.Delete(tempFile); } catch { /* best effort */ }
            }
        }
        catch (OppDecryptionException)
        {
            // Decryption failed (no valid key found), fall back to single entry
            return FallbackSingleEntry(header);
        }
    }

    /// <summary>
    /// <para>用 <see cref="ZipArchive"/> 读取临时文件中的全部非目录条目。</para>
    /// Reads all non-directory entries of the temp ZIP file via <see cref="ZipArchive"/>.
    /// </summary>
    private static IReadOnlyList<OppEntry> ReadZipEntries(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        var entries = new List<OppEntry>(archive.Entries.Count);
        foreach (var zipEntry in archive.Entries)
        {
            // Skip directory entries
            if (zipEntry.Length == 0 && zipEntry.FullName.EndsWith("/"))
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
    /// <para>容错解析：从文件尾部定位 EOCD 并手工遍历中央目录（PK\x01\x02 记录），
    /// 遇到损坏记录即停止，返回已解析出的条目。</para>
    /// Tolerant parsing: locates the EOCD at the file tail and walks the central directory
    /// (PK\x01\x02 records) by hand, stopping at the first corrupt record and returning the entries
    /// parsed so far.
    /// </summary>
    private static IReadOnlyList<OppEntry> ScanCentralDirectoryEntries(string zipPath)
    {
        using var fs = File.OpenRead(zipPath);
        if (fs.Length < 22)
        {
            return [];
        }

        // 在尾部 64KB 内反向查找 EOCD 签名 PK\x05\x06。
        // Search backwards for the EOCD signature PK\x05\x06 within the last 64KB.
        var tailLen = (int)Math.Min(fs.Length, 65536);
        var tail = new byte[tailLen];
        fs.Position = fs.Length - tailLen;
        fs.ReadExactly(tail, 0, tailLen);

        var eocdOffset = -1L;
        for (var i = tailLen - 22; i >= 0; i--)
        {
            if (tail[i] == 0x50 && tail[i + 1] == 0x4B && tail[i + 2] == 0x05 && tail[i + 3] == 0x06)
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

        var cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(eocd[16..]);
        var cdSize = BinaryPrimitives.ReadUInt32LittleEndian(eocd[12..]);
        if (cdOffset > fs.Length || cdSize > fs.Length - cdOffset)
        {
            return [];
        }

        fs.Position = cdOffset;
        var cd = new byte[cdSize];
        fs.ReadExactly(cd, 0, cd.Length);

        var entries = new List<OppEntry>();
        var pos = 0;
        while (pos + 46 <= cd.Length)
        {
            // 中央目录记录签名 PK\x01\x02。
            // Central directory record signature PK\x01\x02.
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
                // 损坏记录：停止解析，保留已解析条目。
                // Corrupt record: stop parsing, keep the entries already parsed.
                break;
            }

            var name = Encoding.UTF8.GetString(cd, pos + 46, nameLen);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(cd.AsSpan(pos + 24));
            entries.Add(new OppEntry
            {
                Name = name,
                Size = size,
                EncryptedSize = 0,
                IsCompressed = false,
                PartitionType = "ozip",
                FileName = Path.GetFileName(name),
            });

            pos += recordLen;
        }

        return entries;
    }

    /// <summary>
    /// <para>提取单个条目到输出流（解密 OZip 文件）。</para>
    /// Extracts a single entry to an output stream (decrypts the OZip file).
    /// </summary>
    public void ExtractEntry(Stream stream, Stream output, OppEntry entry, OppMetadata metadata, string? keyId, IOppCryptoProvider? cryptoProvider)
    {
        Guard.ThrowIfNull(stream);
        Guard.ThrowIfNull(output);

        // Decrypt the whole OZip file with auto-detection.
        // Note: the main extraction path in OppReader handles OZip specially by
        // decrypting once and then copying each inner ZIP entry.
        OzipDecryptor.DecryptFileWithAutoDetection(stream, output);
    }

    /// <summary>
    /// <para>创建解密失败时的回退单条目列表。</para>
    /// Creates a fallback single-entry list when decryption fails.
    /// </summary>
    /// <param name="header"><para>OZip 文件头部。</para>The OZip file header.</param>
    /// <returns><para>包含单个 "decrypted.zip" 条目的列表。</para>A list containing a single "decrypted.zip" entry.</returns>
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