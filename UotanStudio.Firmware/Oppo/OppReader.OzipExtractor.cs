using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UotanStudio.Firmware.Oppo.Crypto;
using UotanStudio.Firmware.Oppo.Helpers;
using UotanStudio.Firmware.Oppo.Models;

namespace UotanStudio.Firmware.Oppo;

public sealed partial class OppReader
{
    private OppExtractResult ExtractOzipCore(
        Stream stream,
        OppArchive archive,
        string outputDir,
        bool overwrite,
        string? keyId,
        IProgress<OppExtractProgress>? progress)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"UotanStudio.Oppo.Ozip.{Guid.NewGuid():N}.zip");
        var extractedCount = 0;
        var completed = 0;
        var total = archive.Entries.Count;

        try
        {
            // If the single fallback encrypted entry is present (decryption during
            // parsing failed and no inner ZIP layout is known), decrypt the whole
            // file into a single "decrypted.zip" output.
            if (archive.Entries.Count == 1 && archive.Entries[0].EncryptedSize > 0)
            {
                var fallbackPath = Path.Combine(outputDir, "decrypted.zip");
                using (var fs = new FileStream(fallbackPath, FileMode.Create, FileAccess.Write, FileShare.None, DefaultBufferSize, FileOptions.SequentialScan))
                {
                    OzipDecryptor.DecryptFileWithAutoDetection(stream, fs);
                }

                return new OppExtractResult
                {
                    ExtractedFileCount = 1,
                    OutputDirectory = outputDir,
                    KeyId = keyId ?? archive.Metadata.KeyId
                };
            }

            // Decrypt the OZip file once into a temporary ZIP
            using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, DefaultBufferSize, FileOptions.SequentialScan))
            {
                OzipDecryptor.DecryptFileWithAutoDetection(stream, fs);
            }

            try
            {
                using var zip = ZipFile.OpenRead(tempFile);
                foreach (var entry in archive.Entries)
                {
                    progress?.Report(new OppExtractProgress(total, completed, entry.Name));

                    var sanitized = SanitizeFileName(entry.Name);
                    if (string.IsNullOrWhiteSpace(sanitized) || sanitized == "." || sanitized == "..")
                    {
                        sanitized = $"entry_{completed}";
                    }

                    var outputPath = Path.Combine(outputDir, sanitized);
                    ValidateOutputPath(outputDir, outputPath);

                    if (File.Exists(outputPath) && !overwrite)
                    {
                        completed++;
                        continue;
                    }

                    var zipEntry = zip.GetEntry(entry.Name);
                    if (zipEntry == null)
                    {
                        completed++;
                        continue;
                    }

                    var outputDirForEntry = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(outputDirForEntry) && !Directory.Exists(outputDirForEntry))
                    {
                        Directory.CreateDirectory(outputDirForEntry);
                    }

                    using var entryStream = zipEntry.Open();
                    using var outputStream = new FileStream(
                        outputPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        DefaultBufferSize,
                        FileOptions.SequentialScan);
                    entryStream.CopyTo(outputStream);

                    extractedCount++;
                    completed++;

                    progress?.Report(new OppExtractProgress(total, completed, entry.Name));
                }
            }
            catch (InvalidDataException)
            {
                // 中央目录损坏（EOCD 条目数与实际记录数不一致等）时 ZipArchive 无法打开，
                // 回退到按本地文件头偏移手动提取，保证解包出的仍是解密后 zip 内条目。
                // When the central directory is corrupt (EOCD entry count mismatching the
                // actual records) ZipArchive cannot open; fall back to manual extraction by
                // local-header offset so the unpacked files are still the decrypted ZIP's
                // inner entries.
                using var raw = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read, DefaultBufferSize, FileOptions.SequentialScan);
                foreach (var entry in archive.Entries)
                {
                    progress?.Report(new OppExtractProgress(total, completed, entry.Name));

                    var sanitized = SanitizeFileName(entry.Name);
                    if (string.IsNullOrWhiteSpace(sanitized) || sanitized == "." || sanitized == "..")
                    {
                        sanitized = $"entry_{completed}";
                    }

                    var outputPath = Path.Combine(outputDir, sanitized);
                    ValidateOutputPath(outputDir, outputPath);

                    if (File.Exists(outputPath) && !overwrite)
                    {
                        completed++;
                        continue;
                    }

                    if (entry.Offset <= 0)
                    {
                        // 中央目录扫描未提供本地文件头偏移（理论上不出现），跳过该条目。
                        // No local-header offset from the central-directory scan (should not
                        // happen); skip the entry.
                        completed++;
                        continue;
                    }

                    var outputDirForEntry = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(outputDirForEntry) && !Directory.Exists(outputDirForEntry))
                    {
                        Directory.CreateDirectory(outputDirForEntry);
                    }

                    using var outputStream = new FileStream(
                        outputPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        DefaultBufferSize,
                        FileOptions.SequentialScan);
                    ExtractZipEntryByLocalHeader(raw, entry, outputStream);

                    extractedCount++;
                    completed++;

                    progress?.Report(new OppExtractProgress(total, completed, entry.Name));
                }
            }

            return new OppExtractResult
            {
                ExtractedFileCount = extractedCount,
                OutputDirectory = outputDir,
                KeyId = keyId ?? archive.Metadata.KeyId
            };
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best effort */ }
        }
    }

}
