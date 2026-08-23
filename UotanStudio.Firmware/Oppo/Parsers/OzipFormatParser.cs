using System.IO.Compression;
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

                using var archive = ZipFile.OpenRead(tempFile);

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
        catch (InvalidDataException)
        {
            // Decrypted data is not a valid ZIP archive, fall back to single entry
            return FallbackSingleEntry(header);
        }
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